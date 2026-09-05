using ReadableHttp;
using ReadableHttp.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddReadableHttpClient("httpbin", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
var app = builder.Build();
app.MapGet("/", () => Results.Redirect("/httpbin"));
app.MapGet("/httpbin", async (IReadableHttpFactory factory, CancellationToken cancellationToken) =>
{
    var exchange = await factory.Request("get", "httpbin")
        .WithQuery("source", "webapi-sample")
        .SendExchangeAsync(cancellationToken);
    return Results.Json(new
    {
        status = exchange.Response?.StatusCode,
        body = exchange.Response?.BodyText,
        error = exchange.Error?.Message
    });
});
app.MapGet("/httpbin/stream", (IReadableHttpFactory factory, CancellationToken cancellationToken) =>
    factory.Request("stream/3", "httpbin").StreamAsync(ReadableStreamFormat.Lines, cancellationToken));
app.Run();
