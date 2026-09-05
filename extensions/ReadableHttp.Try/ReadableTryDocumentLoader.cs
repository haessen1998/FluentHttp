using System.Text.Json;
using System.Text.Json.Nodes;
using ReadableHttp;
using ReadableHttp.ImportExport;
using ReadableHttp.ImportExport.OpenApi;
using ReadableHttp.Storage;

namespace ReadableHttp.Try;

public sealed class ReadableTryDocumentLoader
{
    private readonly OpenApiRequestFactory _openApiRequestFactory = new();

    public async Task<ReadableTryDocument> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        return Load(content, Path.GetFileName(filePath), Path.GetExtension(filePath));
    }

    public ReadableTryDocument Load(string content, string? fileName = null, string? extension = null)
    {
        var sourceType = DetectSourceType(content, extension);
        return sourceType switch
        {
            ReadableTrySourceType.OpenApi => LoadOpenApi(content, fileName, extension),
            ReadableTrySourceType.HttpFile => LoadHttp(content, fileName),
            ReadableTrySourceType.Curl => LoadCurl(content, fileName),
            ReadableTrySourceType.ReadableRequest => LoadReadableRequest(content, fileName),
            _ => new ReadableTryDocument
            {
                FileName = fileName,
                RawContent = content,
                SourceType = ReadableTrySourceType.Unknown
            }
        };
    }

    private ReadableTryDocument LoadOpenApi(string content, string? fileName, string? extension)
    {
        var document = OpenApiRequestFactory.ParseDocument(content, extension ?? string.Empty);
        var result = new ReadableTryDocument
        {
            FileName = fileName,
            RawContent = content,
            SourceType = ReadableTrySourceType.OpenApi,
            Title = document["info"]?["title"]?.GetValue<string>(),
            Version = document["info"]?["version"]?.GetValue<string>()
        };

        foreach (var operation in _openApiRequestFactory.CreateRequests(document))
        {
            result.Operations.Add(new ReadableTryOperation
            {
                Id = operation.OperationId ?? $"{operation.Request.Method} {operation.Path}",
                Name = operation.Request.Name,
                Method = operation.Request.Method,
                Path = operation.Path,
                Summary = operation.Summary,
                Tags = operation.Tags,
                Request = operation.Request
            });
        }

        return result;
    }

    private static ReadableTryDocument LoadHttp(string content, string? fileName)
    {
        var requests = new HttpFileConverter().Import(content);
        return new ReadableTryDocument
        {
            FileName = fileName,
            RawContent = content,
            SourceType = ReadableTrySourceType.HttpFile,
            Title = fileName,
            Operations = requests.Select(request => new ReadableTryOperation
            {
                Id = request.Id,
                Name = request.Name,
                Method = request.Method,
                Path = request.Url,
                Request = request
            }).ToList()
        };
    }

    private static ReadableTryDocument LoadCurl(string content, string? fileName)
    {
        var request = new CurlConverter().Import(content);
        return new ReadableTryDocument
        {
            FileName = fileName,
            RawContent = content,
            SourceType = ReadableTrySourceType.Curl,
            Title = fileName,
            Operations =
            [
                new ReadableTryOperation
                {
                    Id = request.Id,
                    Name = request.Name,
                    Method = request.Method,
                    Path = request.Url,
                    Request = request
                }
            ]
        };
    }

    private static ReadableTryDocument LoadReadableRequest(string content, string? fileName)
    {
        var request = JsonSerializer.Deserialize<ReadableRequest>(content, ReadableHttpJsonStorage.JsonOptions)
            ?? throw new InvalidOperationException("ReadableRequest JSON is empty.");
        return new ReadableTryDocument
        {
            FileName = fileName,
            RawContent = content,
            SourceType = ReadableTrySourceType.ReadableRequest,
            Title = request.Name,
            Operations =
            [
                new ReadableTryOperation
                {
                    Id = request.Id,
                    Name = request.Name,
                    Method = request.Method,
                    Path = request.Url,
                    Request = request
                }
            ]
        };
    }

    private static ReadableTrySourceType DetectSourceType(string content, string? extension)
    {
        if (extension?.Equals(".http", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ReadableTrySourceType.HttpFile;
        }

        if (extension?.Equals(".curl", StringComparison.OrdinalIgnoreCase) == true
            || content.TrimStart().StartsWith("curl ", StringComparison.OrdinalIgnoreCase))
        {
            return ReadableTrySourceType.Curl;
        }

        if (extension?.Equals(".yaml", StringComparison.OrdinalIgnoreCase) == true
            || extension?.Equals(".yml", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ReadableTrySourceType.OpenApi;
        }

        if (JsonNode.Parse(content) is { } node)
        {
            if (node["openapi"] is not null || node["swagger"] is not null)
            {
                return ReadableTrySourceType.OpenApi;
            }

            if (node["method"] is not null && node["url"] is not null)
            {
                return ReadableTrySourceType.ReadableRequest;
            }
        }

        return ReadableTrySourceType.Unknown;
    }
}
