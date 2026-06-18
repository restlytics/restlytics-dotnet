using System;
using System.Security.Cryptography;

namespace Restlytics.AspNetCore;

/// <summary>
/// Trace / span id generation and W3C <c>traceparent</c> handling.
///
/// OTLP/JSON wants lowercase-hex ids: 32 chars (16 bytes) for a trace id,
/// 16 chars (8 bytes) for a span id. The ingestion contract additionally
/// rejects all-zero ids, so we make sure the random bytes are never empty.
/// </summary>
internal static class Ids
{
    /// <summary>32 lowercase hex chars (16 random bytes), never all-zero.</summary>
    public static string TraceId() => RandomHex(16);

    /// <summary>16 lowercase hex chars (8 random bytes), never all-zero.</summary>
    public static string SpanId() => RandomHex(8);

    private static string RandomHex(int byteCount)
    {
        // RandomNumberGenerator is cryptographically secure. The all-zero
        // probability is negligible, but the contract forbids it, so guard.
        Span<byte> buffer = stackalloc byte[byteCount];
        string hex;
        do
        {
            RandomNumberGenerator.Fill(buffer);
            // Convert.ToHexString yields UPPERCASE; OTLP requires lowercase hex.
            // (Convert.ToHexStringLower is .NET 9+, so we lowercase explicitly here.)
            hex = Convert.ToHexString(buffer).ToLowerInvariant();
        }
        while (IsAllZero(hex));

        return hex;
    }

    private static bool IsAllZero(string hex)
    {
        foreach (char c in hex)
        {
            if (c != '0')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parsed W3C <c>traceparent</c> result: the continued trace id, the upstream
    /// span id (which becomes the root span's parent), and the upstream sampled flag.
    /// </summary>
    public readonly record struct Traceparent(string TraceId, string ParentSpanId, bool Sampled);

    /// <summary>
    /// Parse a W3C <c>traceparent</c> header into its parts.
    ///
    /// Format: <c>version-traceid-spanid-flags</c>, e.g.
    ///   <c>00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01</c>
    ///
    /// Returns <c>null</c> when absent or malformed so the caller falls back to a
    /// fresh trace. Continuing an incoming traceparent lets a single distributed
    /// trace stitch together across services (e.g. an upstream gateway → this app).
    /// </summary>
    public static Traceparent? ParseTraceparent(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        string value = header.Trim().ToLowerInvariant();

        // 00-<32hex>-<16hex>-<2hex> → 2 + 1 + 32 + 1 + 16 + 1 + 2 = 55 chars.
        if (value.Length != 55 || value[2] != '-' || value[35] != '-' || value[52] != '-')
        {
            return null;
        }

        string version = value.Substring(0, 2);
        string traceId = value.Substring(3, 32);
        string parentSpanId = value.Substring(36, 16);
        string flags = value.Substring(53, 2);

        if (!IsHex(version) || !IsHex(traceId) || !IsHex(parentSpanId) || !IsHex(flags))
        {
            return null;
        }

        // Reject the invalid all-zero trace/parent ids per the W3C spec.
        if (IsAllZero(traceId) || IsAllZero(parentSpanId))
        {
            return null;
        }

        // Low bit of the flags byte is the "sampled" flag.
        bool sampled = (HexByte(flags) & 0x01) == 0x01;

        return new Traceparent(traceId, parentSpanId, sampled);
    }

    /// <summary>Build a W3C <c>traceparent</c> value for outbound injection (optional).</summary>
    public static string Format(string traceId, string spanId, bool sampled)
        => $"00-{traceId}-{spanId}-{(sampled ? "01" : "00")}";

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static int HexByte(string twoHex)
        => Convert.ToInt32(twoHex, 16);
}
