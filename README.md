# ReadableHttp

ReadableHttp 是面向 .NET 10 的 HTTP SDK，提供流式调用、可序列化的请求/响应模型、认证和可选的格式转换能力。核心包不依赖 UI、命令行应用或 AI 框架。

## 包与目录

| 包 | 目录 | 用途 |
| --- | --- | --- |
| `ReadableHttp` | `src/ReadableHttp` | Fluent API、执行器、变量、认证模型、响应和流式读取 |
| `ReadableHttp.AspNetCore` | `src/ReadableHttp.AspNetCore` | DI、命名客户端、`IHttpClientFactory` |
| `ReadableHttp.Auth` | `extensions/ReadableHttp.Auth` | OAuth2、PKCE、令牌缓存和回调辅助 |
| `ReadableHttp.ImportExport` | `extensions/ReadableHttp.ImportExport` | curl、`.http`、OpenAPI/Swagger 导入导出 |
| `ReadableHttp.Storage` | `extensions/ReadableHttp.Storage` | 请求、exchange、工作区 JSON 存储 |
| `ReadableHttp.Try` | `extensions/ReadableHttp.Try` | 将 API 描述归一化为可执行请求 |

`samples/sdks` 包含控制台和 ASP.NET Core 集成示例；`schemas` 包含请求、环境和工作区的 JSON Schema。应用专用的 MAUI、CLI、AI/MAF 项目及安装包已移除。Storage/Try 的命名空间保持不变，项目引用路径迁移到 `extensions`。

## 安装

```shell
dotnet add package ReadableHttp --version 2.1.0
# 需要依赖注入时安装：
dotnet add package ReadableHttp.AspNetCore --version 2.1.0
```

以上版本对应仓库的包版本。使用尚未发布的变更时，可引用源码项目或使用下文生成的本地 NuGet 包。

## 快速使用

```csharp
using ReadableHttp;

var user = await ReadableHttpClient
    .Request("https://api.example.com/users/{id}")
    .Get()
    .WithPathParameter("id", "42")
    .WithQuery("include", "profile")
    .WithBearerToken(token)
    .WithTimeout(TimeSpan.FromSeconds(30))
    .SendAsync<UserDto>(cancellationToken);
```

- `SendAsync<T>()`：将 JSON 反序列化为 `T`；`string` 返回文本，`byte[]` 返回原始响应字节。
- `SendAsync()`：返回成功的 `ReadableResponse`，适合 DELETE、HEAD 和 204 无响应体的调用。
- `SendExchangeAsync()`：返回请求、响应、重定向、计时和传输错误，保留非 2xx 响应。
- `StreamAsync()`：逐条返回 Headers、Data、Completed 消息。HTTP 状态由 Headers 消息提供，调用方应检查状态后再处理数据。

`SendAsync` 的 HTTP/传输失败抛出 `ReadableHttpException`（继承 `HttpRequestException`）。可通过 `StatusCode` 和 `Exchange` 获取详情；默认异常消息不包含响应正文。取消和超时继续抛出 `OperationCanceledException`；JSON 格式错误抛出 `JsonException`。`SendAsync<T>()` 需要有效的非 null JSON，空响应请使用非泛型重载。

```csharp
try
{
    await ReadableHttpClient.Request("https://api.example.com/users/42")
        .Delete().SendAsync(cancellationToken);
}
catch (ReadableHttpException error)
{
    Console.WriteLine(error.StatusCode);
    // error.Exchange.Response?.BodyText 可供业务错误解析。
}
```

## 复用连接与配置

长期运行的应用应复用调用方管理的 `HttpClient`，或使用下文的 DI 集成。

```csharp
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
    BaseAddress = new Uri("https://api.example.com/"),
    Timeout = Timeout.InfiniteTimeSpan
};
var executor = new ReadableHttpExecutor(client);

var user = await executor.Request("users/{id}")
    .WithPathParameter("id", 42)
    .WithTimeout(TimeSpan.FromSeconds(15))
    .SendAsync<UserDto>(cancellationToken);
```

传入 `HttpClient` 时，执行器不会修改或释放它。每次调用创建独立请求，复用执行器即可；builder 为可变对象，不要在并发任务间共享或修改。同一个 execution context 的请求级配置不会被执行器写回，但调用期间仍不应修改其变量、认证或集合。

`ReadableHttpExecutor(Func<HttpClient>)` 则接管工厂每次创建的客户端并在调用后释放，可用于 `IHttpClientFactory`。工厂必须返回新的客户端实例。显式请求超时覆盖工厂客户端的超时；借用客户端时，请求超时与客户端自己的超时共同生效。显式请求超时覆盖整个响应读取阶段。代理、Cookie 和 TLS 行为由外部客户端的 handler 决定；这些 context 选项仅在执行器自行创建 handler 时生效。

