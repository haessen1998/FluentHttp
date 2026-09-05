using System.Net;

namespace ReadableHttp;

/// <summary>An SDK request failure retaining the exchange for structured diagnostics.</summary>
public sealed class ReadableHttpException : HttpRequestException
{
    public ReadableHttpException(ReadableExchange exchange)
        : base(GetMessage(exchange), null, exchange.Response is { } response ? (HttpStatusCode)response.StatusCode : null)
    {
        Exchange = exchange;
    }

    public ReadableExchange Exchange { get; }

    private static string GetMessage(ReadableExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return exchange.Error?.Message ?? (exchange.Response is { } response
            ? $"HTTP request failed with status {response.StatusCode} ({response.ReasonPhrase})."
            : "HTTP request did not return a response.");
    }
}
