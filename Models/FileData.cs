using Newtonsoft.Json;

namespace BlobToCosmosFunction.Models;

public class FileData
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    public string Content { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public string Status { get; set; } = "Processed";
}
