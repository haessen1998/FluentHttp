# ReadableHttp.AspNetCore

.NET 10 dependency injection and `IHttpClientFactory` integration for ReadableHttp. Works with ASP.NET Core and other hosts using Microsoft.Extensions.DependencyInjection.

```shell
dotnet add package ReadableHttp.AspNetCore --version 2.1.0
```

## Default client

```csharp
using ReadableHttp.AspNetCore;

builder.Services.AddReadableHttp(client =>
{
    client.BaseAddress = new Uri("https://api.example.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

Inject `IReadableHttpExecutor` for the default client, or `IReadableHttpFactory` to select named clients. Existing `SendAsync(request, context, token)` and `SendExchangeAsync` model APIs remain available.

## Named fluent requests

```csharp
using ReadableHttp;
using ReadableHttp.AspNetCore;

builder.Services.AddReadableHttpClient("billing", client =>
{
    client.BaseAddress = new Uri("https://billing.example.com/");
});

app.MapGet("/invoices/{id}", async (
    string id, IReadableHttpFactory factory, CancellationToken cancellationToken) =>
    await factory.Request("invoices/{id}", "billing")
        .WithPathParameter("id", id)
        .SendAsync<InvoiceDto>(cancellationToken));
```

`AddReadableHttpClient` returns `IHttpClientBuilder`, so handler configuration can be chained. The SDK registration disables automatic redirects and cookie storage on the standard primary handler. This keeps SDK redirect handling active and avoids pooled cookie state between requests. If you replace the primary handler afterward, configure these policies on your replacement.

For existing `AddHttpClient(name)` registrations, call `factory.CreateExecutor(name)` or `factory.Request(url, name)`. Their handler configuration remains application-owned; disable automatic redirects for SDK redirect tracking and credential handling. Proxy/TLS/cookie options in an execution context do not reconfigure an external handler.

The factory creates and disposes an HttpClient per call while IHttpClientFactory pools handlers. Configure credentials per request instead of DefaultRequestHeaders when following cross-origin redirects. `SendAsync` throws `ReadableHttpException` on HTTP/transport failures; `SendExchangeAsync` retains the response and error. Cancellation continues to propagate. Streaming is available on the same fluent builder.
