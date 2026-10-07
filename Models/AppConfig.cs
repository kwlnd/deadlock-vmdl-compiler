using System.Text.Json.Serialization;

namespace DeadlockVmdlCompiler.Models;

public class AppConfig
{
    [JsonPropertyName("cswin_dir")]
    public string CsWinDir { get; set; } = string.Empty;

    [JsonPropertyName("citadel_addons_dir")]
    public string CitadelAddonsDir { get; set; } = string.Empty;

    [JsonPropertyName("chk_revert")]
    public bool ChkRevert { get; set; } = true;

    [JsonPropertyName("chk_skel")]
    public bool ChkSkel { get; set; } = true;

    [JsonPropertyName("chk_graph")]
    public bool ChkGraph { get; set; } = true;

    [JsonPropertyName("chk_ui_graph")]
    public bool ChkUiGraph { get; set; } = true;

    [JsonPropertyName("chk_disable_anim_list")]
    public bool ChkDisableAnimList { get; set; }

    [JsonPropertyName("chk_auto_detect_anims")]
    public bool ChkAutoDetectAnims { get; set; } = true;

    [JsonPropertyName("hero_paths_file")]
    public string? HeroPathsFile { get; set; }

    [JsonPropertyName("use_builtin_hero_paths")]
    public bool UseBuiltInHeroPaths { get; set; }
}
