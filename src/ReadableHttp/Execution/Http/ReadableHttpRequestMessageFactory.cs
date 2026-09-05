using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReadableHttp;

namespace ReadableHttp.Execution;

internal static class ReadableHttpRequestMessageFactory
{
    public static HttpRequestMessage Create(ReadableRequest request, ReadableExecutionContext context)
    {
        var query = request.Query.Where(item => item.Enabled).ToList();
        var auth = request.Auth is null or { Type: ReadableAuthType.Inherit } ? context.Auth : request.Auth;
        ApplyAuth(auth, request, query);

        var url = BuildUrl(request, query);
        if (context.BaseAddress is not null)
        {
            url = new Uri(context.BaseAddress, url).AbsoluteUri;
        }
        var message = new HttpRequestMessage(new HttpMethod(request.Method), url)
        {
            Content = CreateContent(request.Body)
        };

        ApplyAuthHeader(auth, request, message);

        foreach (var header in request.Headers.Where(header => header.Enabled))
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            var value = header.Value ?? string.Empty;
            if (message.Headers.TryAddWithoutValidation(header.Name, value))
            {
                continue;
            }

            if (message.Content is not null)
            {
                message.Content.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        return message;
    }

    public static string BuildUrl(ReadableRequest request)
    {
        return BuildUrl(request, request.Query.Where(item => item.Enabled));
    }

    private static string BuildUrl(ReadableRequest request, IEnumerable<ReadableNameValue> query)
    {
        var url = request.Url;
        foreach (var parameter in request.PathParameters.Where(parameter => parameter.Enabled))
        {
            url = url.Replace("{{" + parameter.Name + "}}", Uri.EscapeDataString(parameter.Value ?? string.Empty), StringComparison.OrdinalIgnoreCase)
                .Replace("{" + parameter.Name + "}", Uri.EscapeDataString(parameter.Value ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        }

        var enabledQuery = query
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value ?? string.Empty)}")
            .ToArray();

        if (enabledQuery.Length == 0)
        {
            return url;
        }

        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        url = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var separator = url.Contains('?', StringComparison.Ordinal)
            ? url.EndsWith('?') || url.EndsWith('&') ? string.Empty : "&"
            : "?";

        return $"{url}{separator}{string.Join("&", enabledQuery)}{fragment}";
    }

    private static HttpContent? CreateContent(ReadableBody? body)
    {
        if (body is null || body.Type == ReadableBodyType.None)
        {
            return null;
        }

        return body.Type switch
        {
            ReadableBodyType.Json => new StringContent(body.Content ?? string.Empty, Encoding.UTF8, body.ContentType ?? "application/json"),
            ReadableBodyType.Xml => new StringContent(body.Content ?? string.Empty, Encoding.UTF8, body.ContentType ?? "application/xml"),
            ReadableBodyType.Html => new StringContent(body.Content ?? string.Empty, Encoding.UTF8, body.ContentType ?? "text/html"),
            ReadableBodyType.Javascript => new StringContent(body.Content ?? string.Empty, Encoding.UTF8, body.ContentType ?? "application/javascript"),
            ReadableBodyType.Raw => new StringContent(body.Content ?? string.Empty, Encoding.UTF8, body.ContentType ?? "text/plain"),
            ReadableBodyType.FormUrlEncoded => new FormUrlEncodedContent(
                body.Form
                    .Where(item => item.Enabled)
                    .Select(item => new KeyValuePair<string, string>(item.Name, item.Value ?? string.Empty))),
            ReadableBodyType.MultipartFormData => CreateMultipartContent(body),
            ReadableBodyType.BinaryFile => CreateFileContent(body),
            ReadableBodyType.Graphql => CreateGraphqlContent(body),
            _ => null
        };
    }

    private static StringContent CreateGraphqlContent(ReadableBody body)
    {
        var graphql = body.Graphql ?? new ReadableGraphqlBody
        {
            Query = body.Content ?? string.Empty
        };

        var payload = new JsonObject
        {
            ["query"] = graphql.Query
        };

        if (!string.IsNullOrWhiteSpace(graphql.OperationName))
        {
            payload["operationName"] = graphql.OperationName;
        }

        if (!string.IsNullOrWhiteSpace(graphql.Variables))
        {
            payload["variables"] = TryParseJson(graphql.Variables) ?? graphql.Variables;
        }

        return new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            body.ContentType ?? "application/json");
    }

