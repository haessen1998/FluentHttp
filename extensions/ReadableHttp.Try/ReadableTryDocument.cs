using ReadableHttp;

namespace ReadableHttp.Try;

public enum ReadableTrySourceType
{
    Unknown,
    OpenApi,
    HttpFile,
    Curl,
    ReadableRequest
}

public sealed class ReadableTryDocument
{
    public string? FileName { get; set; }

    public ReadableTrySourceType SourceType { get; set; } = ReadableTrySourceType.Unknown;

    public string RawContent { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Version { get; set; }

    public Dictionary<string, ReadableVariable> Variables { get; set; } = [];

    public List<ReadableTryOperation> Operations { get; set; } = [];
}

public sealed class ReadableTryOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Path { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public List<string> Tags { get; set; } = [];

    public ReadableRequest Request { get; set; } = new();
}
