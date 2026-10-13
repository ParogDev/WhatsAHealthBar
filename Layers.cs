using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace WhatsAHealthBar;

// The information layers ported from syrairc's ExileNameplates (PoE1): style patches, regex overrides, status icons and
// the ignore list. These are lists, so they can't be [Menu] nodes: they are saved with the settings, edited in the custom
// settings section (SettingsPanel.cs) and through the bridge (WhatsAHealthBar.GetLayers / SetLayers). Colors are
// "#AARRGGBB" strings so the JSON stays readable for the control center.

/// <summary>
/// A partial look: every field is optional and only the set ones change the bar. Applied in order per bar, at the classify
/// rate: the kind's look, then a matching override, then the patches of statuses the monster has, then its state
/// (invulnerable). The last one to set a field wins.
/// </summary>
public sealed class StylePatch
{
    /// <summary>True: the bar is not drawn at all.</summary>
    public bool? Hide { get; set; }
    public string? LifeColor { get; set; }
    public string? EsColor { get; set; }
    public string? WardColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? OutlineColor { get; set; }
    /// <summary>Color of the bar's name and data texts.</summary>
    public string? TextColor { get; set; }
    /// <summary>Multiplies the whole bar's opacity (0-1).</summary>
    public float? Opacity { get; set; }
    /// <summary>Desaturates and darkens the pool fills.</summary>
    public bool? Grey { get; set; }
    /// <summary>Dark stripes across the fill.</summary>
    public bool? Hatch { get; set; }

    [JsonIgnore]
    public bool HasAny => Hide.HasValue || LifeColor != null || EsColor != null || WardColor != null || BackgroundColor != null ||
                          OutlineColor != null || TextColor != null || Opacity.HasValue || Grey.HasValue || Hatch.HasValue;
}

/// <summary>A <see cref="StylePatch"/> with its colors parsed (once per settings change, never per frame).</summary>
public sealed class CompiledPatch
{
    public bool? Hide, Grey, Hatch;
    public uint? Life, Es, Ward, Bg, Outline, Text;
    public float? Opacity;
    /// <summary>Fields that didn't parse ("LifeColor: 'red' is not #AARRGGBB"), or null.</summary>
    public string? Error;

    public static CompiledPatch? From(StylePatch? p)
    {
        if (p == null || !p.HasAny) return null;
        var c = new CompiledPatch { Hide = p.Hide, Grey = p.Grey, Hatch = p.Hatch, Opacity = p.Opacity };
        c.Life = Color(p.LifeColor, nameof(p.LifeColor), c);
        c.Es = Color(p.EsColor, nameof(p.EsColor), c);
        c.Ward = Color(p.WardColor, nameof(p.WardColor), c);
        c.Bg = Color(p.BackgroundColor, nameof(p.BackgroundColor), c);
        c.Outline = Color(p.OutlineColor, nameof(p.OutlineColor), c);
        c.Text = Color(p.TextColor, nameof(p.TextColor), c);
        return c;
    }

    private static uint? Color(string? s, string field, CompiledPatch c)
    {
        if (s == null) return null;
        if (Colors.TryParse(s, out var v)) return v;
        c.Error = (c.Error == null ? "" : c.Error + "; ") + $"{field}: '{s}' is not #AARRGGBB or #RRGGBB";
        return null;
    }

    public void ApplyTo(ref BarLook look)
    {
        if (Hide.HasValue) look.Hide = Hide.Value;
        if (Life.HasValue) look.Life = Life.Value;
        if (Es.HasValue) look.Es = Es.Value;
        if (Ward.HasValue) look.Ward = Ward.Value;
        if (Bg.HasValue) look.Bg = Bg.Value;
        if (Outline.HasValue) look.Outline = Outline.Value;
        if (Text.HasValue) look.Text = look.Name = Text.Value;
        if (Opacity.HasValue) look.Opacity = Math.Clamp(Opacity.Value, 0, 1);
        if (Grey.HasValue) look.Grey = Grey.Value;
        if (Hatch.HasValue) look.Hatch = Hatch.Value;
    }
}

/// <summary>A bar's resolved look (packed ImGui ABGR colors), held per bar between classify passes.</summary>
public struct BarLook
{
    public uint Life, Es, Ward, Bg, Outline, Text, Name;
    public float Opacity;
    public bool Grey, Hatch, Hide;
}

