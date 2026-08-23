using System;
using System.Text.Json;
using Restlytics.AspNetCore;
using Xunit;

namespace Restlytics.Tests;

public class RedactionTests
{
    [Fact]
    public void UrlRemovesCredentialsFragmentAndEveryQueryValue()
    {
        string value = Redaction.Url(
            new Uri("https://alice:password@example.test/orders?token=abc&unknown=customer-secret#raw"),
            new[] { "token" });

        foreach (string secret in new[] { "alice", "password", "abc", "customer-secret", "raw" })
        {
            Assert.DoesNotContain(secret, value);
        }
    }

    [Fact]
    public void SpanBoundaryDropsContentBearingFields()
    {
        var span = new SpanBuilder(new string('a', 32), new string('b', 16), null, "GET /users/{id}", 2, 1, 2);
        span.SetString("http.request.method", "GET")
            .SetString("http.request.header.authorization", "Bearer abc.def.ghi")
            .SetString("aspnetcore.request.body", "password=hunter2")
            .SetString("log.body", "alice@example.test")
            .SetString("url.full", "https://example.test/?unknown=customer-secret")
            .SetStatus(SpanStatus.Error, "login failed for alice@example.test password=hunter2");

        OtlpSpan payload = span.ToOtlp();
        string encoded = JsonSerializer.Serialize(payload);
        foreach (string secret in new[] { "hunter2", "alice@example.test", "customer-secret", "authorization" })
        {
            Assert.DoesNotContain(secret, encoded);
        }

        Assert.Null(payload.Status?.Message);
        Assert.True(Redaction.IsSensitiveAttributeKey("razor.request.payload"));
        Assert.False(Redaction.IsSensitiveAttributeKey("restlytics.bindings_count"));
    }
}
