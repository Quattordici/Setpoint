using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Setpoint.Http.Handlers;

/// <summary>
/// Logs each attempt: method, redacted URL, status and duration at debug level, and headers at trace level
/// with sensitive ones masked. Bodies are never logged.
/// </summary>
internal sealed partial class LoggingHandler(ILogger logger, SecretRedactor redactor, string target) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = redactor.RedactUri(request.RequestUri!);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            LogRequestHeaders(logger, target, request.Method.Method, url, FormatHeaders(request.Headers));
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            LogResponse(logger, target, request.Method.Method, url, (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (logger.IsEnabled(LogLevel.Trace))
            {
                LogResponseHeaders(logger, target, (int)response.StatusCode, FormatHeaders(response.Headers));
            }

            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogFailure(logger, target, request.Method.Method, url, redactor.Redact(ex.Message), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private string FormatHeaders(System.Net.Http.Headers.HttpHeaders headers) =>
        string.Join("; ", headers.Select(h => $"{h.Key}: {redactor.RedactHeader(h.Key, string.Join(", ", h.Value))}"));

    [LoggerMessage(Level = LogLevel.Debug, Message = "[{Target}] {Method} {Url} -> {Status} in {ElapsedMs:0} ms")]
    private static partial void LogResponse(ILogger logger, string target, string method, string url, int status, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[{Target}] {Method} {Url} failed after {ElapsedMs:0} ms: {Error}")]
    private static partial void LogFailure(ILogger logger, string target, string method, string url, string error, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[{Target}] {Method} {Url} request headers: {Headers}")]
    private static partial void LogRequestHeaders(ILogger logger, string target, string method, string url, string headers);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[{Target}] {Status} response headers: {Headers}")]
    private static partial void LogResponseHeaders(ILogger logger, string target, int status, string headers);
}