public static class Colors
{
    /// <summary>"#AARRGGBB" or "#RRGGBB" to ImGui's packed ABGR.</summary>
    public static bool TryParse(string s, out uint abgr)
    {
        abgr = 0;
        var t = s.Trim().TrimStart('#');
        if (t.Length == 6) t = "FF" + t;
        if (t.Length != 8 || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return false;
        abgr = (argb & 0xFF00FF00) | ((argb >> 16) & 0xFF) | ((argb & 0xFF) << 16);
        return true;
    }

    public static string Format(uint abgr) =>
        "#" + ((abgr & 0xFF00FF00) | ((abgr >> 16) & 0xFF) | ((abgr & 0xFF) << 16)).ToString("X8", CultureInfo.InvariantCulture);
}

/// <summary>How a status icon is drawn: the icon alone, the icon on a gem badge (one cell), or the icon over a tintable badge.</summary>
public static class Badges
{
    public const string Bare = "Bare", Gem = "Gem", OverBadge = "Over badge";
    public static readonly string[] All = { Bare, Gem, OverBadge };
}

/// <summary>One status (buff or debuff) to show as an icon, matched by the buff's definition id.</summary>
public sealed class StatusRule
{
    /// <summary>The buff definition id, as Buff.Name reads it in game (e.g. "ignited", "curse_vulnerability").</summary>
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Debuffs and buffs are separate icon blocks.</summary>
    public bool Debuff { get; set; } = true;
    /// <summary>A <see cref="StatusAtlas"/> icon name (StatusIcon enum).</summary>
    public string Icon { get; set; } = nameof(StatusIcon.Cursed);
    public string Tint { get; set; } = "#FFFFFFFF";
    public string Badge { get; set; } = Badges.Bare;
    public string BadgeTint { get; set; } = "#FF2A2630";
    public float Scale { get; set; } = 1f;
    /// <summary>Changes the whole bar while the monster has this status (e.g. an orange outline while cursed).</summary>
    public StylePatch? Patch { get; set; }
}

/// <summary>A look for specific monsters: regex on the metadata path (default) or the name, first match wins.</summary>
public sealed class OverrideRule
{
    public string Name { get; set; } = "New override";
    public bool Enabled { get; set; } = true;
    /// <summary>"Path" (metadata path, stable across languages and patches) or "Name" (display name; can be empty in PoE2).</summary>
    public string Match { get; set; } = "Path";
    public string Pattern { get; set; } = "";
    public StylePatch Patch { get; set; } = new();
}

/// <summary>The rules compiled for use at the classify rate: regexes built, colors parsed, status ids hashed.</summary>
public sealed class CompiledLayers
{
    public readonly Dictionary<string, int> StatusById = new(StringComparer.Ordinal);
    public StatusRule[] Statuses = Array.Empty<StatusRule>();
    public CompiledPatch?[] StatusPatches = Array.Empty<CompiledPatch?>();
    public int[] StatusIcons = Array.Empty<int>();
    public uint[] StatusTints = Array.Empty<uint>(), StatusBadgeTints = Array.Empty<uint>();
    public byte[] StatusBadges = Array.Empty<byte>();
    public bool AnyStatusPatch;

    public OverrideRule[] Overrides = Array.Empty<OverrideRule>();
    public Regex?[] OverrideRx = Array.Empty<Regex?>();
    public CompiledPatch?[] OverridePatches = Array.Empty<CompiledPatch?>();

    public readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Problems found while compiling (invalid regex, unknown icon, bad color), reported in GetState and the panel.</summary>
    public readonly List<string> Problems = new();

