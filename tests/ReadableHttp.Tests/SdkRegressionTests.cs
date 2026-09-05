using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReadableHttp.AspNetCore;
using ReadableHttp.Execution;

namespace ReadableHttp.Tests;

public sealed class SdkRegressionTests
{
    [Fact]
    public async Task Fluent_request_encodes_path_query_and_uses_invariant_values()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var handler = new MockHttpMessageHandler((request, _) =>
            {
                Assert.Equal("https://example.test/users/a%2Fb?q=1.5#section", request.RequestUri!.AbsoluteUri);
                return Task.FromResult(MockHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
            });
            var response = await new ReadableHttpExecutor(handler).Request("https://example.test/users/{{id}}#section")
                .WithPathParameter("id", "a/b").WithQuery("q", 1.5m).SendAsync(TestContext.Current.CancellationToken);
            Assert.Equal(200, response.StatusCode);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(301, "POST", "GET", false)]
    [InlineData(302, "PUT", "PUT", true)]
    [InlineData(303, "HEAD", "HEAD", true)]
    [InlineData(303, "PUT", "GET", false)]
    [InlineData(307, "POST", "POST", true)]
    [InlineData(308, "POST", "POST", true)]
    public async Task Redirect_preserves_method_semantics(int status, string method, string expected, bool hasBody)
    {
        var count = 0;
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(++count == 1
            ? Redirect(status, "/next") : MockHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var result = await new ReadableHttpExecutor(client).Request("/start").WithMethod(method)
            .WithRawBody("payload").SendExchangeAsync(TestContext.Current.CancellationToken);
        Assert.Null(result.Error);
        Assert.Single(result.Response!.Redirects);
        Assert.Equal(expected, handler.Requests[1].Method.Method);
        Assert.Equal(hasBody, handler.Requests[1].Content is not null);
        Assert.Equal("https://example.test/next", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_origin_redirect_removes_credentials_for_buffered_and_streamed_calls(bool streaming)
    {
        var count = 0;
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(++count == 1
            ? Redirect(302, "https://other.test/next") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello") }));
        var request = new ReadableHttpExecutor(handler).Request("https://example.test/start")
            .WithBearerToken("secret").WithHeader("Cookie", "session=secret").WithHeader("X-Trace", "trace");
        if (streaming)
        {
            var messages = await request.StreamAsync(ReadableStreamFormat.Raw, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal("hello", string.Concat(messages.Where(x => x.Type == ReadableStreamMessageType.Data).Select(x => x.Data)));
        }
        else Assert.Equal(200, (await request.SendAsync(TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Null(handler.Requests[1].Headers.Authorization);
        Assert.False(handler.Requests[1].Headers.Contains("Cookie"));
        Assert.Equal("trace", Assert.Single(handler.Requests[1].Headers.GetValues("X-Trace")));
    }

    [Fact]
    public async Task Query_auth_is_not_duplicated_and_inherited_auth_is_respected()
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(MockHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        var request = new ReadableRequest { Url = "https://example.test/", Auth = new ReadableAuth { Type = ReadableAuthType.Inherit } };
        var context = new ReadableExecutionContext { Auth = new ReadableAuth { Type = ReadableAuthType.ApiKey, ApiKeyLocation = ReadableApiKeyLocation.Query, Name = "key", Value = "secret" } };
        var exchange = await new ReadableHttpExecutor(handler).SendAsync(request, context, TestContext.Current.CancellationToken);
        Assert.Null(exchange.Error);
        Assert.Equal("?key=secret", handler.Requests[0].RequestUri!.Query);
        Assert.Empty(request.Query);
        Assert.Equal(ReadableAuthType.Inherit, request.Auth.Type);
    }

    [Fact]
    public async Task Execution_does_not_mutate_context_or_dispose_borrowed_client()
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(MockHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/"), Timeout = TimeSpan.FromSeconds(20) };
        var executor = new ReadableHttpExecutor(client);
        var context = new ReadableExecutionContext();
        var request = new ReadableRequest { Url = "/", Options = new ReadableRequestOptions { Timeout = TimeSpan.FromSeconds(1), FollowRedirects = false } };
        Assert.Null((await executor.SendAsync(request, context, TestContext.Current.CancellationToken)).Error);
        Assert.False(context.HasTimeoutOverride);
        Assert.True(context.FollowRedirects);
        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
        Assert.Equal(200, (await executor.Request("/").SendAsync(TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Fluent_response_supports_binary_empty_and_structured_errors()
    {
        using var handler = new MockHttpMessageHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/bytes" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 128, 255]) },
            "/empty" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => MockHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"error\":\"missing\"}")
        }));
        var executor = new ReadableHttpExecutor(handler);
        Assert.Equal(new byte[] { 0, 128, 255 }, await executor.Request("https://example.test/bytes").SendAsync<byte[]>(TestContext.Current.CancellationToken));
        Assert.Equal(204, (await executor.Request("https://example.test/empty").SendAsync(TestContext.Current.CancellationToken)).StatusCode);
        var error = await Assert.ThrowsAsync<ReadableHttpException>(() => executor.Request("https://example.test/error").SendAsync<string>(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal("{\"error\":\"missing\"}", error.Exchange.Response!.BodyText);
        Assert.DoesNotContain("missing", error.Message);
    }

    [Fact]
    public async Task Fluent_json_options_apply_to_request_and_response()
    {
        using var handler = new MockHttpMessageHandler(async (request, token) =>
        {
            Assert.Equal("{\"user_name\":\"Ada\"}", await request.Content!.ReadAsStringAsync(token));
            return MockHttpMessageHandler.Json(HttpStatusCode.OK, "{\"user_name\":\"Grace\"}");
        });
        var result = await new ReadableHttpExecutor(handler).Request("https://example.test/")
            .Post().WithJsonOptions(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            .WithJsonBody(new User("Ada")).SendAsync<User>(TestContext.Current.CancellationToken);
        Assert.Equal("Grace", result.UserName);
    }

    [Theory]
    [InlineData("[1,")]
    [InlineData("[1,,2]")]
    [InlineData("[1] garbage")]
    public async Task Json_array_stream_rejects_malformed_content(string content)
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(MockHttpMessageHandler.Json(HttpStatusCode.OK, content)));
        var request = new ReadableHttpExecutor(handler).Request("https://example.test/");
        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var message in request.StreamAsync(ReadableStreamFormat.JsonArray, TestContext.Current.CancellationToken))
                Assert.NotEqual(ReadableStreamMessageType.Completed, message.Type);
        });
    }

    [Fact]
    public async Task Raw_stream_preserves_utf8_across_single_byte_reads()
    {
        const string text = "你好🙂 café";
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SingleByteStream(Encoding.UTF8.GetBytes(text)))
        }));
        var messages = await new ReadableHttpExecutor(handler).Request("https://example.test/")
            .StreamAsync(ReadableStreamFormat.Raw, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(text, string.Concat(messages.Where(x => x.Type == ReadableStreamMessageType.Data).Select(x => x.Data)));
        Assert.Equal(ReadableStreamMessageType.Completed, messages[^1].Type);
    }

    [Fact]
    public async Task Named_factory_exposes_fluent_requests_and_handler_configuration()
    {
        using var handler = new MockHttpMessageHandler((request, _) =>
        {
            Assert.Equal("https://named.test/users/42", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(MockHttpMessageHandler.Json(HttpStatusCode.OK, "{\"userName\":\"Ada\"}"));
        });
        var services = new ServiceCollection();
        services.AddReadableHttpClient("users", client => client.BaseAddress = new Uri("https://named.test"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IReadableHttpFactory>().Request("/users/{id}", "users")
            .WithPathParameter("id", 42).SendAsync<User>(TestContext.Current.CancellationToken);
        Assert.Equal("Ada", result.UserName);
    }

    [Fact]
    public async Task Cancellation_propagates_without_becoming_an_exchange_error()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        using var handler = new MockHttpMessageHandler((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReadableHttpExecutor(handler)
            .Request("https://example.test/").SendExchangeAsync(cancellation.Token));
    }

    private static HttpResponseMessage Redirect(int status, string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task Cross_origin_redirect_rejects_default_client_credentials()
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(Redirect(302, "https://other.test/")));
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
        var result = await new ReadableHttpExecutor(client).Request("https://example.test/")
            .SendExchangeAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(result.Error);
        Assert.Contains("cross-origin", result.Error.Message);
        Assert.Single(handler.Requests);
        Assert.Equal("secret", client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public async Task Disabled_redirects_return_original_response()
    {
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(Redirect(302, "/next")));
        var result = await new ReadableHttpExecutor(handler).SendAsync(
            new ReadableRequest { Url = "https://example.test/", Options = new ReadableRequestOptions { FollowRedirects = false } },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(302, result.Response!.StatusCode);
        Assert.Empty(result.Response.Redirects);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(2147483648L)]
    public void Timeout_rejects_invalid_values(long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadableHttpClient.Request("https://example.test/")
            .WithTimeout(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2147483647L)]
    public void Timeout_accepts_supported_boundaries(long milliseconds)
    {
        var context = new ReadableExecutionContext { Timeout = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.True(context.HasTimeoutOverride);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), context.Timeout);
    }

    private sealed record User(string UserName);

    private sealed class SingleByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
