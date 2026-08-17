using System.Text.Json;
using System.Text.Json.Serialization;
using RimContext.Core.Contracts;
using RimContext.Core.Model;

namespace RimContext.Core.Output;

public sealed class JsonEnvelope
{
    public string SchemaVersion { get; init; } = IndexConstants.SchemaVersionText;

    public string Status { get; init; } = "ok";

    public string Command { get; init; } = "unknown";

    public object? Results { get; init; }

    public object? Data { get; init; }

    public IReadOnlyList<JsonWarning>? Warnings { get; init; }

    public RimContextError? Error { get; init; }
}

public sealed record JsonWarning(string Code, string Message, string? Path = null);

public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static JsonEnvelope Success(string command, object? data = null, object? results = null) => new()
    {
        Command = command,
        Data = data,
        Results = results
    };

    public static JsonEnvelope Partial(
        string command,
        object? data = null,
        object? results = null,
        IReadOnlyList<JsonWarning>? warnings = null) => new()
        {
            Command = command,
            Status = "partial",
            Data = data,
            Results = results,
            Warnings = warnings
        };

    public static JsonEnvelope Error(string command, RimContextError error) => new()
    {
        Command = command,
        Status = "error",
        Error = error
    };

    public static void Write(TextWriter writer, JsonEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(envelope);
        writer.WriteLine(JsonSerializer.Serialize(envelope, Options));
    }

    public static string Serialize(JsonEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static string SerializePayload(object value) => JsonSerializer.Serialize(value, Options);
}
