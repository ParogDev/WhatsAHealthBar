using System.Collections.Generic;
using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace WhatsAHealthBar;

/// <summary>
/// Every node carries a [Menu] label and tooltip: the HUD menu and the Hexile control center (Health Bars page, generic
/// settings form) both read them. Nested classes are groups there. Changes apply live: the renderer reads the nodes each frame.
/// </summary>
public sealed class WhatsAHealthBarSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    [Menu("Visibility")]
    public VisibilitySettings Visibility { get; set; } = new();

    [Menu("Animation")]
    public AnimationSettings Animation { get; set; } = new();

    [Menu("Look")]
    public LookSettings Look { get; set; } = new();

    [Menu("Text", "Names and data texts above, on and below the bars (per kind templates are in each kind's group)")]
    public TextSettings Text { get; set; } = new();

    [Menu("Status icons", "Ailments, curses and buffs on monsters as icons by the bar. The list of statuses is edited below the " +
                          "settings (Statuses), or from the control center")]
    public StatusIconSettings Icons { get; set; } = new();

    [Menu("States", "How invulnerable, cullable and hidden monsters' bars look")]
    public StateSettings States { get; set; } = new();

    [Menu("Damage over time", "Shows the life that running ailments (ignite, bleed, poison...) will still take")]
    public DotSettings Dot { get; set; } = new();

    [Menu("Unique enemies")]
    public KindSettings Unique { get; set; } = KindSettings.Make(true, 150, 16, Color.FromArgb(255, 232, 120, 36), 10, true, "{name}");

    [Menu("Rare enemies")]
    public KindSettings Rare { get; set; } = KindSettings.Make(true, 118, 14, Color.FromArgb(255, 226, 196, 52), 4, true, "{name}");

    [Menu("Magic enemies")]
    public KindSettings Magic { get; set; } = KindSettings.Make(true, 86, 10, Color.FromArgb(255, 92, 132, 255), 0, false);

    [Menu("Normal enemies")]
    public KindSettings Normal { get; set; } = KindSettings.Make(true, 68, 6, Color.FromArgb(255, 214, 58, 52), 0, false);

    [Menu("Minions and allies")]
    public KindSettings Minion { get; set; } = KindSettings.Make(true, 56, 5, Color.FromArgb(255, 72, 196, 110), 0, false);

    [Menu("Other players")]
    public KindSettings Player { get; set; } = KindSettings.Make(true, 96, 10, Color.FromArgb(255, 64, 200, 140), 0, false);

    [Menu("You")]
    public KindSettings Self { get; set; } = KindSettings.Make(false, 96, 10, Color.FromArgb(255, 64, 200, 140), 0, false);

    [Menu("Motion")]
    public MotionSettings Motion { get; set; } = new();

    [Menu("Bridge")]
    public BridgeSettings Bridge { get; set; } = new();

    [Menu("Debug")]
    public DebugSettings Debug { get; set; } = new();

    /// <summary>Statuses shown as icons (PoE2 defaults from the game's own buff ids). Edited in the custom section and via the bridge.</summary>
    [IgnoreMenu]
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<StatusRule> Statuses { get; set; } = StatusDefaults.Make();

    /// <summary>Per-monster looks by metadata path or name regex, first match wins.</summary>
    [IgnoreMenu]
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<OverrideRule> Overrides { get; set; } = new();

    /// <summary>Metadata paths (without "@level") that never get a bar.</summary>
    [IgnoreMenu]
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> IgnoredPaths { get; set; } = new();

    [JsonIgnore]
    [Menu("Preview in game", "Draws one sample bar per kind in the middle of the screen for 8 seconds, with fake hits and heals (also from the control center)")]
    public ButtonNode Preview { get; set; } = new();
}

/// <summary>The Pools and Total line choices.</summary>
public static class Bars
{
    public const string Sandwich = "Sandwich", SideBySide = "Side by side", LifeOnly = "Life only";
    public const string TotalOff = "Off", TotalRareUnique = "Rares and uniques", TotalAll = "All";
}

