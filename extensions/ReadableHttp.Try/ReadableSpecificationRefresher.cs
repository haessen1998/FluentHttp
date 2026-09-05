using ReadableHttp;
using ReadableHttp.Storage;
using System.Security.Cryptography;
using System.Text;

namespace ReadableHttp.Try;

public sealed class ReadableSpecificationRefresher
{
    private readonly HttpClient _httpClient;
    private readonly ReadableHttpJsonStorage _storage;

    public ReadableSpecificationRefresher(HttpClient? httpClient = null, ReadableHttpJsonStorage? storage = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _storage = storage ?? new ReadableHttpJsonStorage();
    }

    public async Task<ReadableTryDocument> RefreshAsync(
        string workspacePath,
        ReadableSpecification specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var content = specification.SourceType switch
        {
            ReadableSpecificationSourceType.LocalFile => await ReadLocalAsync(workspacePath, specification, cancellationToken),
            ReadableSpecificationSourceType.RemoteEndpoint => await ReadRemoteAsync(workspacePath, specification, cancellationToken),
            ReadableSpecificationSourceType.GitFile => await ReadGitFileAsync(workspacePath, specification, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported specification source '{specification.SourceType}'.")
        };

        var previousChecksum = specification.Checksum;
        var checksum = ComputeChecksum(content);
        specification.Checksum = checksum;
        if (specification.Remote is not null)
        {
            specification.Remote.UpdateAvailable = !string.IsNullOrWhiteSpace(previousChecksum)
                && !string.Equals(previousChecksum, checksum, StringComparison.OrdinalIgnoreCase);
            specification.Remote.Checksum = checksum;
        }

        var extension = GuessExtension(specification);
        if (specification.SourceType == ReadableSpecificationSourceType.RemoteEndpoint
            && !string.IsNullOrWhiteSpace(specification.Remote?.Endpoint))
        {
            specification.Path = GetHashedSourcePath(specification, checksum, extension);
            var sourcePath = ResolveWorkspacePath(workspacePath, specification.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath) ?? workspacePath);
            await File.WriteAllTextAsync(sourcePath, content, cancellationToken);
        }

        var document = new ReadableTryDocumentLoader().Load(content, specification.Name, extension);
        foreach (var variable in specification.Variables)
        {
            document.Variables[variable.Key] = variable.Value;
        }

        specification.LastNormalizedAt = DateTimeOffset.UtcNow;
        specification.NormalizedPath = GetNormalizedPath(specification, checksum);
        await _storage.SaveAsync(
            ResolveWorkspacePath(workspacePath, specification.NormalizedPath),
            document,
            cancellationToken);

        return document;
    }

    private static async Task<string> ReadLocalAsync(
        string workspacePath,
        ReadableSpecification specification,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(specification.Path))
        {
            throw new InvalidOperationException("Local specifications require Path.");
        }

        return await File.ReadAllTextAsync(
            ResolveWorkspacePath(workspacePath, specification.Path),
            cancellationToken);
    }

    private static Task<string> ReadGitFileAsync(
        string workspacePath,
        ReadableSpecification specification,
        CancellationToken cancellationToken)
    {
        var path = specification.Path ?? specification.Git?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Git file specifications require Path or Git.Path.");
        }

        return File.ReadAllTextAsync(ResolveWorkspacePath(workspacePath, path), cancellationToken);
    }

    private async Task<string> ReadRemoteAsync(
        string workspacePath,
        ReadableSpecification specification,
        CancellationToken cancellationToken)
    {
        if (specification.Remote is null || string.IsNullOrWhiteSpace(specification.Remote.Endpoint))
        {
            throw new InvalidOperationException("Remote specifications require an endpoint.");
        }

        using var request = new HttpRequestMessage(new HttpMethod(specification.Remote.Method), specification.Remote.Endpoint);
        foreach (var header in specification.Remote.Headers.Where(header => header.Enabled))
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        specification.Remote.LastRefreshedAt = DateTimeOffset.UtcNow;
        specification.Remote.ETag = response.Headers.ETag?.Tag;

        return content;
    }

    private static string ResolveWorkspacePath(string workspacePath, string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(workspacePath, path);
    }

    private static string GuessExtension(ReadableSpecification specification)
    {
        if (!string.IsNullOrWhiteSpace(specification.Path))
        {
            var extension = Path.GetExtension(specification.Path);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return extension;
            }
        }

        return specification.Format switch
        {
            ReadableSpecificationFormat.Http => ".http",
            ReadableSpecificationFormat.Curl => ".curl",
            ReadableSpecificationFormat.Har => ".har",
            ReadableSpecificationFormat.PostmanCollection => ".postman_collection.json",
            ReadableSpecificationFormat.ReadableRequest => ".json",
            ReadableSpecificationFormat.OpenApi or ReadableSpecificationFormat.Swagger => ".json",
            _ => ".json"
        };
    }

    private static string ComputeChecksum(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetHashedSourcePath(
        ReadableSpecification specification,
        string checksum,
        string extension)
    {
        return Path.Combine(
            "specs",
            $"{ToFileName(specification.Name)}.{checksum[..12]}{extension}")
            .Replace('\\', '/');
    }

    private static string GetNormalizedPath(ReadableSpecification specification, string checksum)
    {
        return Path.Combine(
            ".readablehttp",
            "cache",
            "specifications",
            $"{ToFileName(specification.Name)}.{checksum[..12]}.trydoc.json")
            .Replace('\\', '/');
    }

    private static string ToFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "specification" : sanitized;
    }
}
