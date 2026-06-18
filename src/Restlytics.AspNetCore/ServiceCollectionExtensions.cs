using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Restlytics.AspNetCore;

/// <summary>
/// Registration + pipeline wiring for the restlytics ASP.NET Core SDK.
///
/// Typical usage:
/// <code>
/// builder.Services.AddRestlytics(builder.Configuration);
/// // ...
/// app.UseRestlytics();
/// </code>
///
/// Config resolution order: <c>appsettings.json</c> "Restlytics" section, then
/// <c>RESTLYTICS_*</c> environment variables (which win), then an optional code
/// override. Until a key is configured the SDK stays inert.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register restlytics services: options, the singleton <see cref="Tracer"/>, the
    /// transport, the middleware, the outbound HTTP handler, and (if EF Core is
    /// referenced) the DB interceptor.
    /// </summary>
    public static IServiceCollection AddRestlytics(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<RestlyticsOptions>? configure = null)
    {
        var options = new RestlyticsOptions();

        // 1. appsettings.json "Restlytics" section.
        configuration?.GetSection("Restlytics").Bind(options);

        // 2. RESTLYTICS_* environment variables override appsettings.
        options.ApplyEnvironment(Environment.GetEnvironmentVariable);

        // 3. Explicit code override (highest precedence).
        configure?.Invoke(options);

        services.TryAddSingleton(options);

        services.TryAddSingleton<ITransport>(_ => BuildTransport(options));

        services.TryAddSingleton(sp =>
            new Tracer(
                transport: sp.GetRequiredService<ITransport>(),
                serviceName: options.ServiceName,
                environment: options.Environment,
                sampleRate: options.SampleRate,
                maxSpans: options.MaxSpans));

        // IMiddleware must be registered in the container (factory-activated).
        services.TryAddSingleton(sp =>
            new RestlyticsMiddleware(
                sp.GetRequiredService<Tracer>(),
                sp.GetRequiredService<RestlyticsOptions>()));

        // The outbound HTTP handler is transient (one per HttpClient pipeline).
        services.TryAddTransient(sp =>
            new RestlyticsHttpHandler(
                sp.GetRequiredService<Tracer>(),
                sp.GetRequiredService<RestlyticsOptions>()));

#if RESTLYTICS_EFCORE
        services.TryAddSingleton(sp =>
            new RestlyticsDbInterceptor(
                sp.GetRequiredService<Tracer>(),
                sp.GetRequiredService<RestlyticsOptions>()));
#endif

        return services;
    }

    /// <summary>
    /// Insert the restlytics middleware into the pipeline. Call this EARLY (right
    /// after exception handling) so the SERVER span brackets the whole request, but
    /// AFTER <c>UseRouting()</c> isn't required — the route template is read at the
    /// end of the pipeline once the endpoint is resolved.
    ///
    /// When the key is unconfigured this is a no-op, so it's safe to leave in.
    /// </summary>
    public static IApplicationBuilder UseRestlytics(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService(typeof(RestlyticsOptions)) as RestlyticsOptions;
        if (options is null || !options.Enabled)
        {
            // No key → install nothing. The package stays inert.
            return app;
        }

        return app.UseMiddleware<RestlyticsMiddleware>();
    }

    private static ITransport BuildTransport(RestlyticsOptions options)
    {
        return options.Transport.Trim().ToLowerInvariant() switch
        {
            "null" or "none" => new NullTransport(),
            _ => new HttpTransport(
                ingestUrl: options.IngestUrl,
                key: options.Key,
                timeoutMs: options.TimeoutMs),
        };
    }
}