内置执行器手动处理重定向，保留 307/308 的方法和请求体，301/302 仅将 POST 改为 GET，303 保留 HEAD。跨源重定向会移除请求上的 Authorization、Cookie 和认证模型指定的头，不再应用认证模型。含默认 Authorization/Cookie 的外部客户端会拒绝跨源重定向；请把认证放在请求上。自定义客户端/handler 应关闭自动重定向，以保留上述行为和重定向记录。

## 请求体与 JSON 配置

```csharp
using System.Text.Json;
using ReadableHttp;

var result = await ReadableHttpClient.Request("https://api.example.com/users")
    .Post()
    .WithJsonOptions(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    })
    .WithJsonBody(new { UserName = "Ada" })
    .SendAsync<UserDto>(cancellationToken);
```

在 `WithJsonBody` 前设置 `WithJsonOptions`，选项同时用于请求序列化与响应反序列化。支持 `WithRawBody`、`WithFormBody`、Basic/Bearer/API Key 认证、`Head()`、`Options()` 和自定义 HTTP 方法。路径参数自动编码，查询、表单和变量的数值转换使用 invariant culture。

## 流式响应

```csharp
await foreach (var message in executor.Request("events")
    .Get().StreamAsync(ReadableStreamFormat.ServerSentEvents, cancellationToken))
{
    if (message.Type == ReadableStreamMessageType.Headers && message.StatusCode >= 400)
        throw new HttpRequestException($"HTTP {message.StatusCode}");
    if (message.Type == ReadableStreamMessageType.Data)
        Console.WriteLine(message.Data);
}
```

支持 SSE、逐行文本、JSON 数组、Raw UTF-8 文本和 Auto 检测。Raw 模式保留跨字节块的 UTF-8 字符，适合文本流；二进制下载使用 `SendAsync<byte[]>()`。JSON 数组使用标准流式 JSON 解析器，拒绝截断和格式错误的数据。Auto 对声明 JSON 但实际不是数组的响应保留 Raw 回退。枚举期间的传输、取消和解析异常向调用方传播；结束或提前退出枚举会释放响应。

## ASP.NET Core / 依赖注入

```csharp
using ReadableHttp;
using ReadableHttp.AspNetCore;

builder.Services.AddReadableHttpClient("users", client =>
{
    client.BaseAddress = new Uri("https://api.example.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

app.MapGet("/users/{id}", async (
    string id, IReadableHttpFactory factory, CancellationToken cancellationToken) =>
    await factory.Request("users/{id}", "users")
        .WithPathParameter("id", id)
        .SendAsync<UserDto>(cancellationToken));
```

`AddReadableHttpClient` 返回 `IHttpClientBuilder`，可继续配置 handler、日志和其他 HTTP 管道组件。默认关闭 handler 的自动重定向和 Cookie 缓存，避免池化 handler 在请求间共享 Cookie。`AddReadableHttp(...)` 保留为默认客户端的简便注册方法；已有 `AddHttpClient(name)` 也可通过 `factory.CreateExecutor(name)` 使用，其 handler 策略由应用配置。

## 请求模型与变量

```csharp
var request = new ReadableRequest
{
    Method = "GET",
    Url = "{{baseUrl}}/users/{id}",
    PathParameters = [new ReadableNameValue { Name = "id", Value = "42" }]
};
var context = new ReadableExecutionContext
{
    Variables = { ["baseUrl"] = "https://api.example.com" }
};
var exchange = await executor.SendExchangeAsync(request, context, cancellationToken);
```

请求变量优先于 context 变量，变量名不区分大小写。JSON 正文中的替换保持字符串转义。`Auth.Type = Inherit` 使用 context 认证；`None` 明确关闭继承。请求、环境与工作区的 schema version 保持 1.0，Schema 随核心 NuGet 包一起提供。原始请求预览和 exchange 可能包含认证及正文，保存或记录日志前应由应用脱敏。

## 构建、测试与打包

需要 .NET 10 SDK，无需 MAUI workloads。

```shell
dotnet restore ReadableHttp.sln
dotnet build ReadableHttp.sln --configuration Release --no-restore
dotnet test tests/ReadableHttp.Tests/ReadableHttp.Tests.csproj --configuration Release --no-build
pwsh ./scripts/pack.ps1
```

控制台示例默认请求 HTTPBin，可用参数指定服务地址：

```shell
dotnet run --project samples/sdks/ReadableHttp.ConsoleSample -- https://httpbin.org/
dotnet run --project samples/sdks/ReadableHttp.WebApiSample
```

CI 构建 SDK、扩展和示例，运行测试并打包六个库。NuGet 发布工作流沿用 `v*.*.*` 标签触发或手动触发。

本次结构调整、行为修复与回归验证见 [SDK 审计记录](docs/SDK-AUDIT.md)。
