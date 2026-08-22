using System.Text.Json.Serialization;

namespace DeadlockVmdlCompiler.Models;

public class HeroPreset
{
    [JsonPropertyName("skel")]
    public string Skel { get; set; } = string.Empty;

    [JsonPropertyName("graph")]
    public string Graph { get; set; } = string.Empty;

    [JsonPropertyName("ui_graph")]
    public string UiGraph { get; set; } = string.Empty;
}
