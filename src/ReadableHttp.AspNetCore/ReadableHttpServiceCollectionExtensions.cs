using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReadableHttp.Execution;

namespace ReadableHttp.AspNetCore;

public static class ReadableHttpServiceCollectionExtensions
{
    public static IServiceCollection AddReadableHttp(this IServiceCollection services)
    {
        return services.AddReadableHttp(configureClient: null);
    }

    public static IServiceCollection AddReadableHttp(
        this IServiceCollection services,
        Action<HttpClient>? configureClient)
    {
        services.AddReadableHttpClient(ReadableHttpClientNames.Default, configureClient);
        return services;
    }

    /// <summary>Registers a named SDK client and exposes handler configuration through IHttpClientBuilder.</summary>
    public static IHttpClientBuilder AddReadableHttpClient(
        this IServiceCollection services,
        string name = ReadableHttpClientNames.Default,
        Action<HttpClient>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name != ReadableHttpClientNames.Default) services.AddReadableHttpClient();
        var builder = services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler((handler, _) =>
            {
                if (handler is SocketsHttpHandler sockets)
                {
                    sockets.AllowAutoRedirect = false;
                    sockets.UseCookies = false;
                }
                else if (handler is HttpClientHandler http)
                {
                    http.AllowAutoRedirect = false;
                    http.UseCookies = false;
                }
            });
        if (configureClient is not null) builder.ConfigureHttpClient(configureClient);
        services.TryAddSingleton<IReadableHttpExecutor>(provider =>
            provider.GetRequiredService<IReadableHttpFactory>().CreateExecutor());
        services.TryAddSingleton<IReadableHttpFactory, ReadableHttpFactory>();
        return builder;
    }
}
