using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class HeroPresetMatcher
{
    private static readonly IReadOnlyDictionary<string, string> InternalNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["archer"] = "grey_talon",
            ["artist"] = "violet",
            ["astro"] = "holliday",
            ["bookworm"] = "paige",
            ["chrono"] = "paradox",
            ["chessmaster"] = "solomon",
            ["deadpack"] = "deadman_danny",
            ["digger"] = "mo_krill",
            ["familiar"] = "rem",
            ["familiar_wip"] = "rem",
            ["fencer"] = "apollo",
            ["frank"] = "victor",
            ["geist"] = "lady_geist",
            ["gigawatt_prisoner"] = "seven",
            ["hornet"] = "vindicta",
            ["inferno"] = "infernus",
            ["magician"] = "sinclair",
            ["nano"] = "calico",
            ["necro"] = "graves",
            ["nurse"] = "nurse_harrow",
            ["priest"] = "venator",
            ["punkgoat"] = "billy",
            ["ratking"] = "rat_king",
            ["unicorn"] = "celeste",
            ["vampirebat"] = "mina",
            ["viper"] = "vyper",
            ["werewolf"] = "silver"
        };

    public static DeadlockHeroModel? FindKnownHero(string presetKey, HeroPreset? preset)
    {
        if (preset == null || string.IsNullOrWhiteSpace(preset.Skel))
            return null;
        var heroes = DeadlockHeroCatalog.GetHeroes();
        var heroKey = InternalNames.TryGetValue(presetKey, out var mappedKey) ? mappedKey : presetKey;
        var namedHero = heroes.FirstOrDefault(candidate =>
            candidate.HeroKey.Equals(heroKey, StringComparison.OrdinalIgnoreCase));
        if (namedHero != null)
            return SkeletonMatches(preset, namedHero) ? namedHero : null;

        // Custom preset names can still identify a hero when both AG2 references match exactly.
        var matches = heroes.Where(hero => SkeletonMatches(preset, hero) &&
            string.Equals(preset.Graph?.Replace('\\', '/'), ExpectedGraph(hero),
                StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool SkeletonMatches(HeroPreset preset, DeadlockHeroModel hero) =>
        string.Equals(preset.Skel?.Replace('\\', '/'), ExpectedSkeleton(hero),
            StringComparison.OrdinalIgnoreCase);

    private static string ExpectedGraph(DeadlockHeroModel hero)
    {
        var name = hero.HeroKey switch
        {
            "apollo" => "fencer",
            "billy" => "punkgoat",
            "celeste" => "unicorn",
            "deadman_danny" => "deadpack",
            "graves" => "necro",
            "grey_talon" => "archer",
            "infernus" => "inferno",
            "lady_geist" => "geist",
            "mina" => "vampirebat",
            "nurse_harrow" => "nurse",
            "paige" => "bookworm",
            "paradox" => "chrono",
            "rat_king" => "ratking",
            "rem" => "familiar",
            "seven" => "gigawatt",
            "silver" => "werewolf",
            "sinclair" => "magician",
            "solomon" => "chessmaster",
            "venator" => "priest",
            "victor" => "frank",
            "violet" => "artist",
            "viscous" => "viscous_v1",
            "vyper" => "viper",
            _ => hero.HeroKey
        };
        return $"animgraphs/animgraph2/hero/hero.vnmgraph+{name}.vnmgraph";
    }

    private static string ExpectedSkeleton(DeadlockHeroModel hero) => hero.HeroKey switch
    {
        "ivy" => "models/heroes_staging/tengu/tengu_v2/dmx/mesh/ivy.vnmskel",
        "kelvin" or "viscous" => "models/heroes_staging/kelvin_v2/maya/kelvin.vnmskel",
        "lady_geist" => "models/heroes_staging/ghost/ghost.vnmskel",
        "mo_krill" => "models/heroes_staging/digger/mo_krill.vnmskel",
        "pocket" => "models/heroes_staging/synth/synth.vnmskel",
        "rem" => "models/heroes_wip/familiar/familiar.vnmskel",
        "yamato" => "models/heroes_wip/yamato/yamato.vnmskel",
        _ => hero.VpkPath[..^".vmdl_c".Length] + ".vnmskel"
    };
}