    public static CompiledLayers Build(WhatsAHealthBarSettings s)
    {
        var c = new CompiledLayers();
        var statuses = s.Statuses ?? new List<StatusRule>();
        c.Statuses = statuses.ToArray();
        var n = c.Statuses.Length;
        c.StatusPatches = new CompiledPatch?[n];
        c.StatusIcons = new int[n];
        c.StatusTints = new uint[n];
        c.StatusBadgeTints = new uint[n];
        c.StatusBadges = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var r = c.Statuses[i];
            if (r == null || !r.Enabled || string.IsNullOrWhiteSpace(r.Id)) continue;
            if (!c.StatusById.TryAdd(r.Id.Trim(), i)) c.Problems.Add($"status '{r.Id}' is listed twice: the first one is used");
            if (!Enum.TryParse<StatusIcon>(r.Icon, true, out var icon))
            {
                c.Problems.Add($"status '{r.Id}': icon '{r.Icon}' is not one of the {StatusAtlas.Count} atlas icons; Cursed is drawn");
                icon = StatusIcon.Cursed;
            }
            c.StatusIcons[i] = (int)icon;
            if (!Colors.TryParse(r.Tint ?? "", out c.StatusTints[i])) { c.StatusTints[i] = 0xFFFFFFFF; c.Problems.Add($"status '{r.Id}': tint '{r.Tint}' is not #AARRGGBB"); }
            if (!Colors.TryParse(r.BadgeTint ?? "", out c.StatusBadgeTints[i])) c.StatusBadgeTints[i] = 0xFF30262A;
            c.StatusBadges[i] = (byte)Math.Max(0, Array.IndexOf(Badges.All, r.Badge));
            c.StatusPatches[i] = CompiledPatch.From(r.Patch);
            if (c.StatusPatches[i]?.Error is { } pe) c.Problems.Add($"status '{r.Id}' patch: {pe}");
            c.AnyStatusPatch |= c.StatusPatches[i] != null;
        }

        var overrides = s.Overrides ?? new List<OverrideRule>();
        c.Overrides = overrides.ToArray();
        c.OverrideRx = new Regex?[c.Overrides.Length];
        c.OverridePatches = new CompiledPatch?[c.Overrides.Length];
        for (var i = 0; i < c.Overrides.Length; i++)
        {
            var o = c.Overrides[i];
            if (o == null || !o.Enabled || string.IsNullOrEmpty(o.Pattern)) continue;
            try { c.OverrideRx[i] = new Regex(o.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(5)); }
            catch (ArgumentException ex) { c.Problems.Add($"override '{o.Name}': pattern '{o.Pattern}' is not a valid regex ({ex.Message}); it matches nothing"); }
            c.OverridePatches[i] = CompiledPatch.From(o.Patch);
            if (c.OverridePatches[i]?.Error is { } pe) c.Problems.Add($"override '{o.Name}' patch: {pe}");
        }

        foreach (var p in s.IgnoredPaths ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(p)) c.Ignored.Add(p.Trim());
        return c;
    }

    /// <summary>
    /// Fills null strings (a SetLayers call or a hand-edited settings file with "label": null) with their defaults, so
    /// nothing downstream has to guard them; drops null entries.
    /// </summary>
    public static void Normalize(WhatsAHealthBarSettings s)
    {
        s.Statuses ??= new List<StatusRule>();
        s.Overrides ??= new List<OverrideRule>();
        s.IgnoredPaths ??= new List<string>();
        s.Statuses.RemoveAll(r => r == null);
        s.Overrides.RemoveAll(o => o == null);
        var ignored = s.IgnoredPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.IgnoredPaths.Clear();
        s.IgnoredPaths.AddRange(ignored);
        foreach (var r in s.Statuses)
        {
            r.Id ??= ""; r.Label ??= ""; r.Icon ??= nameof(StatusIcon.Cursed); r.Tint ??= "#FFFFFFFF";
            r.Badge ??= Badges.Bare; r.BadgeTint ??= "#FF2A2630";
        }
        foreach (var o in s.Overrides)
        {
            o.Name ??= ""; o.Match ??= "Path"; o.Pattern ??= ""; o.Patch ??= new StylePatch();
        }
    }

    /// <summary>The first enabled override whose regex matches, or -1. A regex timing out counts as no match.</summary>
    public int MatchOverride(string path, string name)
    {
        for (var i = 0; i < Overrides.Length; i++)
        {
            var rx = OverrideRx[i];
            if (rx == null) continue;
            var target = string.Equals(Overrides[i].Match, "Name", StringComparison.OrdinalIgnoreCase) ? name : path;
            try { if (!string.IsNullOrEmpty(target) && rx.IsMatch(target)) return i; }
            catch (RegexMatchTimeoutException) { }
        }
        return -1;
    }

    /// <summary>A metadata path without its "@level" suffix: the key for overrides and the ignore list.</summary>
    public static string PathKey(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var at = path.LastIndexOf('@');
        return at > 0 ? path[..at] : path;
    }
}

