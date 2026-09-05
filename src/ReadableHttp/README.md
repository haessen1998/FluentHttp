# ReadableHttp

A .NET 10 HTTP SDK with fluent requests, structured exchanges, variable resolution and streaming. No UI or application framework dependencies.

```shell
dotnet add package ReadableHttp --version 2.1.0
```

## Fluent requests

```csharp
using ReadableHttp;

var user = await ReadableHttpClient.Request("https://api.example.com/users/{id}")
    .Get()
    .WithPathParameter("id", 42)
    .WithQuery("include", "profile")
    .WithTimeout(TimeSpan.FromSeconds(30))
    .SendAsync<UserDto>(cancellationToken);
```

`SendAsync<T>()` deserializes JSON; `string` returns response text and `byte[]` returns response bytes. `SendAsync()` returns a successful `ReadableResponse`, including 204/HEAD responses. Generic JSON calls require valid, non-null JSON. Use `WithJsonOptions(options)` before `WithJsonBody(value)` to configure both serialization and deserialization.

The builder supports GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS, custom methods, JSON/raw/form bodies, Basic/Bearer/API Key authentication, variables and path parameters. Values use invariant culture and path/query values are URI-encoded. Builders are mutable; do not modify or share them concurrently.

## Connection lifetime

Reuse an executor backed by a caller-owned client for repeated calls:

```csharp
using ReadableHttp;
using ReadableHttp.Execution;

using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
using var client = new HttpClient(handler)
{
    BaseAddress = new Uri("https://api.example.com/"),
    Timeout = Timeout.InfiniteTimeSpan
};
var executor = new ReadableHttpExecutor(client);
var response = await executor.Request("users/42")
    .WithTimeout(TimeSpan.FromSeconds(30))
    .SendAsync(cancellationToken);
```

The `HttpClient` constructor borrows the client without changing or disposing it. The `Func<HttpClient>` constructor owns and disposes each returned client, making it suitable for `IHttpClientFactory`; the delegate must return a new instance each time. Explicit request timeouts override factory-client timeouts. For borrowed clients, both client and request timeouts apply. Explicit request timeouts cover response reading as well as sending.

External handlers own proxy, cookie and TLS policy. Disable their automatic redirects to let the SDK capture redirects and remove request authentication across origins. Cross-origin redirects with default Authorization/Cookie headers on an external client are rejected. Put credentials on individual requests. The parameterless executor owns its per-call client and handler.

## Responses and errors

`SendExchangeAsync()` returns the resolved request, response, redirects, timings and transport error. HTTP failures retain status and body. `SendAsync()` and `SendAsync<T>()` throw `ReadableHttpException` on HTTP/transport failure; inspect `StatusCode` and `Exchange`. Response bodies are excluded from default exception messages. Cancellation and timeouts propagate as `OperationCanceledException`; invalid JSON propagates as `JsonException`.

Exchange data and raw previews can contain credentials and request/response bodies. Apply application-specific redaction before persistence or logging.

## Streaming

```csharp
await foreach (var message in executor.Request("events")
    .StreamAsync(ReadableStreamFormat.ServerSentEvents, cancellationToken))
{
    if (message.Type == ReadableStreamMessageType.Headers && message.StatusCode >= 400)
        throw new HttpRequestException($"HTTP {message.StatusCode}");
    if (message.Type == ReadableStreamMessageType.Data)
        Console.WriteLine(message.Data);
}
```

Supports Auto, SSE, Lines, JsonArray and Raw UTF-8 text. Raw preserves characters across byte boundaries; it is not a binary download API. JSON arrays are parsed incrementally and malformed/truncated arrays throw. Auto retains a raw-text fallback when JSON content does not begin with an array. HTTP status appears in the Headers message; callers decide how to handle non-success status. Enumeration errors propagate, and completion or early disposal releases the response.

## Models and schemas

Use `ReadableRequest` with `ReadableExecutionContext` for data-driven calls. Request variables override context variables. `Inherit` authentication uses context authentication; `None` disables it. Execution does not write request options back into the supplied context. Do not modify its nested collections during execution.

Request, environment and workspace JSON schemas are included under `schemas/` in this package. Format version remains 1.0. Optional packages provide DI (`ReadableHttp.AspNetCore`), OAuth2 helpers (`ReadableHttp.Auth`), converters (`ReadableHttp.ImportExport`), storage (`ReadableHttp.Storage`) and document normalization (`ReadableHttp.Try`).
