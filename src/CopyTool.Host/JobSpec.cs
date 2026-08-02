using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopyTool.Host;

/// <summary>
/// A job as written by the shell extension. The schema is a contract between
/// native and managed code, so the field names here must match DragDropHandler.cpp.
/// </summary>
public sealed record JobSpec
{
    [JsonPropertyName("schema")]      public int Schema { get; init; }
    [JsonPropertyName("id")]          public string Id { get; init; } = "";
    [JsonPropertyName("operation")]   public string Operation { get; init; } = "copy";
    [JsonPropertyName("destination")] public string Destination { get; init; } = "";
    [JsonPropertyName("sources")]     public string[] Sources { get; init; } = [];

    public const int SupportedSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads and validates a job file. Returns null with a reason rather than
    /// throwing: a malformed job must never take the host down.
    /// </summary>
    public static JobSpec? TryLoad(string path, out string error)
    {
        error = "";
        try
        {
            JobSpec? job = JsonSerializer.Deserialize<JobSpec>(File.ReadAllText(path), Options);

            if (job is null)                       { error = "empty job file"; return null; }
            if (job.Schema != SupportedSchema)     { error = $"unsupported schema {job.Schema}"; return null; }
            if (job.Sources.Length == 0)           { error = "no sources"; return null; }
            if (string.IsNullOrWhiteSpace(job.Destination)) { error = "no destination"; return null; }

            return job;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            error = e.Message;
            return null;
        }
    }
}