/// <summary>
/// The 96 status glyphs of syrairc's atlas (8 columns x 12 rows), in cell order. The runtime atlas
/// (textures/status-atlas.png, built by textures/build-atlas.ps1) holds them twice, gem-badged then bare, plus the plain
/// badge.
/// </summary>
public enum StatusIcon
{
    Ignite, Burning, Chilled, Frozen, Shocked, Electrocuted, Frostbite, Scorched,
    Bleeding, Hemorrhage, CorruptedBlood, Poisoned, Caustic, Decay, Plagued, Withered,
    Stunned, Immobilized, Bound, Blinded, Taunted, Maimed, Hindered, Crushed,
    Exposed, Vulnerable, Cursed, Doomed, Marked, Enfeebled, Hexed, Silenced,
    Enraged, Berserk, Frenzy, AngerAura, UnholyMight, Impale, Adrenaline, CriticalMark,
    Fortify, Endurance, MoltenShell, EnergyShield, Block, Regeneration, Haste, Onslaught,
    FrenzyCharge, EnduranceCharge, PowerCharge, HatredAura, WrathAura, DeterminationAura, GraceAura, ClarityAura,
    Armour, Evasion, Barrier, Immunity, Ward, Deflect, LifeFlask, Recoup,
    Phasing, Tailwind, Sprint, Slowed, Leech, Blink, Knockback, Temporal,
    Petrified, Webbed, Pinned, Disarmed, Smothered, Confused, Charmed, Possessed,
    BurningGround, ChilledGround, ShockedGround, CausticGround, ConsecratedGround, DesecratedGround, BloodGround, FungalGround,
    Totem, Banner, Brand, Trap, Mine, Portal, Beacon, Shrine,
}

public static class StatusAtlas
{
    public const string TextureName = "WhatsAHealthBar.status-atlas", File = "textures/status-atlas.png";
    public const int Count = 96, Columns = 8, Cell = 64, Width = 17 * Cell, Height = 12 * Cell;
    public static readonly string[] Names = Enum.GetNames<StatusIcon>();

    /// <summary>UVs of one cell: <paramref name="sheet"/> 0 = gem-badged icons, 1 = bare icons.</summary>
    public static void Uv(int icon, int sheet, out float u0, out float v0, out float u1, out float v1)
    {
        var x = sheet * Columns * Cell + icon % Columns * Cell;
        var y = icon / Columns * Cell;
        u0 = x / (float)Width; v0 = y / (float)Height;
        u1 = (x + Cell) / (float)Width; v1 = (y + Cell) / (float)Height;
    }

    public static void BadgeUv(out float u0, out float v0, out float u1, out float v1)
    {
        var x = 2 * Columns * Cell;
        u0 = x / (float)Width; v0 = 0;
        u1 = (x + Cell) / (float)Width; v1 = Cell / (float)Height;
    }
}

