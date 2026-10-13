using System.Collections.Generic;

namespace WhatsAHealthBar;

/// <summary>What WhatsAHealthBar.GetState answers (PluginBridge, read by the MCP server's healthbars_state and the control center).</summary>
public sealed class HealthBarStateDto
{
    public string Plugin { get; set; } = "Whats A Health Bar";
    public int Schema { get; set; } = 2;
    public bool Enabled { get; set; }
    /// <summary>Why no bars are drawn right now (town, hideout, a panel is open, not in game), or null.</summary>
    public string? Suppressed { get; set; }
    public string? Area { get; set; }
    public HealthBarCountsDto Counts { get; set; } = new();
    public HealthBarTimingsDto Timings { get; set; } = new();
    public HealthBarPreviewDto Preview { get; set; } = new();
    public HealthBarBridgeDto Bridge { get; set; } = new();
    /// <summary>The information layers: list sizes, the icon atlas, what didn't compile.</summary>
    public HealthBarLayersDto Layers { get; set; } = new();
    /// <summary>A pool read that couldn't be real in the last 30 s (which pool, what was read, on which entity), or null.</summary>
    public string? PoolProblem { get; set; }
    /// <summary>The last kills published (newest first, up to 10).</summary>
    public List<HealthBarKillDto> Kills { get; set; } = new();
}

public sealed class HealthBarCountsDto
{
    /// <summary>Monsters and players known to the plugin (EntityAdded minus EntityRemoved).</summary>
    public int Tracked { get; set; }
    /// <summary>Passed classification: alive, in range, shown kind.</summary>
    public int Wanted { get; set; }
    /// <summary>On screen and drawn last frame.</summary>
    public int Drawn { get; set; }
    /// <summary>Most bars drawn in one frame since the area started.</summary>
    public int PeakDrawn { get; set; }
    /// <summary>Drawn last frame per kind: unique, rare, magic, normal, minion, player, self.</summary>
    public Dictionary<string, int> ByKind { get; set; } = new();
    /// <summary>Wanted bars with at least one listed status, invulnerable right now, and taking damage over time (last classify pass).</summary>
    public int WithStatuses { get; set; }
    public int Invulnerable { get; set; }
    public int WithDot { get; set; }
}

/// <summary>The plugin's own timings, measured with Stopwatch around its Tick and Render (microseconds).</summary>
public sealed class HealthBarTimingsDto
{
    public double TickUs { get; set; }
    public double TickMaxUs { get; set; }
    public double RenderUs { get; set; }
    public double RenderMaxUs { get; set; }
    /// <summary>The last classification pass (runs at the classify rate, included in TickUs on those frames).</summary>
    public double ClassifyUs { get; set; }
    /// <summary>(Tick + Render) per drawn bar, nanoseconds: the cost of one more bar on screen.</summary>
    public double PerBarNs { get; set; }
    public int ClassifyHz { get; set; }
    /// <summary>Quads written into the draw list last frame (all bars, one batch).</summary>
    public int Quads { get; set; }
    /// <summary>Status icons written last frame (one more draw command, the icon atlas).</summary>
    public int Icons { get; set; }
    /// <summary>Render split: building the quads from the bars, and writing them into the draw list.</summary>
    public double BuildUs { get; set; }
    public double FlushUs { get; set; }
    /// <summary>"batched" (vertices written straight into the draw list), or "fallback: why" (one ImGui call per quad).</summary>
    public string RenderPath { get; set; } = "";
    /// <summary>Smooth motion: the game frame interval (the interpolation's lag), HUD frames per game frame, and jitter.</summary>
    public HealthBarMotionDto Motion { get; set; } = new();
    /// <summary>Max values cover the last 5 seconds.</summary>
    public int WindowSec { get; set; } = 5;
}

public sealed class HealthBarPreviewDto
{
    public bool Active { get; set; }
    public double SecondsLeft { get; set; }
    /// <summary>The kind the preview puts first (from the control center's focused group), or null.</summary>
    public string? Focus { get; set; }
    /// <summary>Bars the preview draws (7 samples plus the stress grid); PerBarNs counts them while it runs.</summary>
    public int Bars { get; set; }
}

