using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models;

public sealed class Technician
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("employeeId")]
    [JsonProperty("employeeId")]
    public string EmployeeId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("department")]
    [JsonProperty("department")]
    public string Department { get; set; } = string.Empty;

    [JsonPropertyName("skills")]
    [JsonProperty("skills")]
    public List<string> Skills { get; set; } = [];

    [JsonPropertyName("isAvailable")]
    [JsonProperty("isAvailable")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("currentWorkOrderId")]
    [JsonProperty("currentWorkOrderId")]
    public string? CurrentWorkOrderId { get; set; }

    [JsonPropertyName("shift")]
    [JsonProperty("shift")]
    public string? Shift { get; set; }
}