/// <summary>
/// The PoE2 default status list. Every id is the game's own BuffDefinitions id, read from the running client
/// (Files.BuffDefinitions, 2026-10-12). Those marked "seen" were also recorded on monsters in combat
/// (ignited, poison, shocked, exposure_elemental, herald_of_ash_burning, flammability_ignite_chance_buildup, KaruiAdrenaline).
/// </summary>
public static class StatusDefaults
{
    public static List<StatusRule> Make() => new()
    {
        D("ignited", "Ignited", StatusIcon.Ignite, "#FFFF8A1C"),                       // seen
        D("herald_of_ash_burning", "Burning", StatusIcon.Burning, "#FFFF5A1F"),       // seen
        D("chilled", "Chilled", StatusIcon.Chilled, "#FF6FD2F5"),
        D("frozen", "Frozen", StatusIcon.Frozen, "#FF3FC0FF"),
        D("shocked", "Shocked", StatusIcon.Shocked, "#FFFFEE33"),                     // seen
        D("electrocute_display_buff", "Electrocuted", StatusIcon.Electrocuted, "#FFFFE45C"),
        D("bleeding", "Bleeding", StatusIcon.Bleeding, "#FFE61A1A"),
        D("poison", "Poisoned", StatusIcon.Poisoned, "#FF4AF03A"),                    // seen
        D("withered", "Withered", StatusIcon.Withered, "#FFB57BE8"),
        D("exposure_elemental", "Elemental Exposure", StatusIcon.Exposed, "#FFE8E0C0"), // seen
        D("exposure_fire", "Fire Exposure", StatusIcon.Exposed, "#FFFF7A2E"),
        D("exposure_cold", "Cold Exposure", StatusIcon.Exposed, "#FF6FC8FF"),
        D("exposure_lightning", "Lightning Exposure", StatusIcon.Exposed, "#FFFFE45C"),
        D("broken_armour_debuff", "Fully Broken Armour", StatusIcon.Armour, "#FFFF5040", Badges.OverBadge),
        D("armour_break_tracker", "Armour Break", StatusIcon.Crushed, "#FFD8B48A"),
        D("critical_weakness", "Critical Weakness", StatusIcon.CriticalMark, "#FFFFD34D"),
        D("incision_debuff", "Incision", StatusIcon.Impale, "#FFE07070"),
        D("dazed", "Dazed", StatusIcon.Stunned, "#FFE0E0E0"),
        D("pinned", "Pinned", StatusIcon.Pinned, "#FFD0D0D0"),
        D("blinded", "Blinded", StatusIcon.Blinded, "#FFB0B0B0"),
        D("maim", "Maimed", StatusIcon.Maimed, "#FFC8A070"),
        D("hinder", "Hindered", StatusIcon.Hindered, "#FF9AD07A"),
        D("intimidated", "Intimidated", StatusIcon.Marked, "#FFE0A040"),
        D("curse_vulnerability", "Vulnerability", StatusIcon.Vulnerable, "#FFE0D040", Badges.OverBadge),
        D("curse_enfeeble", "Enfeeble", StatusIcon.Enfeebled, "#FFB0B0FF", Badges.OverBadge),
        D("curse_temporal_chains", "Temporal Chains", StatusIcon.Temporal, "#FF9AD0FF", Badges.OverBadge),
        D("curse_elemental_weakness", "Elemental Weakness", StatusIcon.Cursed, "#FFE8E0C0", Badges.OverBadge),
        D("curse_fire_weakness", "Flammability", StatusIcon.Cursed, "#FFFF7A2E", Badges.OverBadge),
        D("flammability_ignite_chance_buildup", "Flammability", StatusIcon.Cursed, "#FFFF7A2E", Badges.OverBadge), // seen
        D("curse_cold_weakness", "Hypothermia", StatusIcon.Cursed, "#FF6FC8FF", Badges.OverBadge),
        D("curse_lightning_weakness", "Conductivity", StatusIcon.Cursed, "#FFFFE45C", Badges.OverBadge),
        D("curse_chaos_weakness", "Despair", StatusIcon.Doomed, "#FFC070F0", Badges.OverBadge),
        new() { Id = "KaruiAdrenaline", Label = "Adrenaline", Debuff = false, Icon = nameof(StatusIcon.Adrenaline), Tint = "#FFFF4040" }, // seen
    };

    private static StatusRule D(string id, string label, StatusIcon icon, string tint, string badge = Badges.Bare) =>
        new() { Id = id, Label = label, Icon = icon.ToString(), Tint = tint, Badge = badge };
}

/// <summary>Bridge DTO: the three lists (WhatsAHealthBar.GetLayers answers it; SetLayers takes it, null lists unchanged).</summary>
public sealed class LayersDto
{
    public int Schema { get; set; } = 1;
    public List<StatusRule>? Statuses { get; set; }
    public List<OverrideRule>? Overrides { get; set; }
    public List<string>? IgnoredPaths { get; set; }
    /// <summary>GetLayers only: the icon names a status may use, in atlas order, and the badge styles.</summary>
    public string[]? Icons { get; set; }
    public string[]? BadgeStyles { get; set; }
    /// <summary>GetLayers only: what didn't compile (invalid regex, unknown icon, bad color).</summary>
    public List<string>? Problems { get; set; }
}

public sealed class LayersReplyDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public int Statuses { get; set; }
    public int Overrides { get; set; }
    public int IgnoredPaths { get; set; }
    public List<string> Problems { get; set; } = new();
}

/// <summary>One entry of WhatsAHealthBar.SearchBuffs: a buff definition from the game's own data.</summary>
public sealed class BuffDefDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Invisible { get; set; }
    public bool Listed { get; set; }
}
