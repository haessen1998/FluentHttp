# ReadableHttp.Try

Optional .NET 10 normalization of OpenAPI/Swagger, `.http`, curl and ReadableHttp JSON into request documents. No UI or application host is required.

```shell
dotnet add package ReadableHttp.Try --version 2.1.0
```

```csharp
using ReadableHttp.Try;

var document = new ReadableTryDocumentLoader().Load(
    "GET https://api.example.com/users/42", "users.http", ".http");
foreach (var operation in document.Operations)
    Console.WriteLine($"{operation.Method} {operation.Path}");
```

Use `LoadAsync(path, cancellationToken)` for a local file. Each normalized operation exposes a `ReadableRequest`; pass it to an SDK executor to send it. `ReadableSpecificationRefresher` supports specification refresh workflows. Loading a document does not execute its requests.

The project moved from application supports into `extensions`; the `ReadableHttp.Try` namespace is unchanged. Dependencies are ReadableHttp, ReadableHttp.ImportExport and ReadableHttp.Storage.
