using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using HarmonyLib;

namespace VSRPBridgeClient;

/// <summary>
/// Makes the wounds that VSRPBridge stores on the player show up in the character dialog's Traits tab.
///
/// The server adds each wound as a key in "extraTraits" and stores its JSON
/// ({ name, cause, debuffs }) under the same key in the "woundData" tree; both are synced to the client.
/// The Traits tab (PlayerModelLib's replacement of CharacterSystem.getClassTraitText) only lists
/// extraTraits that exist in the client's CharacterSystem.TraitsByCode, and names them with the
/// "trait-{code}" and "traitdesc-{code}" lang entries. Wound names come from the bot at runtime,
/// so both the traits and their lang entries are added here, right before the Traits tab is composed.
/// Doing it then means the tab always matches the current woundData, without relying on spawn
/// events (the own player's entity can spawn before capi.World.Player is set).
///
/// The traits are registered without attributes on purpose: the debuffs are already applied as stats by
/// the server, and trait attributes would be applied again whenever trait attributes are recomputed.
/// The debuffs are shown through the description instead.
/// </summary>
public class WoundTraits
{
    private static WoundTraits? instance;

    private readonly ICoreClientAPI capi;
    // Keys registered by us, so we only ever update our own traits and never replace real ones
    private readonly HashSet<string> ownKeys = new();

    public WoundTraits(ICoreClientAPI capi, Harmony harmony)
    {
        this.capi = capi;
        instance = this;

        // Runs before PlayerModelLib's prefix, which replaces the method and builds the trait text
        harmony.Patch(
            AccessTools.Method(typeof(CharacterSystem), "composeTraitsTab"),
            prefix: new HarmonyMethod(typeof(WoundTraits), nameof(BeforeComposeTraitsTab)) { priority = Priority.First });
    }

    public static void BeforeComposeTraitsTab()
    {
        try { instance?.Sync(); }
        catch (Exception ex)
        {
            instance?.capi.Logger.Warning("[VSRPBridgeClient] Could not register wound traits: " + ex.Message);
        }
    }

    private void Sync()
    {
        var woundStore = capi.World.Player?.Entity?.WatchedAttributes.GetTreeAttribute("woundData");
        var traitsByCode = capi.ModLoader.GetModSystem<CharacterSystem>()?.TraitsByCode;
        if (woundStore == null || traitsByCode == null) return;

        foreach (var entry in woundStore)
        {
            string traitKey = entry.Key;
            if (traitsByCode.ContainsKey(traitKey) && !ownKeys.Contains(traitKey)) continue;

            traitsByCode[traitKey] = new Trait
            {
                Code = traitKey,
                Type = EnumTraitType.Negative,
                Attributes = new Dictionary<string, double>()
            };
            ownKeys.Add(traitKey);

            ReadWound(woundStore.GetString(traitKey), out string? name, out string? cause, out var debuffs);
            SetLang("trait-" + traitKey, DisplayName(traitKey, name));
            // Without a description entry the tab shows just the name, instead of "Name: "
            string description = Description(cause, debuffs);
            SetLang("traitdesc-" + traitKey, description.Length > 0 ? description : null);
        }
    }

    private static void ReadWound(string? json, out string? name, out string? cause, out List<KeyValuePair<string, double>> debuffs)
    {
        name = null;
        cause = null;
        debuffs = new();
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
            if (root.TryGetProperty("cause", out var c) && c.ValueKind == JsonValueKind.String) cause = c.GetString();
            if (root.TryGetProperty("debuffs", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                foreach (var debuff in d.EnumerateObject())
                    if (debuff.Value.TryGetDouble(out double value))
                        debuffs.Add(new(debuff.Name, value));
            }
        }
        catch (JsonException)
        {
            // Fall back to the key as the name
        }
    }

    /// <summary>
    /// Duplicates are stored as "name_2", "name_3", so show them as "Name (2)" to tell them apart.
    /// </summary>
    private static string DisplayName(string traitKey, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return traitKey;

        string baseKey = name.ToLowerInvariant().Replace(" ", "_");
        return traitKey.StartsWith(baseKey + "_") && int.TryParse(traitKey.Substring(baseKey.Length + 1), out int counter)
            ? name + " (" + counter + ")"
            : name;
    }

    private static string Description(string? cause, List<KeyValuePair<string, double>> debuffs)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cause)) parts.Add(cause);
        foreach (var (stat, value) in debuffs) parts.Add(DescribeStat(stat, value));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Lang keys the character dialog's Stats panel uses for the stats it shows
    /// (CharacterExtraDialogs.ComposeStatsGui). Only used for stats missing from our own lang file.
    /// </summary>
    private static readonly Dictionary<string, string> StatNameLangKeys = new()
    {
        ["walkspeed"] = "Walk speed",
        ["healingeffectivness"] = "Healing effectivness",
        ["hungerrate"] = "Hunger rate",
        ["rangedWeaponsAcc"] = "Ranged Accuracy",
        ["rangedWeaponsSpeed"] = "Ranged Charge Speed",
    };

    /// <summary>
    /// Describes one debuff using "vsrpbridgeclient:stat-{code}" from our lang file, e.g. "Walk speed -15%".
    /// Each entry decides how its value is shown, since the blend type doesn't say: maxhealthExtraPoints
    /// is a plain number of points even though it's registered like the multiplier stats.
    /// Stats without an entry fall back to a name and a percentage.
    /// </summary>
    private static string DescribeStat(string stat, double value)
    {
        string percent = (value * 100).ToString("+0.#;-0.#", CultureInfo.InvariantCulture) + "%";
        string plain = value.ToString("+0.##;-0.##", CultureInfo.InvariantCulture);

        // Lang keys can't contain ':' after the domain (the key would lose its domain), so mod domains use '-'
        string? text = Lang.GetIfExists("vsrpbridgeclient:stat-" + stat.Replace(':', '-'), percent, plain);
        return text ?? StatDisplayName(stat) + " " + percent;
    }

    private static string StatDisplayName(string stat)
    {
        if (StatNameLangKeys.TryGetValue(stat, out string? langKey)) return Lang.Get(langKey);

        // Make the code readable: "jumpHeightMul" -> "Jump height", "HoD:ThirstRateMul" -> "Thirst rate"
        stat = stat.Substring(stat.IndexOf(':') + 1);
        string code = stat.EndsWith("Mul") ? stat.Substring(0, stat.Length - 3) : stat;
        var words = new System.Text.StringBuilder();
        foreach (char c in code)
        {
            if (char.IsUpper(c) && words.Length > 0) words.Append(' ');
            words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return words.ToString();
    }

    /// <summary>
    /// Adds a runtime lang entry. GetAllEntries returns the live lookup table, whose keys carry the domain.
    /// Lang.Get falls back to the default locale, so the entry is added to both. A null value removes it.
    /// </summary>
    private static void SetLang(string key, string? value)
    {
        foreach (string locale in new[] { Lang.CurrentLocale, Lang.DefaultLocale }.Distinct())
        {
            if (locale == null || !Lang.AvailableLanguages.TryGetValue(locale, out var translations)) continue;

            var entries = translations.GetAllEntries();
            if (value != null) entries["game:" + key] = value;
            else entries.Remove("game:" + key);
        }
    }

    public void Dispose()
    {
        if (instance == this) instance = null;
    }
}