[Submenu(CollapsedByDefault = true)]
public sealed class VisibilitySettings
{
    [Menu("Show in town")]
    public ToggleNode ShowInTown { get; set; } = new(false);

    [Menu("Show in hideout")]
    public ToggleNode ShowInHideout { get; set; } = new(true);

    [Menu("Hide behind big panels", "Hide all bars while a fullscreen or large panel (inventory, skill tree, stash) is open")]
    public ToggleNode HideWhenPanelsOpen { get; set; } = new(true);

    [Menu("Max distance", "Grid units from you; bars further away are not drawn")]
    public RangeNode<int> MaxDistance { get; set; } = new(140, 20, 400);

    [Menu("Minions only when hurt", "Show minion and ally bars only below this life %; 100 always shows them")]
    public RangeNode<int> MinionBelowPercent { get; set; } = new(99, 1, 100);

    [Menu("Hide full-life normals", "Normal enemies at full life draw no bar until they are hit")]
    public ToggleNode HideFullNormals { get; set; } = new(false);

    [Menu("Hide untargetable", "No bar for monsters you can't target (decorations, emerging or submerged monsters). Uniques always keep theirs")]
    public ToggleNode HideUntargetable { get; set; } = new(true);

    [Menu("Height offset", "Moves every bar up (negative) or down, in world units")]
    public RangeNode<int> ZOffset { get; set; } = new(0, -300, 300);

    [Menu("Sit on the game's bar", "Place each bar exactly where the game draws its own life bar: the game anchors it at the same " +
                                   "model top this plugin projects, then shifts it 2.3 px left and 4.5 px up (measured at 1080p on " +
                                   "9 monsters of 4 types; scaled with the window height). Off: the bar sits on the model top")]
    public ToggleNode AlignToGameBar { get; set; } = new(true);

    [Menu("Classify rate (Hz)", "How often each monster's kind, hostility and distance are rechecked. Positions and life update every frame")]
    public RangeNode<int> ClassifyHz { get; set; } = new(10, 2, 30);
}

[Submenu(CollapsedByDefault = true)]
public sealed class AnimationSettings
{
    [Menu("Animate", "Off: bars jump straight to the current value (cheapest)")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Damage trail", "A lighter chip shows the damage just taken, holds briefly, then drains")]
    public ToggleNode DamageTrail { get; set; } = new(true);

    [Menu("Trail hold (ms)", "How long the damage chip holds before draining")]
    public RangeNode<int> TrailHoldMs { get; set; } = new(320, 0, 1500);

    [Menu("Trail drain speed", "Higher drains faster")]
    public RangeNode<float> TrailDrain { get; set; } = new(4.5f, 0.5f, 20f);

    [Menu("Hit flash", "The bar's edge flashes white on a hit")]
    public ToggleNode HitFlash { get; set; } = new(true);

    [Menu("Heal glow", "Healing fills smoothly with a glow")]
    public ToggleNode HealGlow { get; set; } = new(true);

    [Menu("Fade in/out (ms)", "Bars fade in when a monster appears and out when it dies")]
    public RangeNode<int> FadeMs { get; set; } = new(180, 0, 1000);

    [Menu("Low life pulse %", "Rare and unique bars pulse below this life %; 0 turns it off")]
    public RangeNode<int> LowLifePulse { get; set; } = new(20, 0, 60);
}

[Submenu(CollapsedByDefault = true)]
public sealed class LookSettings
{
    [Menu("Scale", "Multiplies every bar's width and height")]
    public RangeNode<float> Scale { get; set; } = new(1f, 0.5f, 3f);

    [Menu("Scale with resolution", "Sizes are for 1440p; other window heights scale them")]
    public ToggleNode ScaleWithResolution { get; set; } = new(true);

    [Menu("Corner rounding (px)")]
    public RangeNode<float> Rounding { get; set; } = new(2f, 0f, 8f);

    [Menu("Outline (px)")]
    public RangeNode<int> Outline { get; set; } = new(1, 0, 4);

