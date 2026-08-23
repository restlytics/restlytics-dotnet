using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Restlytics.AspNetCore;

/// <summary>Fail-closed privacy boundary shared by every instrumentation path.</summary>
internal static class Redaction
{
    private static readonly HashSet<string> SensitiveSegments = new(StringComparer.Ordinal)
    {
        "authorization", "auth", "cookie", "cookies", "setcookie", "password", "passwd",
        "secret", "token", "accesstoken", "refreshtoken", "apikey", "credential",
        "credentials", "body", "payload", "form", "stack", "stacktrace", "log",
    };

    internal static bool IsSensitiveAttributeKey(string key)
    {
        string normalized = key.Trim().ToLowerInvariant().Replace('-', '.').Replace('_', '.');
        if (normalized is "http.request.method" or "http.response.status.code" or "restlytics.bindings.count")
        {
            return false;
        }

        return normalized.Split('.').Any(SensitiveSegments.Contains);
    }

    /// <summary>
    /// Remove credentials/fragments and redact every query value. redactKeys is
    /// retained for configuration compatibility; unknown keys are equally safe.
    /// </summary>
    internal static string Url(Uri uri, IReadOnlyList<string>? redactKeys = null)
    {
        _ = redactKeys;
        try
        {
            var safe = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Fragment = string.Empty };
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parsed =
                QueryHelpers.ParseQuery(uri.Query);
            var query = new StringBuilder();
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in parsed)
            {
                foreach (string? _value in pair.Value)
                {
                    query.Append(query.Length == 0 ? '?' : '&');
                    query.Append(Uri.EscapeDataString(pair.Key));
                    query.Append("=REDACTED");
                }
            }

            safe.Query = query.Length > 0 ? query.ToString(1, query.Length - 1) : string.Empty;
            return safe.Uri.AbsoluteUri;
        }
        catch
        {
            return uri.GetLeftPart(UriPartial.Path);
        }
    }

    /// <summary>Exception content is intentionally omitted; Restlytics is not a crash tracker.</summary>
    internal static string? ExceptionMessage(string? message)
    {
        _ = message;
        return null;
    }
}
