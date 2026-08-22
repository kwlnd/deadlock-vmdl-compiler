using System.Text.Json.Serialization;

namespace DeadlockVmdlCompiler.Models;

public class AppConfig
{
    [JsonPropertyName("cswin_dir")]
    public string CsWinDir { get; set; } = string.Empty;

    [JsonPropertyName("citadel_addons_dir")]
    public string CitadelAddonsDir { get; set; } = string.Empty;

    [JsonPropertyName("last_target_path")]
    public string LastTargetPath { get; set; } = string.Empty;

    [JsonPropertyName("chk_compile")]
    public bool ChkCompile { get; set; } = true;

    [JsonPropertyName("chk_revert")]
    public bool ChkRevert { get; set; } = true;

    [JsonPropertyName("chk_header")]
    public bool ChkHeader { get; set; } = true;

    [JsonPropertyName("chk_skel")]
    public bool ChkSkel { get; set; } = true;

    [JsonPropertyName("chk_graph")]
    public bool ChkGraph { get; set; } = true;

    [JsonPropertyName("chk_ui_graph")]
    public bool ChkUiGraph { get; set; } = true;

    [JsonPropertyName("chk_backup")]
    public bool ChkBackup { get; set; } = true;

    [JsonPropertyName("last_vpk_export_dir")]
    public string? LastVpkExportDir { get; set; }

    [JsonPropertyName("prompt_vpk_after_compile")]
    public bool PromptVpkAfterCompile { get; set; } = true;
}