    [Menu("Outline")]
    public ColorNode OutlineColor { get; set; } = Color.FromArgb(230, 8, 8, 10);

    [Menu("Background")]
    public ColorNode BackgroundColor { get; set; } = Color.FromArgb(200, 22, 20, 24);

    [Menu("Damage trail")]
    public ColorNode TrailColor { get; set; } = Color.FromArgb(215, 255, 250, 242);

    [Menu("Heal glow")]
    public ColorNode HealColor { get; set; } = Color.FromArgb(220, 150, 255, 170);

    [Menu("Pools", "Life, energy shield and ward each fill and drain on their own (ES recharges while life stays low, ward restores " +
                   "on its timer). Sandwich: the pools are layers stacked top to bottom (ward, ES, life), each as thick as its share " +
                   "of the combined max, so together they are always the whole bar; each layer fills left to right by its own value. " +
                   "Side by side: each pool owns a region of the width by its max share, with dividers. Life only: ES and ward hidden")]
    public ListNode Pools { get; set; } = new() { Values = new() { Bars.Sandwich, Bars.SideBySide, Bars.LifeOnly }, Value = Bars.Sandwich };

    [Menu("Total line", "A thin line under the bar showing life + ES + ward together, as a share of their sum")]
    public ListNode TotalLine { get; set; } = new() { Values = new() { Bars.TotalOff, Bars.TotalRareUnique, Bars.TotalAll }, Value = Bars.TotalRareUnique };

    [Menu("Energy shield")]
    public ColorNode EsColor { get; set; } = Color.FromArgb(245, 176, 226, 255);

    [Menu("Ward")]
    public ColorNode WardColor { get; set; } = Color.FromArgb(245, 176, 132, 255);

    [Menu("Pool dividers", "Side by side: a dark notch between the life, ES and ward regions")]
    public ToggleNode PoolDividers { get; set; } = new(true);

    [Menu("Gloss", "Strength of the shine on the top half of the fill")]
    public RangeNode<float> Gloss { get; set; } = new(0.22f, 0f, 0.6f);

    [Menu("Shading", "How much darker the bottom of the fill is")]
    public RangeNode<float> Shading { get; set; } = new(0.35f, 0f, 0.8f);

    [Menu("Text")]
    public ColorNode TextColor { get; set; } = Color.FromArgb(255, 245, 240, 230);

    /// <summary>
    /// The old single culling marker (replaced by States > Cullable, per rarity). Read once at load: a value above 0 turns
    /// the per-rarity notch on (the per-rarity thresholds replace the single %), then it is cleared.
    /// </summary>
    [IgnoreMenu]
    public RangeNode<int> CullPercent { get; set; } = new(0, 0, 30);
}

[Submenu(CollapsedByDefault = true)]
public sealed class TextSettings
{
    [Menu("Abbreviate names longer than", "Characters; longer names keep the last word and initials (\"S. S. Captain\"). 0: never")]
    public RangeNode<int> AbbreviateOver { get; set; } = new(20, 0, 40);

    [Menu("Text size", "Multiplies the HUD font size for names and data texts")]
    public RangeNode<float> Scale { get; set; } = new(1f, 0.6f, 2f);

    [Menu("Shadow", "A dark copy one pixel down-right, so text reads on any background")]
    public ToggleNode Shadow { get; set; } = new(true);

    [Menu("DPS window (s)", "How quickly {dps} and {ttk} follow the damage: the time constant of the moving average")]
    public RangeNode<float> DpsWindow { get; set; } = new(1.5f, 0.2f, 6f);
}

/// <summary>Where a block of status icons sits next to the bar.</summary>
public static class Anchors
{
    public const string Above = "Above", Below = "Below", Left = "Left", Right = "Right";
    public static readonly string[] All = { Above, Below, Left, Right };
}

[Submenu(CollapsedByDefault = true)]
public sealed class StatusIconSettings
{
    [Menu("Show status icons")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("On", "Which bars get icons (buffs are read only for those)")]
    public ListNode Kinds { get; set; } = new()
        { Values = new() { IconKinds.RareUnique, IconKinds.MagicUp, IconKinds.Enemies, IconKinds.All }, Value = IconKinds.MagicUp };

