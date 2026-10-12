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

    [Menu("Unique enemies")]
    public KindSettings Unique { get; set; } = KindSettings.Make(true, 150, 16, Color.FromArgb(255, 232, 120, 36), 10, true);

    [Menu("Rare enemies")]
    public KindSettings Rare { get; set; } = KindSettings.Make(true, 118, 14, Color.FromArgb(255, 226, 196, 52), 4, true);

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

    [Menu("Bridge")]
    public BridgeSettings Bridge { get; set; } = new();

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

    [Menu("Height offset", "Moves every bar up (negative) or down, in world units")]
    public RangeNode<int> ZOffset { get; set; } = new(0, -300, 300);

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

    [Menu("Culling marker %", "Draws a notch at this life % (culling strike threshold); 0 turns it off")]
    public RangeNode<int> CullPercent { get; set; } = new(0, 0, 30);
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

    public static KindSettings Make(bool show, int width, int height, Color life, int segments, bool percent) => new()
    {
        Show = new ToggleNode(show),
        Width = new RangeNode<int>(width, 20, 300),
        Height = new RangeNode<int>(height, 3, 40),
        LifeColor = life,
        Segments = new RangeNode<int>(segments, 0, 20),
        ShowPercent = new ToggleNode(percent),
    };
}

[Submenu(CollapsedByDefault = true)]
public sealed class BridgeSettings
{
    [Menu("Publish kills", "Publish an event (kind healthbars.kill) through the AI Bridge when a unique, or also a rare, dies: " +
                           "name, time to kill, area. Agents and other plugins subscribe to it")]
    public ListNode PublishKills { get; set; } = new() { Values = new() { "Off", "Uniques", "Rares and uniques" }, Value = "Uniques" };
}