    private static JsonNode? TryParseJson(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MultipartFormDataContent CreateMultipartContent(ReadableBody body)
    {
        var content = new MultipartFormDataContent();
        foreach (var item in body.Form.Where(item => item.Enabled))
        {
            content.Add(new StringContent(item.Value ?? string.Empty), item.Name);
        }

        foreach (var item in body.Multipart.Where(item => item.Enabled))
        {
            if (item.Type == ReadableMultipartItemType.File)
            {
                if (string.IsNullOrWhiteSpace(item.FilePath))
                {
                    throw new InvalidOperationException($"Multipart file item '{item.Name}' requires FilePath.");
                }

                var fileContent = new ByteArrayContent(File.ReadAllBytes(item.FilePath));
                if (!string.IsNullOrWhiteSpace(item.ContentType))
                {
                    fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(item.ContentType);
                }

                content.Add(fileContent, item.Name, item.FileName ?? Path.GetFileName(item.FilePath));
            }
            else
            {
                content.Add(new StringContent(item.Value ?? string.Empty), item.Name);
            }
        }

        return content;
    }

    private static HttpContent CreateFileContent(ReadableBody body)
    {
        if (string.IsNullOrWhiteSpace(body.FilePath))
        {
            throw new InvalidOperationException("Binary file body requires FilePath.");
        }

        var bytes = File.ReadAllBytes(body.FilePath);
        var content = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(body.ContentType))
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(body.ContentType);
        }

