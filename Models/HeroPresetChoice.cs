using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace DeadlockVmdlCompiler.Models;

public sealed class HeroPresetChoice : INotifyPropertyChanged
{
    private Bitmap? _portrait;

    public HeroPresetChoice(string key, DeadlockHeroModel? hero = null, bool isAuto = false, string? detail = null,
        HeroPreset? preset = null, string? displayName = null, string sourcePath = "")
    {
        Key = key;
        Hero = hero;
        IsAuto = isAuto;
        _detail = detail;
        Preset = preset;
        _displayName = displayName;
        SourcePath = sourcePath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public DeadlockHeroModel? Hero { get; }
    public bool IsAuto { get; }

    /// <summary>Paths of an NPC found in the game's VPK; null for presets from the preset list.</summary>
    public HeroPreset? Preset { get; }

    /// <summary>The game model an NPC preset was read from, such as models/npc/midboss/midboss.vmdl_c.</summary>
    public string SourcePath { get; }

    public bool IsNpc => _detail is "npc" or "neutral";
    private readonly string? _displayName;
    public string DisplayName => IsAuto ? "auto-detect" : _displayName ?? Hero?.DisplayName ?? Key;
    private readonly string? _detail;
    public string Detail => IsAuto ? "hero or npc, from the selected model" : Hero != null ? Key : _detail ?? "custom / unknown preset";
    public string FallbackGlyph => IsAuto ? "A" : IsNpc ? "N" : "?";
    public bool HasPortrait => _portrait != null;
    public bool ShowFallback => _portrait == null;
    public Bitmap? Portrait
    {
        get => _portrait;
        set
        {
            if (ReferenceEquals(_portrait, value)) return;
            _portrait = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Portrait)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPortrait)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFallback)));
        }
    }
}
