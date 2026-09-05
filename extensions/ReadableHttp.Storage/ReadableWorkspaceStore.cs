using ReadableHttp;
using ReadableHttp.ImportExport;
using System.Text.Json;

namespace ReadableHttp.Storage;

public sealed class ReadableWorkspaceStore
{
    private readonly ReadableHttpJsonStorage _jsonStorage;

    public ReadableWorkspaceStore(ReadableHttpJsonStorage? jsonStorage = null)
    {
        _jsonStorage = jsonStorage ?? new ReadableHttpJsonStorage();
    }

    public Task<ReadableWorkspace> LoadWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        return _jsonStorage.LoadAsync<ReadableWorkspace>(
            Path.Combine(workspacePath, "workspace.json"),
            cancellationToken);
    }

    public Task SaveWorkspaceAsync(
        string workspacePath,
        ReadableWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CreateWorkspaceSnapshot(workspace);
        return _jsonStorage.SaveAsync(
            Path.Combine(workspacePath, "workspace.json"),
            snapshot,
            cancellationToken);
    }

    public async Task<ReadableRequest> LoadRequestAsync(
        string workspacePath,
        string requestNameOrId,
        CancellationToken cancellationToken = default)
    {
        var collectionsDirectory = Path.Combine(workspacePath, "collections");
        if (!Directory.Exists(collectionsDirectory))
        {
            throw new FileNotFoundException($"Request '{requestNameOrId}' was not found in workspace '{workspacePath}'.");
        }

        var requestFiles = Directory.EnumerateFiles(
            collectionsDirectory,
            "*.json",
            SearchOption.AllDirectories);

        foreach (var file in requestFiles)
        {
            var request = await TryLoadRequestAsync(file, cancellationToken);
            if (request is null)
            {
                continue;
            }

            if (Matches(request.Id, requestNameOrId) || Matches(request.Name, requestNameOrId))
            {
                return request;
            }
        }

        throw new FileNotFoundException($"Request '{requestNameOrId}' was not found in workspace '{workspacePath}'.");
    }

    public async Task<IReadOnlyList<ReadableRequest>> LoadCollectionRequestsAsync(
        string workspacePath,
        ReadableCollection collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var requestDirectory = GetCollectionRequestDirectory(workspacePath, collection);
        if (!Directory.Exists(requestDirectory))
        {
            return collection.Requests;
        }

        var requests = new List<ReadableRequest>();
        foreach (var file in Directory.EnumerateFiles(requestDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var request = await TryLoadRequestAsync(file, cancellationToken);
            if (request is not null)
            {
                request.SourcePath = ToWorkspaceRelativePath(workspacePath, file);
                requests.Add(request);
            }
        }

        collection.Requests = requests;
        return requests;
    }

    public async Task SaveCollectionRequestsAsync(
        string workspacePath,
        ReadableCollection collection,
        IEnumerable<ReadableRequest> requests,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var requestDirectory = GetCollectionRequestDirectory(workspacePath, collection);
        Directory.CreateDirectory(requestDirectory);

        collection.Requests = requests.ToList();
        foreach (var request in collection.Requests)
        {
            await SaveRequestAsync(workspacePath, collection, request, cancellationToken);
        }
    }

    public async Task SaveRequestAsync(
        string workspacePath,
        ReadableCollection collection,
        ReadableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(request);

        var requestDirectory = GetCollectionRequestDirectory(workspacePath, collection);
        Directory.CreateDirectory(requestDirectory);

        var existingPath = ResolveExistingRequestPath(workspacePath, collection, request);
        var desiredPath = Path.Combine(requestDirectory, $"{ToFileName(request.Name)}.json");
        var targetPath = ResolveRequestSavePath(existingPath, desiredPath);
        if (existingPath is not null
            && !string.Equals(Path.GetFullPath(existingPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(existingPath, targetPath);
        }

        await _jsonStorage.SaveAsync(targetPath, request, cancellationToken);
        request.SourcePath = ToWorkspaceRelativePath(workspacePath, targetPath);
    }

    public Task DeleteRequestAsync(
        string workspacePath,
        ReadableCollection collection,
        ReadableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(request);

        var requestPath = ResolveExistingRequestPath(workspacePath, collection, request);
        if (requestPath is not null && File.Exists(requestPath))
        {
            File.Delete(requestPath);
        }

        request.SourcePath = null;
        return Task.CompletedTask;
    }

    public async Task MoveRequestAsync(
        string workspacePath,
        ReadableCollection sourceCollection,
        ReadableCollection targetCollection,
        ReadableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceCollection);
        ArgumentNullException.ThrowIfNull(targetCollection);
        ArgumentNullException.ThrowIfNull(request);

        var targetDirectory = GetCollectionRequestDirectory(workspacePath, targetCollection);
        Directory.CreateDirectory(targetDirectory);
        var targetPath = NextAvailablePath(targetDirectory, $"{ToFileName(request.Name)}.json");
        var sourcePath = ResolveExistingRequestPath(workspacePath, sourceCollection, request);
        if (sourcePath is not null && File.Exists(sourcePath))
        {
            File.Move(sourcePath, targetPath);
            request.SourcePath = ToWorkspaceRelativePath(workspacePath, targetPath);
            return;
        }

        await _jsonStorage.SaveAsync(targetPath, request, cancellationToken);
        request.SourcePath = ToWorkspaceRelativePath(workspacePath, targetPath);
    }

    public Task DeleteCollectionRequestsAsync(
        string workspacePath,
        ReadableCollection collection,
        CancellationToken cancellationToken = default)
    {
        var requestDirectory = GetCollectionRequestDirectory(workspacePath, collection);
        if (Directory.Exists(requestDirectory))
        {
            Directory.Delete(requestDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public async Task<ReadableEnvironment?> LoadEnvironmentAsync(
        string workspacePath,
        string environmentNameOrId,
        CancellationToken cancellationToken = default)
    {
        var environmentDirectory = Path.Combine(workspacePath, "environments");
        if (!Directory.Exists(environmentDirectory))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(environmentDirectory, "*.json", SearchOption.AllDirectories))
        {
            var environment = await _jsonStorage.LoadAsync<ReadableEnvironment>(file, cancellationToken);
            if (Matches(environment.Id, environmentNameOrId) || Matches(environment.Name, environmentNameOrId))
            {
                return environment;
            }
        }

        return null;
    }

    public async Task SaveExchangeAsync(
        string workspacePath,
        ReadableExchange exchange,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.Combine(workspacePath, "history"));
        var historyPath = Path.Combine(
            workspacePath,
            "history",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{exchange.Request.Id}.json");

        await _jsonStorage.SaveAsync(historyPath, exchange, cancellationToken);
        await AppendHistoryEntryAsync(workspacePath, exchange, historyPath, cancellationToken);
    }

    public Task<ReadableSecretStore> LoadSecretsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(workspacePath, ".readablehttp", "secrets.local.json");
        return File.Exists(path)
            ? _jsonStorage.LoadAsync<ReadableSecretStore>(path, cancellationToken)
            : Task.FromResult(new ReadableSecretStore());
    }

    public Task SaveSecretsAsync(string workspacePath, ReadableSecretStore secrets, CancellationToken cancellationToken = default)
    {
        return _jsonStorage.SaveAsync(
            Path.Combine(workspacePath, ".readablehttp", "secrets.local.json"),
            secrets,
            cancellationToken);
    }

    private async Task AppendHistoryEntryAsync(
        string workspacePath,
        ReadableExchange exchange,
        string exchangePath,
        CancellationToken cancellationToken)
    {
        var entry = new ReadableHistoryEntry
        {
            RequestId = exchange.Request.Id,
            RequestName = exchange.Request.Name,
            Method = exchange.Request.Method,
            Url = exchange.Request.Url,
            StatusCode = exchange.Response?.StatusCode,
            Duration = exchange.Response?.Duration ?? TimeSpan.Zero,
            StartedAt = exchange.StartedAt,
            ExchangePath = Path.GetRelativePath(workspacePath, exchangePath)
        };

        var indexPath = Path.Combine(workspacePath, "history", "index.jsonl");
        var json = System.Text.Json.JsonSerializer.Serialize(entry, ReadableHttpJsonStorage.JsonOptions);
        await File.AppendAllTextAsync(indexPath, json + Environment.NewLine, cancellationToken);
    }

    public async Task InitializeExampleAsync(
        string workspacePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(workspacePath) && Directory.EnumerateFileSystemEntries(workspacePath).Any() && !overwrite)
        {
            throw new InvalidOperationException($"Workspace path '{workspacePath}' is not empty.");
        }

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(Path.Combine(workspacePath, "collections"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "collections", "httpbin"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "environments"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "specs"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "history"));
        Directory.CreateDirectory(Path.Combine(workspacePath, ".readablehttp"));

        await _jsonStorage.SaveAsync(Path.Combine(workspacePath, "workspace.json"), new ReadableWorkspace
        {
            Name = "ReadableHttp Example Workspace",
            Type = ReadableWorkspaceType.Local,
            Collections =
            [
                new ReadableCollection
                {
                    Name = "HTTPBin Examples",
                    SourceType = ReadableCollectionSourceType.Local,
                    RequestDirectory = "collections/httpbin"
                }
            ],
            Specifications =
            [
                new ReadableSpecification
                {
                    Name = "HTTPBin OpenAPI",
                    SourceType = ReadableSpecificationSourceType.LocalFile,
                    Format = ReadableSpecificationFormat.OpenApi,
                    Path = "specs/httpbin.openapi.json"
                },
                new ReadableSpecification
                {
                    Name = "Remote OpenAPI Example",
                    SourceType = ReadableSpecificationSourceType.RemoteEndpoint,
                    Format = ReadableSpecificationFormat.OpenApi,
                    Path = "specs/remote-openapi.json",
                    Remote = new ReadableRemoteSpecificationOptions
                    {
                        Endpoint = "https://httpbin.org/spec.json"
                    }
                }
            ]
        }, cancellationToken);

        await _jsonStorage.SaveAsync(Path.Combine(workspacePath, "environments", "dev.json"), new ReadableEnvironment
        {
            Name = "dev",
            Variables =
            {
                ["baseUrl"] = "https://httpbin.org",
                ["keyword"] = "readable-http",
                ["apiKey"] = "sample-api-key"
            }
        }, cancellationToken);

        await _jsonStorage.SaveAsync(Path.Combine(workspacePath, "collections", "httpbin", "get-with-query.json"), new ReadableRequest
        {
            Name = "get-with-query",
            Method = "GET",
            Url = "{{baseUrl}}/get",
            Query =
            [
                new ReadableNameValue { Name = "keyword", Value = "{{keyword}}" }
            ],
            Headers =
            [
                new ReadableNameValue { Name = "accept", Value = "application/json" }
            ]
        }, cancellationToken);

        await _jsonStorage.SaveAsync(Path.Combine(workspacePath, "collections", "httpbin", "post-json.json"), new ReadableRequest
        {
            Name = "post-json",
            Method = "POST",
            Url = "{{baseUrl}}/post",
            Headers =
            [
                new ReadableNameValue { Name = "accept", Value = "application/json" }
            ],
            Body = new ReadableBody
            {
                Type = ReadableBodyType.Json,
                ContentType = "application/json",
                Content = "{\n  \"name\": \"ReadableHttp\",\n  \"source\": \"example-workspace\"\n}"
            }
        }, cancellationToken);

        await File.WriteAllTextAsync(Path.Combine(workspacePath, "specs", "httpbin.openapi.json"), CreateExampleOpenApi(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "specs", "httpbin.http"), CreateExampleHttpFile(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, ".gitignore"), "history/\n.readablehttp/secrets.local.json\n", cancellationToken);
    }

    public async Task<IReadOnlyList<ReadableRequest>> ImportOpenApiAsync(
        string workspacePath,
        string specPath,
        string? groupName = null,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(specPath);
        var content = await File.ReadAllTextAsync(specPath, cancellationToken);
        var requests = new OpenApiConverter().Import(content, extension);
        var group = ToFileName(groupName ?? Path.GetFileNameWithoutExtension(specPath));
        var requestDirectory = Path.Combine(workspacePath, "collections", group);
        var specDirectory = Path.Combine(workspacePath, "specs");

        Directory.CreateDirectory(requestDirectory);
        Directory.CreateDirectory(specDirectory);

        await File.WriteAllTextAsync(Path.Combine(specDirectory, Path.GetFileName(specPath)), content, cancellationToken);
        foreach (var request in requests)
        {
            await _jsonStorage.SaveAsync(
                Path.Combine(requestDirectory, $"{ToFileName(request.Name)}.json"),
                request,
                cancellationToken);
        }

        return requests;
    }

    private static string CreateExampleOpenApi()
    {
        return """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "HTTPBin Example",
            "version": "1.0.0"
          },
          "servers": [
            {
              "url": "https://httpbin.org"
            }
          ],
          "paths": {
            "/get": {
              "get": {
                "operationId": "getWithQuery",
                "summary": "GET with query",
                "parameters": [
                  {
                    "name": "keyword",
                    "in": "query",
                    "schema": {
                      "type": "string"
                    }
                  }
                ],
                "responses": {
                  "200": {
                    "description": "OK"
                  }
                }
              }
            }
          }
        }
        """;
    }

    private static string CreateExampleHttpFile()
    {
        return """
        ### Get with query
        GET {{baseUrl}}/get?keyword={{keyword}}
        accept: application/json

        ### Post JSON
        POST {{baseUrl}}/post
        accept: application/json
        content-type: application/json

        {
          "name": "ReadableHttp",
          "source": "example-workspace"
        }
        """;
    }

    private static string ToFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "item" : sanitized;
    }

    private static string GetCollectionRequestDirectory(string workspacePath, ReadableCollection collection)
    {
        return string.IsNullOrWhiteSpace(collection.RequestDirectory)
            ? Path.Combine(workspacePath, "collections", ToFileName(collection.Name))
            : Path.Combine(workspacePath, collection.RequestDirectory);
    }

    private static ReadableWorkspace CreateWorkspaceSnapshot(ReadableWorkspace workspace)
    {
        var json = JsonSerializer.Serialize(workspace, ReadableHttpJsonStorage.JsonOptions);
        var snapshot = JsonSerializer.Deserialize<ReadableWorkspace>(json, ReadableHttpJsonStorage.JsonOptions) ?? new ReadableWorkspace();
        ClearCollectionRequests(snapshot.Collections);

        return snapshot;
    }

    private static void ClearCollectionRequests(IEnumerable<ReadableCollection> collections)
    {
        foreach (var collection in collections)
        {
            collection.Requests.Clear();
            ClearCollectionRequests(collection.Children);
        }
    }

    private static string? ResolveExistingRequestPath(
        string workspacePath,
        ReadableCollection collection,
        ReadableRequest request)
    {
        var requestDirectory = Path.GetFullPath(GetCollectionRequestDirectory(workspacePath, collection));
        if (!string.IsNullOrWhiteSpace(request.SourcePath))
        {
            var sourcePath = Path.GetFullPath(Path.Combine(workspacePath, request.SourcePath));
            if (IsPathInsideDirectory(sourcePath, requestDirectory) && File.Exists(sourcePath))
            {
                return sourcePath;
            }
        }

        var files = Directory.Exists(requestDirectory)
            ? Directory.EnumerateFiles(requestDirectory, "*.json", SearchOption.TopDirectoryOnly)
            : Enumerable.Empty<string>();
        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var stored = JsonSerializer.Deserialize<ReadableRequest>(stream, ReadableHttpJsonStorage.JsonOptions);
                if (stored is not null && Matches(stored.Id, request.Id))
                {
                    return Path.GetFullPath(file);
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string NextAvailablePath(string directory, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            index++;
        }

        return candidate;
    }

    private static string ResolveRequestSavePath(string? existingPath, string desiredPath)
    {
        if (existingPath is null)
        {
            return NextAvailablePath(Path.GetDirectoryName(desiredPath)!, Path.GetFileName(desiredPath));
        }

        var normalizedExisting = Path.GetFullPath(existingPath);
        var normalizedDesired = Path.GetFullPath(desiredPath);
        if (string.Equals(normalizedExisting, normalizedDesired, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(normalizedDesired))
        {
            return desiredPath;
        }

        return NextAvailablePath(Path.GetDirectoryName(desiredPath)!, Path.GetFileName(desiredPath));
    }

    private static string ToWorkspaceRelativePath(string workspacePath, string path)
    {
        return Path.GetRelativePath(workspacePath, path).Replace('\\', '/');
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCollectionsRoot(string workspacePath, string requestDirectory)
    {
        var collectionsDirectory = Path.GetFullPath(Path.Combine(workspacePath, "collections"));
        var directory = Path.GetFullPath(requestDirectory);
        return string.Equals(collectionsDirectory, directory, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ReadableRequest?> TryLoadRequestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var request = await _jsonStorage.LoadAsync<ReadableRequest>(path, cancellationToken);
            return string.IsNullOrWhiteSpace(request.Url) ? null : request;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Matches(string? value, string expected)
    {
        return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
