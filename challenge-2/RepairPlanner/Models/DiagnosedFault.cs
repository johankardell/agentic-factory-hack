using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

public sealed class DiagnosedFault
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("machineId")]
    [JsonProperty("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("faultType")]
    [JsonProperty("faultType")]
    public string FaultType { get; set; } = string.Empty;

    [JsonPropertyName("faultDescription")]
    [JsonProperty("faultDescription")]
    public string FaultDescription { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    [JsonProperty("severity")]
    public string Severity { get; set; } = "medium";

    [JsonPropertyName("confidence")]
    [JsonProperty("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("timestampUtc")]
    [JsonProperty("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("symptoms")]
    [JsonProperty("symptoms")]
    public List<string> Symptoms { get; set; } = [];

    [JsonPropertyName("diagnosticNotes")]
    [JsonProperty("diagnosticNotes")]
    public string? DiagnosticNotes { get; set; }
}