public sealed class HealthBarBridgeDto
{
    public bool Linked { get; set; }
    public string? Why { get; set; }
    public string Subscribes { get; set; } = AiBridgeLink.PreviewKind;
    public string Publishes { get; set; } = AiBridgeLink.KillKind;
    public string PublishKills { get; set; } = "";
}

public sealed class HealthBarMotionDto
{
    public bool Interpolate { get; set; }
    public bool SubPixel { get; set; }
    /// <summary>The measured game frame interval, ms: how far behind the newest game frame interpolated bars are drawn.</summary>
    public double LagMs { get; set; }
    /// <summary>HUD frames per game frame (above 1: the HUD runs faster than the game).</summary>
    public double HudFramesPerGameFrame { get; set; }
    /// <summary>
    /// Average second difference of moving bars' screen positions per HUD frame, px: how unevenly they move. Raw is the
    /// projection as read; Shown is what is drawn. Shown well under Raw = the interpolation removes the stutter.
    /// </summary>
    public double JitterRawPx { get; set; }
    public double JitterShownPx { get; set; }
}

/// <summary>What WhatsAHealthBar.Preview answers.</summary>
public sealed class PreviewReplyDto
{
    public bool Ok { get; set; }
    public double Seconds { get; set; }
    public string? Focus { get; set; }
    /// <summary>Extra bars drawn on a grid to measure the many-bars cost.</summary>
    public int Stress { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
}

/// <summary>The data of a healthbars.kill event.</summary>
public sealed class HealthBarKillDto
{
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string? Path { get; set; }
    public int MaxLife { get; set; }
    public int MaxEs { get; set; }
    public int MaxWard { get; set; }
    /// <summary>From the first hit the plugin saw to death, milliseconds (null when it died before a hit was seen).</summary>
    public int? TimeToKillMs { get; set; }
    public string? Area { get; set; }
    public string At { get; set; } = "";
}

public sealed class HealthBarLayersDto
{
    /// <summary>Enabled statuses, overrides and ignored paths in effect (edit with WhatsAHealthBar.GetLayers / SetLayers).</summary>
    public int Statuses { get; set; }
    public int Overrides { get; set; }
    public int IgnoredPaths { get; set; }
    /// <summary>Why the status icon atlas isn't loaded (no icons drawn), or null.</summary>
    public string? AtlasProblem { get; set; }
    /// <summary>Rules that didn't compile: invalid regex, unknown icon, bad color.</summary>
    public List<string>? Problems { get; set; }
}

/// <summary>What WhatsAHealthBar.WhyNotDrawn answers.</summary>
public sealed class WhyReplyDto
{
    /// <summary>Why no bar is drawn at all right now (town, panel open...), or null: then the per-entity reasons apply.</summary>
    public string? Suppressed { get; set; }
    public int MaxDistance { get; set; }
    public int Tracked { get; set; }
    public string? Message { get; set; }
    public List<WhyEntityDto> Entities { get; set; } = new();
}

public sealed class WhyEntityDto
{
    public uint Id { get; set; }
    public string? Path { get; set; }
    public string? Name { get; set; }
    public string Kind { get; set; } = "";
    public bool Drawn { get; set; }
    /// <summary>drawn | invalid | dead | no_life_component | no_render_component | hidden | ignored | untargetable | kind_hidden |
    /// too_far | minion_not_hurt | normal_not_hit | override_hidden | status_hidden | invulnerable | cullable | off_screen |
    /// no_pools | not_classified.</summary>
    public string Reason { get; set; } = "";
    public string? Detail { get; set; }
    public float Distance { get; set; }
    public bool Invulnerable { get; set; }
    public float? CullAtPercent { get; set; }
    public string? Override { get; set; }
    public List<string> Statuses { get; set; } = new();
    public float? DotPerSecond { get; set; }
    public float? Dps { get; set; }
}