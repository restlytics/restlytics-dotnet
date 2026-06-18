using System.Text.RegularExpressions;

namespace Restlytics.AspNetCore;

/// <summary>
/// SQL normalization → a literal-free template string.
///
/// Two jobs:
///  1. PII / redaction — strip every literal so we NEVER ship customer values
///     (emails, tokens, ids) inside <c>db.query.summary</c>. Only the shape survives.
///  2. N+1 grouping — collapse the query down to a stable fingerprint so that
///     <c>SELECT * FROM users WHERE id = 1</c> and <c>... id = 2</c> map to the same key.
///     <c>IN (?, ?, ?)</c> lists of varying length also collapse to <c>IN (?)</c> so a
///     batched query and its single-row cousin don't fragment the grouping.
///
/// This is deliberately a best-effort lexical normalizer, not a real SQL parser —
/// it must be fast (runs on every query) and never throw.
/// </summary>
internal static partial class Sql
{
    // Single- and double-quoted string literals, with escaped-quote support.
    [GeneratedRegex(@"'(?:[^'\\]|\\.|'')*'", RegexOptions.Singleline)]
    private static partial Regex SingleQuoted();

    [GeneratedRegex(@"""(?:[^""\\]|\\.|"""")*""", RegexOptions.Singleline)]
    private static partial Regex DoubleQuoted();

    // Numeric literals: hex, decimal/scientific, then plain integers. Word
    // boundaries keep identifiers like `column2` intact.
    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b")]
    private static partial Regex HexLiteral();

    [GeneratedRegex(@"\b\d+\.\d+(?:[eE][+-]?\d+)?\b")]
    private static partial Regex DecimalLiteral();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex IntLiteral();

    // Existing positional/named placeholders all normalize to `?`.
    [GeneratedRegex(@"\?\d+")]
    private static partial Regex NumberedPlaceholder();

    [GeneratedRegex(@"[:$]\w+")]
    private static partial Regex NamedPlaceholder();

    // Collapse `IN (?, ?, ?)` → `IN (?)` so list length doesn't fragment groups.
    [GeneratedRegex(@"\bin\s*\(\s*\?(?:\s*,\s*\?)*\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex InList();

    // Collapse multi-row VALUES tuples: (?, ?), (?, ?) → (?)
    [GeneratedRegex(@"\(\s*\?(?:\s*,\s*\?)*\s*\)(?:\s*,\s*\(\s*\?(?:\s*,\s*\?)*\s*\))+")]
    private static partial Regex ValuesTuples();

    [GeneratedRegex(@"\(\s*\?(?:\s*,\s*\?)+\s*\)")]
    private static partial Regex SingleTuple();

    // Whitespace runs (incl. newlines) → single space.
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Normalize a raw SQL string into a stable, literal-free template.</summary>
    public static string Normalize(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return string.Empty;
        }

        string s = sql;

        // Drop string literals, replacing with `?` so they read like bindings.
        s = SingleQuoted().Replace(s, "?");
        s = DoubleQuoted().Replace(s, "?");

        // Drop numeric literals (hex first, then decimal, then plain ints).
        s = HexLiteral().Replace(s, "?");
        s = DecimalLiteral().Replace(s, "?");
        s = IntLiteral().Replace(s, "?");

        // Normalize existing placeholders.
        s = NumberedPlaceholder().Replace(s, "?");   // ?1, ?2 (some drivers)
        s = NamedPlaceholder().Replace(s, "?");      // :name, $1

        // Collapse IN lists and VALUES tuples.
        s = InList().Replace(s, "IN (?)");
        s = ValuesTuples().Replace(s, "(?)");
        s = SingleTuple().Replace(s, "(?)");

        // Squash whitespace, trim, lowercase.
        s = Whitespace().Replace(s, " ");
        s = s.Trim();

        return s.ToLowerInvariant();
    }
}
