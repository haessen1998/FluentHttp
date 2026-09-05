using ReadableHttp;
using ReadableHttp.Execution;

using var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
};
using var client = new HttpClient(handler)
{
    BaseAddress = new Uri(args.FirstOrDefault() ?? "https://httpbin.org/"),
    Timeout = TimeSpan.FromSeconds(30)
};
var executor = new ReadableHttpExecutor(client);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var response = await executor.Request("get")
    .Get().WithQuery("source", "console-sample")
    .SendAsync(cancellation.Token);
Console.WriteLine($"Status: {response.StatusCode}");
Console.WriteLine(response.BodyText);

await foreach (var message in executor.Request("stream/3")
    .StreamAsync(ReadableStreamFormat.Lines, cancellation.Token))
{
    if (message.Type == ReadableStreamMessageType.Data)
        Console.WriteLine(message.Data);
}
