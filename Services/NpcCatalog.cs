using SkiaSharp;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Readable names and HUD icons for the game's NPC models.</summary>
public static class NpcCatalog
{
    private const string Npcs = "panorama/images/npcs/";
    private const string Neutrals = Npcs + "neutrals/";
    // The game has no HUD icon for the mid-boss; its wall graffiti carries an emblem of it.
    private const string MidBossGraffiti = "materials/overlays/midboss_graffiti_02_color_psd_15ffd570.vtex_c";

    /// <summary>
    /// Hero summons with their own skeleton and graphs that the unit list does not name or that
    /// live with their hero: Grey Talon's owl, Graves' shamblers, Silver's wolf form.
    /// </summary>
    public static IReadOnlyList<string> SummonModels { get; } =
    [
        "models/heroes_staging/grey_talon/grey_talon_owl.vmdl_c",
        "models/heroes_wip/necro/necro_shambler.vmdl_c",
        "models/heroes_wip/werewolf/transform/werewolf_transform.vmdl_c",
    ];

    // Unit models the game data still names but the game no longer uses.
    private static readonly HashSet<string> Unused = new(StringComparer.OrdinalIgnoreCase)
    {
        "neutral_pigeon_cerberus",
    };

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["boss_tier_01_brazier_guardian"] = "Guardian",
        ["boss_tier_02_sun_walker"] = "Walker",
        ["midboss"] = "Mid-Boss",
        ["trooper_01"] = "Trooper",
        ["trooper_01_melee"] = "Trooper (melee)",
        ["medic_01"] = "Medic",
        ["grey_talon_owl"] = "Grey Talon Owl",
        ["necro_shambler"] = "Graves Shambler",
        ["werewolf_transform"] = "Silver (wolf form)",
    };

    // The first token found in the model's file name picks the icon, so specific names come first.
    // Neutral icons come in weak/normal/heavy tiers; the larger model of a pair gets the bigger one.
    private static readonly (string Token, string Icon)[] Icons =
    [
        ("grey_talon_owl", "panorama/images/hud/abilities/archer/archer_guided_arrow_psd.vtex_c"),
        ("necro_shambler", "panorama/images/heroes/necro_shambler_psd.vtex_c"),
        ("werewolf_transform", "panorama/images/heroes/werewolf_wolf_card_psd.vtex_c"),
        ("midboss", MidBossGraffiti),
        ("trooper", Npcs + "trooper_psd.vtex_c"),
        ("medic", Npcs + "trooper_psd.vtex_c"),
        ("brazier_guardian", Npcs + "tier_1_boss_psd.vtex_c"),
        ("sun_walker", Npcs + "tier_2_boss_psd.vtex_c"),
        ("patron_amber", Npcs + "patron_archmother_psd.vtex_c"),
        ("patron_sapphire", Npcs + "patron_hiddenking_psd.vtex_c"),
        ("patron", Npcs + "patron_psd.vtex_c"),
        ("core_", Npcs + "core_psd.vtex_c"),
        ("barrel_mimic_02", Neutrals + "neutral_barrel_heavy_psd.vtex_c"),
        ("barrel_mimic", Neutrals + "neutral_barrel_normal_psd.vtex_c"),
        ("bookmoth", Neutrals + "neutral_book_weak_psd.vtex_c"),
        ("library_watcher", Neutrals + "neutral_book_normal_psd.vtex_c"),
        ("ct_lantern_elite", Neutrals + "neutral_lantern_strong_psd.vtex_c"),
        ("ct_lantern", Neutrals + "neutral_lantern_normal_psd.vtex_c"),
        ("drain_creature_large", Neutrals + "neutral_drain_creature_strong_psd.vtex_c"),
        ("drain_creature", Neutrals + "neutral_drain_creature_normal_psd.vtex_c"),
        ("mushroom_large", Neutrals + "neutral_mushroom_heavy_psd.vtex_c"),
        ("mushroom_small", Neutrals + "neutral_mushroom_normal_psd.vtex_c"),
        ("plant_large", Neutrals + "neutral_plant_heavy_psd.vtex_c"),
        ("plant_small", Neutrals + "neutral_plant_normal_psd.vtex_c"),
        ("specimen", Neutrals + "neutral_specimen_normal_psd.vtex_c"),
        ("theatre_puppeteer", Neutrals + "neutral_stagehand_normal_psd.vtex_c"),
        ("park_watcher", Neutrals + "neutral_underground_heavy_psd.vtex_c"),
        ("underhand", Neutrals + "neutral_underground_normal_psd.vtex_c"),
    ];

    /// <summary>Whether the game no longer uses this model, though its data still names it.</summary>
    public static bool IsUnused(string modelPath) => Unused.Contains(Stem(modelPath));

    /// <summary>Whether this is a hero summon rather than a map NPC.</summary>
    public static bool IsSummon(string modelPath) =>
        SummonModels.Contains(modelPath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Trims a decoded icon to the part that shows the NPC, for icons cut from a larger texture.
    /// Other icons are returned as they are.
    /// </summary>
    public static byte[] Trim(string iconPath, byte[] png)
    {
        if (!iconPath.Equals(MidBossGraffiti, StringComparison.OrdinalIgnoreCase)) return png;
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap == null) return png;
        // The round crocodile emblem in the middle of the graffiti sheet.
        var area = SKRectI.Create((int)(bitmap.Width * 0.325f), (int)(bitmap.Height * 0.44f),
            (int)(bitmap.Width * 0.37f), (int)(bitmap.Height * 0.335f));
        using var emblem = new SKBitmap();
        if (!bitmap.ExtractSubset(emblem, area)) return png;
        using var image = SKImage.FromBitmap(emblem);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Every icon the catalog can return, for loading them in one pass.</summary>
    public static IReadOnlyList<string> IconPaths { get; } = Icons.Select(icon => icon.Icon).Distinct().ToList();

    /// <summary>
    /// A readable name for an NPC model, such as Walker for boss_tier_02_sun_walker. With
    /// <paramref name="withoutTeam"/>, the amber/sapphire part is left out, for an entry that
    /// stands for both teams' models.
    /// </summary>
    public static string DisplayName(string modelPath, bool withoutTeam = false)
    {
        var name = Stem(modelPath);
        if (name.StartsWith("neutral_", StringComparison.Ordinal)) name = name["neutral_".Length..];
        if (withoutTeam)
            name = string.Join('_', name.Split('_').Where(word => word is not ("amber" or "sapphire")));
        if (Names.TryGetValue(name, out var known)) return known;
        return string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>The HUD icon for an NPC model, or null when the game has none for it.</summary>
    public static string? IconPath(string modelPath)
    {
        var name = Stem(modelPath);
        return Icons.FirstOrDefault(icon => name.Contains(icon.Token, StringComparison.Ordinal)).Icon;
    }

    private static string Stem(string modelPath)
    {
        var path = modelPath.Replace('\\', '/');
        if (path.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) path = path[..^2];
        return Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
    }
}
