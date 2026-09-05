namespace ReadableHttp.AspNetCore;

public static class ReadableHttpFactoryExtensions
{
    /// <summary>Creates a fluent request backed by the default or a named factory client.</summary>
    public static ReadableHttpRequestBuilder Request(this IReadableHttpFactory factory, string url, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return ReadableHttpClient.Request(url).WithExecutor(clientName is null
            ? factory.CreateExecutor()
            : factory.CreateExecutor(clientName));
    }
}