        return content;
    }

    private static void ApplyAuth(ReadableAuth? auth, ReadableRequest request, List<ReadableNameValue> query)
    {
        if (auth is null || auth.Type is ReadableAuthType.None or ReadableAuthType.Inherit)
        {
            return;
        }

        switch (auth.Type)
        {
            case ReadableAuthType.ApiKey when auth.ApiKeyLocation == ReadableApiKeyLocation.Query:
                AddQueryParameter(query, auth.Name, auth.Value);
                break;
            case ReadableAuthType.OAuth2 when auth.OAuth2 is not null:
                ApplyOAuth2Query(auth.OAuth2, query);
                break;
            case ReadableAuthType.OAuth1 when auth.OAuth1 is not null:
                ApplyOAuth1Query(auth.OAuth1, request, query);
                break;
        }
    }

    private static void ApplyAuthHeader(ReadableAuth? auth, ReadableRequest request, HttpRequestMessage message)
    {
        if (auth is null || auth.Type is ReadableAuthType.None or ReadableAuthType.Inherit)
        {
            return;
        }

        switch (auth.Type)
        {
            case ReadableAuthType.Basic:
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"));
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                break;
            case ReadableAuthType.Bearer:
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
                break;
            case ReadableAuthType.ApiKey when auth.ApiKeyLocation == ReadableApiKeyLocation.Header:
                if (!string.IsNullOrWhiteSpace(auth.Name))
                {
                    message.Headers.TryAddWithoutValidation(auth.Name, auth.Value ?? string.Empty);
                }
                break;
            case ReadableAuthType.OAuth2 when auth.OAuth2 is not null:
                ApplyOAuth2Header(auth.OAuth2, message);
                break;
            case ReadableAuthType.OAuth1 when auth.OAuth1 is not null:
                ApplyOAuth1Header(auth.OAuth1, message, request);
                break;
        }
    }

    private static void AddQueryParameter(List<ReadableNameValue> query, string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        query.Add(new ReadableNameValue
        {
            Name = name,
            Value = value,
            Enabled = true
        });
    }

    private static void ApplyOAuth2Header(ReadableOAuth2Options options, HttpRequestMessage message)
    {
        if (options.TokenPlacement != ReadableTokenPlacement.Header)
        {
            return;
        }

        var token = options.ExtraParameters.TryGetValue(options.TokenId, out var storedToken)
            ? storedToken
            : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var value = string.IsNullOrWhiteSpace(options.HeaderPrefix)
            ? token
            : $"{options.HeaderPrefix} {token}";

        message.Headers.TryAddWithoutValidation(options.TokenName, value);
    }

    private static void ApplyOAuth2Query(ReadableOAuth2Options options, List<ReadableNameValue> query)
    {
        if (options.TokenPlacement != ReadableTokenPlacement.Query)
        {
            return;
        }

        var token = options.ExtraParameters.TryGetValue(options.TokenId, out var storedToken)
            ? storedToken
            : null;

        AddQueryParameter(query, options.TokenName, token);
    }

    private static void ApplyOAuth1Header(ReadableOAuth1Options options, HttpRequestMessage message, ReadableRequest request)
    {
        if (options.Placement != ReadableTokenPlacement.Header
            || string.IsNullOrWhiteSpace(options.ConsumerKey)
            || string.IsNullOrWhiteSpace(options.ConsumerSecret))
        {
            return;
        }

        var parameters = CreateOAuth1Parameters(options, request.Method, BuildUrl(request));
        var header = "OAuth " + string.Join(", ", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}=\"{Uri.EscapeDataString(pair.Value)}\""));
        message.Headers.TryAddWithoutValidation("Authorization", header);
    }

    private static void ApplyOAuth1Query(ReadableOAuth1Options options, ReadableRequest request, List<ReadableNameValue> query)
    {
        if (options.Placement != ReadableTokenPlacement.Query
            || string.IsNullOrWhiteSpace(options.ConsumerKey)
            || string.IsNullOrWhiteSpace(options.ConsumerSecret))
        {
            return;
        }

        var parameters = CreateOAuth1Parameters(options, request.Method, BuildUrl(request, query));
        foreach (var parameter in parameters)
        {
            AddQueryParameter(query, parameter.Key, parameter.Value);
        }
    }

    private static SortedDictionary<string, string> CreateOAuth1Parameters(
        ReadableOAuth1Options options,
        string method,
        string url)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = options.ConsumerKey ?? string.Empty,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = ToOAuth1SignatureMethod(options.SignatureMethod),
            ["oauth_timestamp"] = timestamp,
            ["oauth_version"] = options.Version
        };

        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            parameters["oauth_token"] = options.Token;
        }

        parameters["oauth_signature"] = CreateOAuth1Signature(options, method, url, parameters);
        return parameters;
    }

    private static string CreateOAuth1Signature(
        ReadableOAuth1Options options,
        string method,
        string url,
        SortedDictionary<string, string> parameters)
    {
        if (options.SignatureMethod == ReadableOAuth1SignatureMethod.PlainText)
        {
            return $"{Uri.EscapeDataString(options.ConsumerSecret ?? string.Empty)}&{Uri.EscapeDataString(options.TokenSecret ?? string.Empty)}";
        }

        var normalizedParameters = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var normalizedUrl = url.Split('?', 2)[0];
        var baseString = string.Join("&",
            method.ToUpperInvariant(),
            Uri.EscapeDataString(normalizedUrl),
            Uri.EscapeDataString(normalizedParameters));
        var key = $"{Uri.EscapeDataString(options.ConsumerSecret ?? string.Empty)}&{Uri.EscapeDataString(options.TokenSecret ?? string.Empty)}";

        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(baseString)));
    }

    private static string ToOAuth1SignatureMethod(ReadableOAuth1SignatureMethod method)
    {
        return method switch
        {
            ReadableOAuth1SignatureMethod.PlainText => "PLAINTEXT",
            ReadableOAuth1SignatureMethod.RsaSha1 => "RSA-SHA1",
            _ => "HMAC-SHA1"
        };
    }
}
