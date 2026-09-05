using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReadableHttp;

namespace ReadableHttp.Execution;

public sealed class ReadableHttpExecutor : IReadableHttpExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly HttpMessageHandler? _handler;
    private readonly Func<HttpClient>? _httpClientFactory;
    private readonly HttpClient? _client;

    /// <summary>Uses a caller-owned client without changing or disposing it. Configure its handler to disable automatic redirects.</summary>
    public ReadableHttpExecutor(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public ReadableHttpExecutor()
    {
    }

    public ReadableHttpExecutor(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public ReadableHttpExecutor(Func<HttpClient> httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ReadableExchange> SendAsync(
        ReadableRequest request,
        ReadableExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        return await SendExchangeAsync(request, context, cancellationToken);
    }

    public async Task<ReadableExchange> SendExchangeAsync(
        ReadableRequest request,
        ReadableExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        context = context?.Snapshot() ?? new ReadableExecutionContext();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var timings = new List<ReadableExchangeTiming>();
        var buildStarted = stopwatch.Elapsed;
        var exchange = new ReadableExchange
        {
            Request = ReadableRequestVariableResolver.Resolve(request, context),
            StartedAt = startedAt,
            Timings = timings
        };
        ApplyRequestOptions(exchange.Request, context);
        AddTiming(timings, "Build Request", buildStarted, stopwatch.Elapsed);

        try
        {
            using var timeoutCancellation = CreateTimeoutCancellationTokenSource(context, cancellationToken);
            var effectiveCancellationToken = timeoutCancellation?.Token ?? cancellationToken;

            var clientStarted = stopwatch.Elapsed;
            using var lease = CreateHttpClient(context);
            var httpClient = lease.Client;
            AddTiming(timings, "Create HttpClient", clientStarted, stopwatch.Elapsed);

            var sendStarted = stopwatch.Elapsed;
            var result = await SendWithRedirectsAsync(httpClient, exchange.Request, context, effectiveCancellationToken);
            AddTiming(timings, "Send HTTP", sendStarted, stopwatch.Elapsed);
            exchange.RawRequestPreview = result.RawRequestPreview;
            using var response = result.Response;

            var readStarted = stopwatch.Elapsed;
            var bytes = await response.Content.ReadAsByteArrayAsync(effectiveCancellationToken);
            AddTiming(timings, "Read Response", readStarted, stopwatch.Elapsed);
            stopwatch.Stop();

            exchange.Response = new ReadableResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = ReadHeaders(response),
                Cookies = ReadCookies(response),
                Redirects = result.Redirects,
                BodyBytes = bytes,
                BodyText = TryReadText(bytes, response.Content.Headers.ContentType),
                ContentType = response.Content.Headers.ContentType?.ToString(),
                Duration = stopwatch.Elapsed,
                Size = bytes.LongLength
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            exchange.Error = new ReadableExecutionError
            {
                Type = exception.GetType().Name,
                Message = exception.Message
            };
        }
        finally
        {
            exchange.FinishedAt = DateTimeOffset.UtcNow;
        }

        return exchange;
    }

    private static void AddTiming(
        List<ReadableExchangeTiming> timings,
        string name,
        TimeSpan startOffset,
        TimeSpan finishedAt)
    {
        timings.Add(new ReadableExchangeTiming
        {
            Name = name,
            StartOffset = startOffset,
            Duration = finishedAt - startOffset
        });
    }

    public async IAsyncEnumerable<ReadableStreamMessage> StreamAsync(
        ReadableRequest request,
        ReadableExecutionContext? context = null,
        ReadableStreamOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        context = context?.Snapshot() ?? new ReadableExecutionContext();
        options ??= new ReadableStreamOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BufferSize);

        var resolvedRequest = ReadableRequestVariableResolver.Resolve(request, context);
        ApplyRequestOptions(resolvedRequest, context);
        using var timeoutCancellation = CreateTimeoutCancellationTokenSource(context, cancellationToken);
        var effectiveCancellationToken = timeoutCancellation?.Token ?? cancellationToken;

        using var lease = CreateHttpClient(context);
        var httpClient = lease.Client;

        HttpResponseMessage? response = null;
        try
        {
            var result = await SendWithRedirectsAsync(httpClient, resolvedRequest, context, effectiveCancellationToken, capturePreview: false);
            response = result.Response;

            yield return new ReadableStreamMessage
            {
                Type = ReadableStreamMessageType.Headers,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = ReadHeaders(response)
            };

            var format = ResolveStreamFormat(options.Format, response.Content.Headers.ContentType);
            await using var stream = await response.Content.ReadAsStreamAsync(effectiveCancellationToken);

            if (format == ReadableStreamFormat.Raw)
            {
                await foreach (var message in ReadRawStreamAsync(stream, options.BufferSize, effectiveCancellationToken))
                {
                    yield return message;
                }
            }
            else if (format == ReadableStreamFormat.ServerSentEvents)
            {
                await foreach (var message in ReadServerSentEventsAsync(stream, effectiveCancellationToken))
                {
                    yield return message;
                }
            }
            else if (format == ReadableStreamFormat.JsonArray)
            {
                await foreach (var message in ReadJsonArrayAsync(stream, options.Format == ReadableStreamFormat.Auto, effectiveCancellationToken))
                {
                    yield return message;
                }
            }
            else
            {
                await foreach (var message in ReadLinesAsync(stream, effectiveCancellationToken))
                {
                    yield return message;
                }
            }

            yield return new ReadableStreamMessage
            {
                Type = ReadableStreamMessageType.Completed
            };
        }
        finally
        {
            response?.Dispose();
        }
    }

    private ClientLease CreateHttpClient(ReadableExecutionContext context)
    {
        if (_client is not null)
        {
            return new ClientLease(_client, false);
        }
        if (_httpClientFactory is not null)
        {
            var client = _httpClientFactory();
            if (context.HasTimeoutOverride) client.Timeout = context.Timeout;
            return new ClientLease(client, true);
        }

        var handler = _handler ?? CreateHandler(context);

        var httpClient = new HttpClient(handler, disposeHandler: _handler is null)
        {
            BaseAddress = context.BaseAddress
        };
        if (context.HasTimeoutOverride)
        {
            httpClient.Timeout = context.Timeout;
        }

        return new ClientLease(httpClient, true);
    }

    private static CancellationTokenSource? CreateTimeoutCancellationTokenSource(
        ReadableExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.HasTimeoutOverride || context.Timeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(context.Timeout);
        return timeoutCancellation;
    }

    private static HttpClientHandler CreateHandler(ReadableExecutionContext context)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = context.UseCookies,
            CookieContainer = new CookieContainer()
        };

        ApplyProxy(handler, context.Proxy);

        if (context.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        foreach (var cookie in context.Cookies)
        {
            if (!string.IsNullOrWhiteSpace(cookie.Domain))
            {
                handler.CookieContainer.Add(new Cookie(cookie.Name, cookie.Value ?? string.Empty, cookie.Path ?? "/", cookie.Domain));
            }
        }

        return handler;
    }

    private static async Task<ReadableSendResult> SendWithRedirectsAsync(
        HttpClient httpClient,
        ReadableRequest request,
        ReadableExecutionContext context,
        CancellationToken cancellationToken,
        bool capturePreview = true)
    {
        var redirects = new List<ReadableRedirect>();
        var currentUrl = request.Url;
        var currentMethod = request.Method;
        var includeBody = true;
        var stripCredentials = false;
        string? rawRequestPreview = null;

        for (var redirectCount = 0; redirectCount < 10; redirectCount++)
        {
            var hopRequest = redirectCount == 0 ? request : CloneForHop(request, currentUrl, currentMethod, includeBody);
            if (stripCredentials)
            {
                if (httpClient.DefaultRequestHeaders.Any(header =>
                    header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Cannot follow a cross-origin redirect with credentials in HttpClient.DefaultRequestHeaders. Set authentication on the request instead.");
                }
                hopRequest.Auth = new ReadableAuth { Type = ReadableAuthType.None };
                var auth = request.Auth is null or { Type: ReadableAuthType.Inherit } ? context.Auth : request.Auth;
                hopRequest.Headers.RemoveAll(header =>
                    header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                    header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    (auth?.Type == ReadableAuthType.ApiKey && header.Name.Equals(auth.Name, StringComparison.OrdinalIgnoreCase)) ||
                    (auth?.Type == ReadableAuthType.OAuth2 && header.Name.Equals(auth.OAuth2?.TokenName, StringComparison.OrdinalIgnoreCase)));
            }
            using var httpRequest = ReadableHttpRequestMessageFactory.Create(hopRequest, context);
            if (httpRequest.RequestUri is { IsAbsoluteUri: false } && httpClient.BaseAddress is not null)
            {
                httpRequest.RequestUri = new Uri(httpClient.BaseAddress, httpRequest.RequestUri);
            }
            if (capturePreview)
            {
                rawRequestPreview ??= await CreateRawRequestPreviewAsync(httpRequest, cancellationToken);
            }

            var response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!context.FollowRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                return new ReadableSendResult(response, redirects, rawRequestPreview ?? string.Empty);
            }

            string nextUrl;
            try
            {
                var currentUri = httpRequest.RequestUri!;
                nextUrl = ResolveRedirectUrl(currentUri.AbsoluteUri, response.Headers.Location);
                var nextUri = new Uri(nextUrl);
                stripCredentials |= !string.Equals(currentUri.Scheme, nextUri.Scheme, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(currentUri.Authority, nextUri.Authority, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                response.Dispose();
                throw;
            }
            redirects.Add(new ReadableRedirect
            {
                StatusCode = (int)response.StatusCode,
                Location = nextUrl
            });

            if ((response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found && currentMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                || (response.StatusCode == HttpStatusCode.SeeOther && !currentMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
            {
                currentMethod = "GET";
                includeBody = false;
            }

            currentUrl = nextUrl;
            response.Dispose();
        }

        throw new InvalidOperationException("Too many HTTP redirects.");
    }

    private static ReadableRequest CloneForHop(ReadableRequest request, string url, string method, bool includeBody)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var clone = JsonSerializer.Deserialize<ReadableRequest>(json, JsonOptions) ?? new ReadableRequest();
        clone.Url = url;
        clone.Query.Clear();
        clone.PathParameters.Clear();
        clone.Method = method;
        if (!includeBody)
        {
            clone.Body = null;
        }

        return clone;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static string ResolveRedirectUrl(string currentUrl, Uri location)
    {
        if (location.IsAbsoluteUri)
        {
            return location.ToString();
        }

        return new Uri(new Uri(currentUrl, UriKind.Absolute), location).ToString();
    }

    private static void ApplyProxy(HttpClientHandler handler, ReadableProxyOptions? proxy)
    {
        if (proxy is null || proxy.Mode == ReadableProxyMode.System)
        {
            handler.UseProxy = true;
            return;
        }

        if (proxy.Mode == ReadableProxyMode.None)
        {
            handler.UseProxy = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(proxy.Url))
        {
            throw new InvalidOperationException("Custom proxy requires Url.");
        }

        var webProxy = new WebProxy(proxy.Url);
        if (!string.IsNullOrWhiteSpace(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
        }

        handler.Proxy = webProxy;
        handler.UseProxy = true;
    }

    private static void ApplyRequestOptions(ReadableRequest request, ReadableExecutionContext context)
    {
        if (request.Options.Timeout is { } timeout)
        {
            context.Timeout = timeout;
        }

        if (request.Options.FollowRedirects is { } followRedirects)
        {
            context.FollowRedirects = followRedirects;
        }

        if (request.Options.UseCookies is { } useCookies)
        {
            context.UseCookies = useCookies;
        }

        if (request.Options.IgnoreSslErrors is { } ignoreSslErrors)
        {
            context.IgnoreSslErrors = ignoreSslErrors;
        }

        if (request.Options.Proxy is not null)
        {
            context.Proxy = request.Options.Proxy;
        }
    }

    private static List<ReadableNameValue> ReadHeaders(HttpResponseMessage response)
    {
        return response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(header => header.Value.Select(value => new ReadableNameValue
            {
                Name = header.Key,
                Value = value,
                Enabled = true
            }))
            .ToList();
    }

    private static List<ReadableCookie> ReadCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return [];
        }

        return values.Select(value =>
        {
            var segments = value.Split(';', StringSplitOptions.TrimEntries);
            var first = segments[0];
            var pair = first.Split('=', 2);
            var cookie = new ReadableCookie
            {
                Name = pair[0],
                Value = pair.Length > 1 ? pair[1] : string.Empty
            };

            foreach (var segment in segments.Skip(1))
            {
                var attribute = segment.Split('=', 2);
                var name = attribute[0];
                var attributeValue = attribute.Length > 1 ? attribute[1] : null;

                if (string.Equals(name, "Domain", StringComparison.OrdinalIgnoreCase))
                {
                    cookie.Domain = attributeValue;
                }
                else if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
                {
                    cookie.Path = attributeValue;
                }
                else if (string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParse(attributeValue, out var expires))
                {
                    cookie.Expires = expires;
                }
                else if (string.Equals(name, "Max-Age", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(attributeValue, out var maxAge))
                {
                    cookie.Expires = DateTimeOffset.UtcNow.AddSeconds(maxAge);
                }
                else if (string.Equals(name, "Secure", StringComparison.OrdinalIgnoreCase))
                {
                    cookie.Secure = true;
                }
                else if (string.Equals(name, "HttpOnly", StringComparison.OrdinalIgnoreCase))
                {
                    cookie.HttpOnly = true;
                }
            }

            return cookie;
        }).ToList();
    }

    private static async Task<string> CreateRawRequestPreviewAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append(request.Method).Append(' ').Append(request.RequestUri).AppendLine(" HTTP/1.1");
        foreach (var header in request.Headers)
        {
            foreach (var value in header.Value)
            {
                builder.Append(header.Key).Append(": ").AppendLine(value);
            }
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                foreach (var value in header.Value)
                {
                    builder.Append(header.Key).Append(": ").AppendLine(value);
                }
            }

            builder.AppendLine();
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            builder.Append(body);
        }

        return builder.ToString();
    }

    private static ReadableStreamFormat ResolveStreamFormat(ReadableStreamFormat format, MediaTypeHeaderValue? contentType)
    {
        if (format != ReadableStreamFormat.Auto)
        {
            return format;
        }

        var mediaType = contentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return ReadableStreamFormat.ServerSentEvents;
        }

        if (mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ReadableStreamFormat.JsonArray;
        }

        if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ReadableStreamFormat.Lines;
        }

        return ReadableStreamFormat.Raw;
    }

    private static async IAsyncEnumerable<ReadableStreamMessage> ReadRawStreamAsync(
        Stream stream,
        int bufferSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var buffer = new char[bufferSize];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            yield return new ReadableStreamMessage
            {
                Type = ReadableStreamMessageType.Data,
                Data = new string(buffer, 0, read)
            };
        }
    }
    private static async IAsyncEnumerable<ReadableStreamMessage> ReadLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            yield return new ReadableStreamMessage
            {
                Type = ReadableStreamMessageType.Data,
                Data = line,
                Raw = line
            };
        }
    }

    private static async IAsyncEnumerable<ReadableStreamMessage> ReadJsonArrayAsync(
        Stream stream,
        bool allowRawFallback,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var prefix = new MemoryStream();
        var probe = new byte[1];
        var read = 0;
        if (allowRawFallback)
        {
            do
            {
                read = await stream.ReadAsync(probe, cancellationToken);
                if (read != 0) prefix.WriteByte(probe[0]);
            } while (read != 0 && prefix.Length < 4096 && probe[0] is 9 or 10 or 13 or 32);
        }

        using var replay = new PrefixReadStream(prefix.ToArray(), stream);
        if (allowRawFallback && (read == 0 || probe[0] != '['))
        {
            await foreach (var message in ReadRawStreamAsync(replay, 8192, cancellationToken)) yield return message;
            yield break;
        }

        await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(replay, cancellationToken: cancellationToken))
        {
            var raw = element.GetRawText();
            yield return new ReadableStreamMessage
            {
                Type = ReadableStreamMessageType.Data,
                Data = element.ValueKind == JsonValueKind.String ? element.GetString() : raw,
                Raw = raw
            };
        }
    }
    private static async IAsyncEnumerable<ReadableStreamMessage> ReadServerSentEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string? eventName = null;
        string? eventId = null;
        var data = new StringBuilder();
        var raw = new StringBuilder();
        var pending = new StringBuilder();
        var buffer = new char[1024];
        bool? parseAsSse = null;

        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var chunk = new string(buffer, 0, read);
            if (parseAsSse == false)
            {
                yield return new ReadableStreamMessage
                {
                    Type = ReadableStreamMessageType.Data,
                    Data = chunk
                };
                continue;
            }

            pending.Append(chunk);
            if (parseAsSse is null)
            {
                var sniff = pending.ToString().TrimStart();
                if (LooksLikeServerSentEvent(sniff))
                {
                    parseAsSse = true;
                }
                else if (!CouldBecomeServerSentEvent(sniff))
                {
                    parseAsSse = false;
                    yield return new ReadableStreamMessage
                    {
                        Type = ReadableStreamMessageType.Data,
                        Data = pending.ToString()
                    };
                    pending.Clear();
                    continue;
                }
            }

            if (parseAsSse is not true)
            {
                continue;
            }

            while (TryTakeLine(pending, out var line))
            {
                raw.AppendLine(line);
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        yield return new ReadableStreamMessage
                        {
                            Type = ReadableStreamMessageType.Data,
                            Event = eventName,
                            Id = eventId,
                            Data = data.ToString().TrimEnd('\n'),
                            Raw = raw.ToString()
                        };
                    }

                    eventName = null;
                    eventId = null;
                    data.Clear();
                    raw.Clear();
                    continue;
                }

                if (line.StartsWith(':'))
                {
                    continue;
                }

                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator < 0)
                {
                    data.Append(line).Append('\n');
                    continue;
                }

                var field = line[..separator];
                var value = line[(separator + 1)..].TrimStart(' ');

                switch (field)
                {
                    case "event":
                        eventName = value;
                        break;
                    case "id":
                        eventId = value;
                        break;
                    case "data":
                        data.Append(value).Append('\n');
                        break;
                }
            }
        }

        if (parseAsSse == false)
        {
            if (pending.Length > 0)
            {
                yield return new ReadableStreamMessage
                {
                    Type = ReadableStreamMessageType.Data,
                    Data = pending.ToString()
                };
            }
        }
        else
        {
            if (pending.Length > 0)
            {
                var line = pending.ToString();
                raw.Append(line);
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line["data:".Length..].TrimStart(' ')).Append('\n');
                }
                else if (!line.StartsWith(':'))
                {
                    data.Append(line).Append('\n');
                }
            }

            if (data.Length > 0)
            {
                yield return new ReadableStreamMessage
                {
                    Type = ReadableStreamMessageType.Data,
                    Event = eventName,
                    Id = eventId,
                    Data = data.ToString().TrimEnd('\n'),
                    Raw = raw.ToString()
                };
            }
        }
    }

    private static bool LooksLikeServerSentEvent(string value)
    {
        return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("retry:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(':');
    }

    private static bool CouldBecomeServerSentEvent(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        return "data:".StartsWith(value, StringComparison.OrdinalIgnoreCase)
            || "event:".StartsWith(value, StringComparison.OrdinalIgnoreCase)
            || "id:".StartsWith(value, StringComparison.OrdinalIgnoreCase)
            || "retry:".StartsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryTakeLine(StringBuilder pending, out string line)
    {
        for (var index = 0; index < pending.Length; index++)
        {
            if (pending[index] != '\n')
            {
                continue;
            }

            var length = index > 0 && pending[index - 1] == '\r' ? index - 1 : index;
            line = pending.ToString(0, length);
            pending.Remove(0, index + 1);
            return true;
        }

        line = string.Empty;
        return false;
    }

    private static string? TryReadText(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var mediaType = contentType?.MediaType;
        var isText = mediaType is null
            || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);

        if (!isText)
        {
            return null;
        }

        var encoding = TryGetEncoding(contentType?.CharSet) ?? Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    private static Encoding? TryGetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record ReadableSendResult(
        HttpResponseMessage Response,
        List<ReadableRedirect> Redirects,
        string RawRequestPreview);

    private sealed record ClientLease(HttpClient Client, bool OwnsClient) : IDisposable
    {
        public void Dispose()
        {
            if (OwnsClient) Client.Dispose();
        }
    }
}
