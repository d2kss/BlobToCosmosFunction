using Newtonsoft.Json;

namespace BlobToCosmosFunction.Models;

public class PhoneNumber
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Number { get; set; } = string.Empty;
    public string NormalizedNumber { get; set; } = string.Empty; // For comparison (digits only)
    public string SourceFile { get; set; } = string.Empty;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public int OccurrenceCount { get; set; } = 1;
    public List<string> SourceFiles { get; set; } = new();
}