    [Menu("Debuffs at")]
    public ListNode DebuffAnchor { get; set; } = new() { Values = new(Anchors.All), Value = Anchors.Above };

    [Menu("Buffs at")]
    public ListNode BuffAnchor { get; set; } = new() { Values = new(Anchors.All), Value = Anchors.Right };

    [Menu("Icon size (px)", "At 1440p; scales with the bars")]
    public RangeNode<int> Size { get; set; } = new(18, 8, 64);

    [Menu("Columns", "Icons per row before wrapping")]
    public RangeNode<int> Columns { get; set; } = new(6, 1, 12);

    [Menu("Max icons", "Per block (debuffs, buffs)")]
    public RangeNode<int> MaxIcons { get; set; } = new(6, 1, 12);

    [Menu("Show stacks")]
    public ToggleNode ShowStacks { get; set; } = new(true);

    [Menu("Show duration", "Seconds left under each icon with a timer")]
    public ToggleNode ShowDuration { get; set; } = new(false);
}

public static class IconKinds
{
    public const string RareUnique = "Rares and uniques", MagicUp = "Magic and up", Enemies = "All enemies", All = "Everything";
}

public static class StateModes
{
    public const string Off = "Off", GreyHatch = "Grey and hatch", Hide = "Hide", Notch = "Notch", NotchTint = "Notch and tint", Dim = "Dim";
}

[Submenu(CollapsedByDefault = true)]
public sealed class StateSettings
{
    [Menu("Invulnerable", "Monsters that can't be damaged right now (the CannotBeDamaged stat: immune boss phases, shielded adds)")]
    public ListNode Invulnerable { get; set; } = new()
        { Values = new() { StateModes.Off, StateModes.GreyHatch, StateModes.Hide }, Value = StateModes.GreyHatch };

    [Menu("Invulnerable color", "The fill color while invulnerable")]
    public ColorNode InvulnerableColor { get; set; } = Color.FromArgb(255, 150, 150, 160);

    [Menu("Cullable", "Culling strike kills at a life % by rarity (PoE2: normal 35, magic 20, rare 10, unique 5, from poe2db). " +
                      "Notch: a mark at the threshold. Tint: the fill changes color once under it. Hide: no bar under it")]
    public ListNode Cullable { get; set; } = new()
        { Values = new() { StateModes.Off, StateModes.Notch, StateModes.NotchTint, StateModes.Hide }, Value = StateModes.NotchTint };

    [Menu("Cull % normal")]
    public RangeNode<int> CullNormal { get; set; } = new(35, 0, 60);

    [Menu("Cull % magic")]
    public RangeNode<int> CullMagic { get; set; } = new(20, 0, 60);

    [Menu("Cull % rare")]
    public RangeNode<int> CullRare { get; set; } = new(10, 0, 60);

    [Menu("Cull % unique")]
    public RangeNode<int> CullUnique { get; set; } = new(5, 0, 60);

    [Menu("Cull threshold multiplier", "Increased culling strike threshold from your passives or gear (1.25 = 25% increased)")]
    public RangeNode<float> CullMultiplier { get; set; } = new(1f, 0.5f, 3f);

    [Menu("Cullable color", "The life fill color once under the threshold (Notch and tint)")]
    public ColorNode CullableColor { get; set; } = Color.FromArgb(255, 245, 235, 255);

