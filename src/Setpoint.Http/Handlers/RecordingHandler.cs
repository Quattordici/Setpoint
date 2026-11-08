using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Setpoint.Http.Handlers;

/// <summary>
/// Dev-only <c>--record</c>: saves every request and its response, redacted, as a WireMock.Net mapping file,
/// so recordings from the live test environments can feed the WireMock tests directly.
/// </summary>
internal sealed partial class RecordingHandler(string directory, string target, SecretRedactor redactor) : DelegatingHandler
{
    private static readonly HashSet<string> s_droppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "Server", "Connection", "Transfer-Encoding", "Content-Length", "Keep-Alive", "Strict-Transport-Security",
        "X-GitHub-Request-Id", "ActivityId", "X-TFS-Session", "X-VSS-E2EID", "Request-Context", "X-MSEdge-Ref",
    };

    private int _sequence;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        int sequence = Interlocked.Increment(ref _sequence);
        Uri uri = request.RequestUri!;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var mapping = new WireMockMapping(
            Title: $"{target} #{sequence}: {request.Method} {uri.AbsolutePath}",
            Description: requestBody is null ? null : redactor.Redact(requestBody),
            Request: new WireMockRequest(
                Path: uri.AbsolutePath,
                Methods: [request.Method.Method],
                Params: query.AllKeys.Length == 0
                    ? null
                    : [.. query.AllKeys.OfType<string>().Select(key => new WireMockParam(
                        key,
                        [new WireMockMatcher("ExactMatcher", redactor.Redact(query[key] ?? string.Empty))]))]),
            Response: new WireMockResponse(
                StatusCode: (int)response.StatusCode,
                Headers: response.Headers.Concat(response.Content.Headers)
                    .Where(h => !s_droppedResponseHeaders.Contains(h.Key) && !SecretRedactor.SensitiveHeaders.Contains(h.Key))
                    .ToDictionary(h => h.Key, h => redactor.Redact(string.Join(", ", h.Value)), StringComparer.OrdinalIgnoreCase),
                Body: redactor.Redact(responseBody)));

        Directory.CreateDirectory(directory);
        string fileName = $"{target}-{sequence:D4}-{request.Method.Method.ToLowerInvariant()}-{Slug(uri.AbsolutePath)}.json";
        await File.WriteAllTextAsync(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(mapping, RecordingJsonContext.Default.WireMockMapping),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        return response;
    }

    private static string Slug(string path)
    {
        string slug = NonWordPattern().Replace(path.Trim('/'), "-");
        return slug.Length > 80 ? slug[..80] : slug;
    }

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonWordPattern();
}

/// <summary>A WireMock.Net static mapping, in the format its <c>__admin/mappings</c> files use.</summary>
internal sealed record WireMockMapping(string Title, string? Description, WireMockRequest Request, WireMockResponse Response);

internal sealed record WireMockRequest(string Path, IReadOnlyList<string> Methods, IReadOnlyList<WireMockParam>? Params);

internal sealed record WireMockParam(string Name, IReadOnlyList<WireMockMatcher> Matchers);

internal sealed record WireMockMatcher(string Name, string Pattern);

internal sealed record WireMockResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body);

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireMockMapping))]
internal sealed partial class RecordingJsonContext : JsonSerializerContext;
