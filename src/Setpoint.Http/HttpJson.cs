using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Setpoint.Http;

/// <summary>JSON helpers that take a source-generated contract, so no adapter can fall back to reflection.</summary>
public static class HttpJson
{
    private static readonly MediaTypeHeaderValue s_json = new("application/json") { CharSet = "utf-8" };

    /// <summary>Reads the response body as <typeparamref name="T"/>.</summary>
    /// <exception cref="HttpApiException">The response is not a success, or its body is not the expected JSON.</exception>
    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        await HttpApiException.ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(body, typeInfo, cancellationToken).ConfigureAwait(false)
                    ?? throw new HttpApiException(response.RequestMessage?.Method, response.RequestMessage?.RequestUri, (int)response.StatusCode, "The response body was empty.");
            }
        }
        catch (JsonException ex)
        {
            throw new HttpApiException(response.RequestMessage?.Method, response.RequestMessage?.RequestUri, (int)response.StatusCode, $"The response was not the JSON Setpoint expected: {ex.Message}", ex);
        }
    }

    /// <summary>Creates request content from <paramref name="value"/>.</summary>
    public static HttpContent Content<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        string json = JsonSerializer.Serialize(value, typeInfo);
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = s_json;
        return content;
    }
}

/// <summary>An HTTP call that failed, with enough context to report which target and object it was about.</summary>
public sealed class HttpApiException : Exception
{
    public HttpApiException(HttpMethod? method, Uri? uri, int? statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Method = method;
        Uri = uri;
        StatusCode = statusCode;
    }

    /// <summary>The request method.</summary>
    public HttpMethod? Method { get; }

    /// <summary>The request URI.</summary>
    public Uri? Uri { get; }

    /// <summary>The response status, if there was a response.</summary>
    public int? StatusCode { get; }

    /// <summary>Throws when <paramref name="response"/> is not a success, including the start of the body in the message.</summary>
    public static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string excerpt = body.Length > 500 ? body[..500] + "…" : body;
        throw new HttpApiException(
            response.RequestMessage?.Method,
            response.RequestMessage?.RequestUri,
            (int)response.StatusCode,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} returned {(int)response.StatusCode} {response.ReasonPhrase}. {excerpt}".TrimEnd());
    }
}
