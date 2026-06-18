#if RESTLYTICS_EFCORE
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Restlytics.AspNetCore;

/// <summary>
/// EF Core DB instrumentation: turns each executed command into a CLIENT span.
///
/// EF's <see cref="DbCommandInterceptor"/> gives us both the start
/// (<c>*ExecutingAsync</c>/<c>*Executing</c>) and the elapsed duration
/// (<c>eventData.Duration</c>) on the <c>*Executed</c>/<c>*Reader</c> hooks, so we
/// can back-date the start precisely. We hook the three executed variants
/// (reader/scalar/non-query) plus their async counterparts.
///
/// Redaction: we send only the NORMALIZED statement (<c>db.query.summary</c>) — a
/// literal-free template that doubles as the N+1 grouping key — plus a binding
/// COUNT. Raw text (<c>db.query.text</c>) is sent only when <c>CaptureSql</c> is on,
/// capped at 2048 chars. Parameter VALUES are NEVER sent.
///
/// This type is compiled only when the optional
/// <c>Microsoft.EntityFrameworkCore.Relational</c> package is referenced
/// (the <c>RESTLYTICS_EFCORE</c> compile constant is defined by the .csproj then).
/// </summary>
public sealed class RestlyticsDbInterceptor : DbCommandInterceptor
{
    private readonly Tracer _tracer;
    private readonly RestlyticsOptions _options;

    internal RestlyticsDbInterceptor(Tracer tracer, RestlyticsOptions options)
    {
        _tracer = tracer;
        _options = options;
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        Record(command, eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        Record(command, eventData);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        Record(command, eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command, CommandExecutedEventData eventData)
    {
        try
        {
            if (!_options.InstrumentDb)
            {
                return;
            }

            RequestState? state = _tracer.Current;
            if (state is null || !state.Sampled || state.RootSpan is null)
            {
                return;
            }

            state.DbQueryCount++;

            // eventData.Duration is the elapsed time of the command. Back-date start.
            long endNs = state.NowNs();
            long startNs = endNs - (long)eventData.Duration.TotalMilliseconds * 1_000_000L;

            string summary = Sql.Normalize(command.CommandText);

            SpanBuilder? span = state.AddChild("db.query", startNs, endNs);
            if (span is null)
            {
                return;
            }

            span.SetString("db.system.name", DbSystem(command));
            span.SetString("db.query.summary", summary);
            span.SetInt("restlytics.bindings_count", command.Parameters.Count);
            span.SetString("restlytics.category", "db");

            // A short, human-readable span name from the leading SQL keyword.
            string keyword = LeadingKeyword(summary);
            if (keyword.Length > 0)
            {
                span.SetName("db " + keyword);
            }

            if (_options.CaptureSql)
            {
                // Raw text may carry PII; cap hard at 2048 chars (contract max).
                string text = command.CommandText ?? string.Empty;
                if (text.Length > 2048)
                {
                    text = text.Substring(0, 2048);
                }

                span.SetString("db.query.text", text);
            }
        }
        catch
        {
            // DB instrumentation must never break the query path.
        }
    }

    /// <summary>Best-effort db.system.name from the provider's connection type.</summary>
    private static string DbSystem(DbCommand command)
    {
        string type = command.Connection?.GetType().Name ?? string.Empty;
        return type switch
        {
            "NpgsqlConnection" => "postgresql",
            "SqlConnection" => "mssql",
            "MySqlConnection" => "mysql",
            "SqliteConnection" => "sqlite",
            _ => "other",
        };
    }

    private static string LeadingKeyword(string summary)
    {
        int i = 0;
        while (i < summary.Length && char.IsWhiteSpace(summary[i]))
        {
            i++;
        }

        int start = i;
        while (i < summary.Length && (char.IsLetterOrDigit(summary[i]) || summary[i] == '_'))
        {
            i++;
        }

        return i > start ? summary.Substring(start, i - start) : string.Empty;
    }
}
#endif
