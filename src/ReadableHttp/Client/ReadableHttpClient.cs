using System.Text.Json;
using System.Globalization;
using ReadableHttp.Execution;

namespace ReadableHttp;

public static class ReadableHttpClient
{
    public static ReadableHttpRequestBuilder Request(string url)
    {
        return new ReadableHttpRequestBuilder().WithUrl(url);
    }

    public static ReadableHttpRequestBuilder Request(this IReadableHttpExecutor executor, string url)
        => Request(url).WithExecutor(executor);
}

public sealed class ReadableHttpRequestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ReadableRequest _request = new();
    private readonly ReadableExecutionContext _context = new();
    private IReadableHttpExecutor _executor = new ReadableHttpExecutor();
    private JsonSerializerOptions _jsonOptions = JsonOptions;

    public ReadableHttpRequestBuilder WithJsonOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _jsonOptions = new JsonSerializerOptions(options);
        return this;
    }

    public ReadableHttpRequestBuilder WithExecutor(IReadableHttpExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
        return this;
    }

    public ReadableHttpRequestBuilder WithBaseAddress(string baseAddress)
    {
        _context.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
        return this;
    }

    public ReadableHttpRequestBuilder WithTimeout(TimeSpan timeout)
    {
        _context.Timeout = timeout;
        return this;
    }

    public ReadableHttpRequestBuilder WithUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        _request.Url = url;
        return this;
    }

    public ReadableHttpRequestBuilder WithMethod(string method)
    {
        _request.Method = new HttpMethod(method).Method;
        return this;
    }

    public ReadableHttpRequestBuilder Get() => WithMethod("GET");

    public ReadableHttpRequestBuilder Post() => WithMethod("POST");

    public ReadableHttpRequestBuilder Put() => WithMethod("PUT");

    public ReadableHttpRequestBuilder Patch() => WithMethod("PATCH");

    public ReadableHttpRequestBuilder Delete() => WithMethod("DELETE");

    public ReadableHttpRequestBuilder Head() => WithMethod("HEAD");

    public ReadableHttpRequestBuilder Options() => WithMethod("OPTIONS");

    public ReadableHttpRequestBuilder WithPathParameter(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _request.PathParameters.Add(new ReadableNameValue { Name = name, Value = FormatValue(value) });
        return this;
    }

    public ReadableHttpRequestBuilder WithHeader(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _request.Headers.Add(new ReadableNameValue { Name = name, Value = FormatValue(value), Enabled = true });
        return this;
    }

    public ReadableHttpRequestBuilder WithQuery(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _request.Query.Add(new ReadableNameValue { Name = name, Value = FormatValue(value), Enabled = true });
        return this;
    }

    public ReadableHttpRequestBuilder WithVariable(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _context.Variables[name] = FormatValue(value);
        return this;
    }

    public ReadableHttpRequestBuilder WithBearerToken(string token)
    {
        _request.Auth = new ReadableAuth { Type = ReadableAuthType.Bearer, Token = token };
        return this;
    }

    public ReadableHttpRequestBuilder WithBasicAuth(string username, string password)
    {
        _request.Auth = new ReadableAuth { Type = ReadableAuthType.Basic, Username = username, Password = password };
        return this;
    }

    public ReadableHttpRequestBuilder WithApiKey(string name, string value, ReadableApiKeyLocation location = ReadableApiKeyLocation.Header)
    {
        _request.Auth = new ReadableAuth { Type = ReadableAuthType.ApiKey, Name = name, Value = value, ApiKeyLocation = location };
        return this;
    }

    public ReadableHttpRequestBuilder WithJsonBody<T>(T body)
    {
        _request.Body = new ReadableBody
        {
            Type = ReadableBodyType.Json,
            Content = JsonSerializer.Serialize(body, _jsonOptions),
            ContentType = "application/json"
        };
        return this;
    }

    public ReadableHttpRequestBuilder WithRawBody(string content, string contentType = "text/plain")
    {
        _request.Body = new ReadableBody
        {
            Type = ReadableBodyType.Raw,
            Content = content,
            ContentType = contentType
        };
        return this;
    }

    public ReadableHttpRequestBuilder WithFormBody(params (string Name, object? Value)[] values)
    {
        _request.Body = new ReadableBody
        {
            Type = ReadableBodyType.FormUrlEncoded,
            Form = values.Select(value => new ReadableNameValue
            {
                Name = value.Name,
                Value = FormatValue(value.Value),
                Enabled = true
            }).ToList()
        };
        return this;
    }

    public Task<ReadableExchange> SendExchangeAsync(CancellationToken cancellationToken = default)
    {
        return _executor.SendExchangeAsync(_request, _context, cancellationToken);
    }

    public IAsyncEnumerable<ReadableStreamMessage> StreamAsync(
        ReadableStreamFormat format = ReadableStreamFormat.Auto,
        CancellationToken cancellationToken = default)
    {
        return _executor.StreamAsync(
            _request,
            _context,
            new ReadableStreamOptions { Format = format },
            cancellationToken);
    }

    public async Task<T> SendAsync<T>(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(cancellationToken).ConfigureAwait(false);

        if (typeof(T) == typeof(string))
        {
            return (T)(object)(response.BodyText ?? string.Empty);
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)(response.BodyBytes ?? []);
        }

        return JsonSerializer.Deserialize<T>(response.BodyText ?? string.Empty, _jsonOptions)
            ?? throw new InvalidOperationException("Response JSON content was empty or null.");
    }

    /// <summary>Sends a request and returns a successful response, including responses with no body.</summary>
    public async Task<ReadableResponse> SendAsync(CancellationToken cancellationToken = default)
    {
        var exchange = await SendExchangeAsync(cancellationToken).ConfigureAwait(false);
        if (exchange.Error is not null || exchange.Response is null || exchange.Response.StatusCode is < 200 or >= 300)
        {
            throw new ReadableHttpException(exchange);
        }
        return exchange.Response;
    }

    private static string? FormatValue(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);
}
