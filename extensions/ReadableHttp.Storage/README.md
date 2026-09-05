# ReadableHttp.Storage

Optional .NET 10 JSON persistence and workspace helpers for ReadableHttp. Moved from the application support directory to `extensions`; the namespace remains `ReadableHttp.Storage`.

```shell
dotnet add package ReadableHttp.Storage --version 2.1.0
```

```csharp
using ReadableHttp;
using ReadableHttp.Storage;

var storage = new ReadableHttpJsonStorage();
await storage.SaveAsync("requests/user.json", new ReadableRequest
{
    Method = "GET",
    Url = "https://api.example.com/users/42"
}, cancellationToken);
var request = await storage.LoadAsync<ReadableRequest>("requests/user.json", cancellationToken);
```

`ReadableWorkspaceStore` manages workspace metadata and request collections. `ReadableWorkspaceGitService` provides Git workspace operations and requires Git when those operations are used. JSON persistence does not encrypt or redact secrets; callers control what is stored and where. This extension depends on ReadableHttp and ReadableHttp.ImportExport, without any UI or CLI dependency.