    [Menu("Hidden monsters", "Monsters the game marks hidden (not yet emerged, burrowed). Hide: no bar. Dim: a faint bar")]
    public ListNode Hidden { get; set; } = new() { Values = new() { StateModes.Hide, StateModes.Dim }, Value = StateModes.Hide };
}

[Submenu(CollapsedByDefault = true)]
public sealed class DotSettings
{
    [Menu("Show predicted loss", "A dark hatched chunk at the end of the life fill: what the damage over time running now will still " +
                                 "take (rate x time left on the longest ailment)")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Chunk color")]
    public ColorNode Color { get; set; } = System.Drawing.Color.FromArgb(150, 0, 0, 0);

    [Menu("Fallback seconds", "Time assumed when the damage over time has no ailment with a timer")]
    public RangeNode<float> FallbackSeconds { get; set; } = new(3f, 0.5f, 10f);
}

[Submenu(CollapsedByDefault = true)]
public sealed class DebugSettings
{
    [Menu("Why overlay", "Over every monster near you: why its bar is or isn't drawn (valid, alive, targetable, distance, kind, " +
                         "state, override, ignored), with a [+ ignore] button that adds its path to the ignore list")]
    public ToggleNode WhyOverlay { get; set; } = new(false);

    [Menu("Why overlay range", "Grid units")]
    public RangeNode<int> WhyRange { get; set; } = new(90, 20, 300);
}

[Submenu(CollapsedByDefault = true)]
public sealed class KindSettings
{
    [Menu("Show")]
    public ToggleNode Show { get; set; } = new(true);

    [Menu("Width (px)")]
    public RangeNode<int> Width { get; set; } = new(80, 20, 300);

    [Menu("Height (px)")]
    public RangeNode<int> Height { get; set; } = new(8, 3, 40);

    [Menu("Life")]
    public ColorNode LifeColor { get; set; } = Color.Red;

    [Menu("Segments", "Divider notches across the bar (0: none), e.g. 10 = every 10%")]
    public RangeNode<int> Segments { get; set; } = new(0, 0, 20);

    [Menu("Show percent", "Life + ES + ward together, as a % of their sum")]
    public ToggleNode ShowPercent { get; set; } = new(false);

    [Menu("Text above", "Template: {name} {hp} {hp%} {life} {life%} {es} {es%} {ward} {dist} {rarity} {dps} {ttk} {dotloss}; empty: none")]
    public TextNode TextAbove { get; set; } = new("");

    [Menu("Text on bar", "Same tokens, centered on the bar")]
    public TextNode TextOn { get; set; } = new("");

    [Menu("Text below", "Same tokens, under the bar")]
    public TextNode TextBelow { get; set; } = new("");

    public static KindSettings Make(bool show, int width, int height, Color life, int segments, bool percent, string above = "") => new()
    {
        Show = new ToggleNode(show),
        Width = new RangeNode<int>(width, 20, 300),
        Height = new RangeNode<int>(height, 3, 40),
        LifeColor = life,
        Segments = new RangeNode<int>(segments, 0, 20),
        ShowPercent = new ToggleNode(percent),
        TextAbove = new TextNode(above),
    };
}

[Submenu(CollapsedByDefault = true)]
public sealed class MotionSettings
{
    [Menu("Smooth motion", "The overlay runs on its own clock, so some HUD frames see the same game frame twice and others skip " +
                           "one: bars stutter. On: each bar is drawn between the last two game frames, evenly spaced, at a " +
                           "constant lag of about one game frame (the state's motion.lagMs)")]
    public ToggleNode Interpolate { get; set; } = new(true);

    [Menu("Sub-pixel (experimental)", "Off by default: blending copies made bars flicker in play. Moving bars are drawn at fractional positions (blended into the next pixel) instead of jumping a whole " +
                       "pixel at a time; bars at rest snap to whole pixels so they stay crisp and don't flicker between two")]
    public ToggleNode SubPixel { get; set; } = new(false);
}

[Submenu(CollapsedByDefault = true)]
public sealed class BridgeSettings
{
    [Menu("Publish kills", "Publish an event (kind healthbars.kill) through the AI Bridge when a unique, or also a rare, dies: " +
                           "name, time to kill, area. Agents and other plugins subscribe to it")]
    public ListNode PublishKills { get; set; } = new() { Values = new() { "Off", "Uniques", "Rares and uniques" }, Value = "Uniques" };
}
