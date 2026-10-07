using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class DeadlockHeroCatalog
{
    private static readonly IReadOnlyList<DeadlockHeroModel> Heroes = Array.AsReadOnly(new DeadlockHeroModel[]
    {
        new("Abrams", "abrams", "models/heroes_wip/abrams/abrams.vmdl_c", "bull_sm_psd"),
        new("Apollo", "apollo", "models/heroes_wip/fencer/fencer.vmdl_c", "fencer_sm_psd"),
        new("Baba", "baba", "models/heroes_wip/baba/baba.vmdl_c"),
        new("Bebop", "bebop", "models/heroes_staging/bebop/bebop.vmdl_c", "bebop_sm_psd"),
        new("Billy", "billy", "models/heroes_wip/punkgoat/punkgoat.vmdl_c", "punkgoat_sm_psd"),
        new("Calico", "calico", "models/heroes_staging/nano/nano_v2/nano.vmdl_c", "nano_sm_psd"),
        new("Celeste", "celeste", "models/heroes_wip/unicorn/unicorn.vmdl_c", "unicorn_sm_psd"),
        new("Deadman Danny", "deadman_danny", "models/heroes_wip/deadpack/deadpack.vmdl_c"),
        new("Drifter", "drifter", "models/heroes_wip/drifter/drifter.vmdl_c", "drifter_sm_psd"),
        new("Dynamo", "dynamo", "models/heroes_wip/dynamo/dynamo.vmdl_c", "sumo_sm_psd"),
        new("The Doorman", "doorman", "models/heroes_wip/doorman_v2/doorman.vmdl_c", "doorman_sm_psd"),
        new("Graves", "graves", "models/heroes_wip/necro/necro.vmdl_c", "necro_sm_psd"),
        new("Grey Talon", "grey_talon", "models/heroes_staging/archer/archer.vmdl_c", "archer_sm_psd"),
        new("Haze", "haze", "models/heroes_staging/haze/haze.vmdl_c", "haze_sm_psd"),
        new("Holliday", "holliday", "models/heroes_staging/astro/astro.vmdl_c", "astro_sm_psd"),
        new("Infernus", "infernus", "models/heroes_wip/inferno/inferno.vmdl_c", "inferno_sm_psd"),
        new("Ivy", "ivy", "models/heroes_wip/ivy/ivy.vmdl_c", "tengu_sm_psd"),
        new("Kelvin", "kelvin", "models/heroes_staging/kelvin_v2/kelvin.vmdl_c", "kelvin_sm_psd"),
        new("Lady Geist", "lady_geist", "models/heroes_wip/geist/geist.vmdl_c", "spectre_sm_psd"),
        new("Lash", "lash", "models/heroes_wip/lash/lash.vmdl_c", "lash_sm_psd"),
        new("McGinnis", "mcginnis", "models/heroes_wip/mcginnis/mcginnis.vmdl_c", "engineer_sm_psd"),
        new("Mina", "mina", "models/heroes_wip/vampirebat/vampirebat.vmdl_c", "vampirebat_sm_psd"),
        new("Mirage", "mirage", "models/heroes_staging/mirage_v2/mirage.vmdl_c", "mirage_sm_psd"),
        new("Mo & Krill", "mo_krill", "models/heroes_staging/digger/digger.vmdl_c", "digger_sm_psd"),
        new("Nurse Harrow", "nurse_harrow", "models/heroes_wip/nurse/nurse.vmdl_c"),
        new("Paige", "paige", "models/heroes_wip/bookworm/bookworm.vmdl_c", "bookworm_sm_psd"),
        new("Paradox", "paradox", "models/heroes_staging/chrono/chrono.vmdl_c", "chrono_sm_psd"),
        new("Pocket", "pocket", "models/heroes_wip/pocket/pocket.vmdl_c", "synth_sm_psd"),
        new("Rat King", "rat_king", "models/heroes_wip/ratking/ratking.vmdl_c"),
        new("Rem", "rem", "models/heroes_wip/familiar/familiar_wip.vmdl_c", "familiar_sm_psd"),
        new("Seven", "seven", "models/heroes_staging/gigawatt_prisoner/gigawatt_prisoner.vmdl_c", "gigawatt_sm_psd"),
        new("Shiv", "shiv", "models/heroes_staging/shiv/shiv.vmdl_c", "shiv_sm_psd"),
        new("Silver", "silver", "models/heroes_wip/werewolf/werewolf.vmdl_c", "werewolf_sm_psd"),
        new("Sinclair", "sinclair", "models/heroes_staging/magician_v2/magician.vmdl_c", "magician_sm_psd"),
        new("Solomon", "solomon", "models/heroes_wip/chessmaster/chessmaster.vmdl_c"),
        new("Venator", "venator", "models/heroes_wip/priest/priest.vmdl_c", "priest_sm_psd"),
        new("Victor", "victor", "models/heroes_wip/frank/frank.vmdl_c", "frank_sm_psd"),
        new("Vindicta", "vindicta", "models/heroes_staging/hornet_v3/hornet.vmdl_c", "hornet_sm_png"),
        new("Violet", "violet", "models/heroes_wip/artist/artist.vmdl_c"),
        new("Viscous", "viscous", "models/heroes_staging/viscous/viscous.vmdl_c", "viscous_sm_psd"),
        new("Vyper", "vyper", "models/heroes_staging/viper/viper.vmdl_c", "kali_sm_psd"),
        new("Warden", "warden", "models/heroes_staging/warden/warden.vmdl_c", "warden_sm_psd"),
        new("Wraith", "wraith", "models/heroes_wip/wraith/wraith.vmdl_c", "wraith_sm_psd"),
        new("Yamato", "yamato", "models/heroes_staging/yamato_v2/yamato.vmdl_c", "yamato_sm_psd")
    });

    // Neutral creeps with AG2 skeletons; keys match their hero_paths.json presets.
    private static readonly IReadOnlyList<DeadlockHeroModel> Neutrals = Array.AsReadOnly(new[]
    {
        "models/npc_units/neutral_barrel_mimic_01/neutral_barrel_mimic_01.vmdl_c",
        "models/npc_units/neutral_barrel_mimic_02/neutral_barrel_mimic_02.vmdl_c",
        "models/npc_units/neutral_ct_lanterns/neutral_ct_lantern.vmdl_c",
        "models/npc_units/neutral_ct_lanterns/neutral_ct_lantern_elite.vmdl_c",
        "models/npc/neutral_drain_creature/neutral_drain_creature.vmdl_c",
        "models/npc/neutral_drain_creature/neutral_drain_creature_large.vmdl_c",
        "models/npc_units/neutral_library_bookmoth/neutral_library_bookmoth.vmdl_c",
        "models/npc_units/neutral_library_watcher_01/neutral_library_watcher_01.vmdl_c",
        "models/npc_units/neutral_mushroom_large_01/neutral_mushroom_large_01.vmdl_c",
        "models/npc_units/neutral_mushroom_small_01/neutral_mushroom_small_01.vmdl_c",
        "models/npc_units/neutral_park_watcher_01/neutral_park_watcher_01.vmdl_c",
        "models/npc_units/neutral_pigeon_cerberus/neutral_pigeon_cerberus.vmdl_c",
        "models/npc_units/neutral_plant_large_01/neutral_plant_large_01.vmdl_c",
        "models/npc_units/neutral_plant_small_01/neutral_plant_small_01.vmdl_c",
        "models/npc_units/neutral_specimens/neutral_specimen_01.vmdl_c",
        "models/npc_units/neutral_theatre_puppeteer_01/neutral_theatre_puppeteer_01.vmdl_c",
        "models/npc_units/neutral_theatre_puppeteer_02/neutral_theatre_puppeteer_02.vmdl_c",
        "models/npc_units/neutral_theatre_puppeteer_03/neutral_theatre_puppeteer_03.vmdl_c",
        "models/npc_units/neutral_underhand_01/neutral_underhand_01.vmdl_c"
    }.Select(path =>
    {
        var key = Path.GetFileName(path)[..^".vmdl_c".Length];
        return new DeadlockHeroModel("Neutral: " + key["neutral_".Length..].Replace('_', ' '), key, path);
    }).ToArray());

    private static readonly IReadOnlyList<DeadlockHeroModel> Exportable =
        Array.AsReadOnly(Heroes.Concat(Neutrals).ToArray());

    public static IReadOnlyList<DeadlockHeroModel> GetHeroes() => Heroes;

    public static IReadOnlyList<DeadlockHeroModel> GetNeutrals() => Neutrals;

    /// <summary>Everything "add addon" can export: heroes first, then neutral creeps.</summary>
    public static IReadOnlyList<DeadlockHeroModel> GetExportableModels() => Exportable;
}
