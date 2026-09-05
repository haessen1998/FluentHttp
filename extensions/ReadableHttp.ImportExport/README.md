# ReadableHttp.ImportExport

ReadableHttp.ImportExport converts between ReadableHttp requests and common API request formats.

It supports `.http` files, curl commands, OpenAPI 3 documents, and Swagger 2 documents. Use this package when you need to import existing API descriptions into `ReadableRequest` objects or export ReadableHttp requests for interoperability.

## Install

```shell
dotnet add package ReadableHttp.ImportExport --version 2.1.0
```

## Import Curl

```csharp
using ReadableHttp.ImportExport;

var request = new CurlConverter().Import("""
curl -X POST https://api.example.com/users \
  -H 'content-type: application/json' \
  --data-raw '{"name":"Ada"}'
""");

Console.WriteLine(request.Method);
Console.WriteLine(request.Url);
```

## Import `.http`

```csharp
using ReadableHttp.ImportExport;

var requests = new HttpFileConverter().Import("""
### Get user
GET https://api.example.com/users/42
accept: application/json
""");
```

## Import OpenAPI

```csharp
using ReadableHttp.ImportExport;

var openApiJson = File.ReadAllText("openapi.json");
var requests = new OpenApiConverter().Import(openApiJson, ".json");
```

## Create A Request For One OpenAPI Operation

```csharp
using ReadableHttp.ImportExport.OpenApi;

var request = await new OpenApiRequestFactory()
    .CreateRequestAsync("openapi.json", "get /users/{id}");
```

## Export OpenAPI

```csharp
using ReadableHttp.ImportExport;

var document = new OpenApiConverter().Export(requests);
```
