using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>Why a bar is or isn't drawn, as a code and a sentence naming the link that decided it (the overlay and the bridge).</summary>
public static class WhyText
{
    private static readonly string[] Codes =
    {
        "drawn", "not_classified", "invalid", "dead", "no_life_component", "no_render_component", "hidden", "ignored", "untargetable",
        "kind_hidden", "too_far", "minion_not_hurt", "normal_not_hit", "override_hidden", "status_hidden", "invulnerable", "cullable",
        "off_screen", "no_pools",
    };

    public static string Code(Why w) => (int)w < Codes.Length ? Codes[(int)w] : w.ToString();

    public static string? Detail(Bar b, WhatsAHealthBarSettings s) => b.Why switch
    {
        Why.NotClassified => "added since the last classify pass",
        Why.Invalid => "Entity.IsValid is false (left the area or despawning)",
        Why.Dead => "Entity.IsAlive is false",
        Why.NoLifeComponent => "the entity has no Life component",
        Why.NoRenderComponent => "the entity has no Render component (no position to draw at)",
        Why.Hidden => "Entity.IsHidden (not emerged / burrowed); States > Hidden monsters = Hide",
        Why.Ignored => $"its path {b.PathKey} is on the ignore list",
        Why.Untargetable => "Entity.IsTargetable is false; Visibility > Hide untargetable is on",
        Why.KindHidden => $"{b.Kind} bars are off ({b.Kind} > Show)",
        Why.TooFar => $"{b.Dist:0} grid units away, over Visibility > Max distance {s.Visibility.MaxDistance.Value}",
        Why.MinionNotHurt => $"life at or over Visibility > Minions only when hurt ({s.Visibility.MinionBelowPercent.Value}%)",
        Why.NormalNotHit => "full life and not hit yet; Visibility > Hide full-life normals is on",
        Why.OverrideHidden => "an override hides it",
        Why.StatusHidden => "a status it has hides the bar (status patch Hide)",
        Why.Invulnerable => "GameStat.CannotBeDamaged is set; States > Invulnerable = Hide",
        Why.Cullable => $"life {b.Hp}/{b.MaxHp} is under its cull threshold {b.CullAt * 100:0.#}%; States > Cullable = Hide",
        Why.OffScreen => "it projects outside the game window",
        Why.NoPools => "no pool with a max (or an implausible life read: see poolProblem)",
        _ => null,
    };
}

/// <summary>
/// Debug overlay (Debug > Why overlay): a small block over each monster near you with why its bar is or isn't drawn, the
/// cached state behind it, and a [+ ignore] button that adds its path to the ignore list. Debug only: it allocates and
/// measures text every frame, outside the plugin's timings.
/// </summary>
public static class WhyOverlay
{
    private const ImGuiWindowFlags ButtonFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;

    public static void Draw(WhatsAHealthBar plugin, List<Bar> bars, WhatsAHealthBarSettings s)
    {
        var gc = plugin.GameController;
        var cam = gc.IngameState?.Camera;
        if (cam == null) return;
        var dl = ImGui.GetBackgroundDrawList();
        var range = s.Debug.WhyRange.Value;
        var win = gc.Window.GetWindowRectangleReal();
        var rules = plugin.Rules;
        var buttons = 0;
        var lines = new List<(string text, uint col)>(4);
        foreach (var b in bars)
        {
            var e = b.Entity;
            if (e == null || !e.IsValid) continue;
            if (e.DistancePlayer > range) continue;
            var render = b.Render ?? e.GetComponent<ExileCore2.PoEMemory.Components.Render>();
            if (render == null) continue;
            var p = render.Pos;
            var sp = cam.WorldToScreen(p with { Z = p.Z - render.Bounds.Z });
            if (sp == Vector2.Zero || sp.X < 0 || sp.Y < 0 || sp.X > win.Width || sp.Y > win.Height) continue;

            var drawn = b.Why == Why.Drawn;
            lines.Clear();
            var detail = WhyText.Detail(b, s);
            lines.Add((WhyText.Code(b.Why) + (detail != null ? ": " + detail : ""), drawn ? 0xFF6EDC6Eu : 0xFF5A5AEBu));
            var facts = $"{b.Kind}  d {e.DistancePlayer:0}  {(e.IsTargetable ? "targetable" : "UNTARGETABLE")}" +
                        (e.IsHidden ? "  HIDDEN" : "") + (b.Invulnerable ? "  INVULNERABLE" : "") +
                        (b.CullAt > 0 ? $"  cull<={b.CullAt * 100:0.#}%" : "") +
                        (b.OverrideIndex >= 0 && b.OverrideIndex < rules.Overrides.Length ? $"  override '{rules.Overrides[b.OverrideIndex].Name}'" : "") +
                        (b.StatusCount > 0 ? $"  statuses {b.StatusCount}" : "") +
                        (b.DotRate > 0 ? $"  dot {b.DotRate:0}/s" : "");
            lines.Add((facts, 0xFFE6E6E6u));
            var key = b.PathKey.Length > 0 ? b.PathKey : CompiledLayers.PathKey(e.Path);
            lines.Add((key, 0xFFB4B4B4u));

            var w = 0f; var h = 0f;
            foreach (var (text, _) in lines) { var sz = ImGui.CalcTextSize(text); w = MathF.Max(w, sz.X); h += sz.Y; }
            var left = MathF.Round(sp.X - w * 0.5f - 4);
            var top = MathF.Round(sp.Y - h - 34);
            dl.AddRectFilled(new Vector2(left, top), new Vector2(left + w + 8, top + h + 6), 0xB4000000);
            var y = top + 3;
            foreach (var (text, col) in lines) { dl.AddText(new Vector2(left + 4, y), col, text); y += ImGui.CalcTextSize(text).Y; }

            // No ignore button on players (a player's path is the class's: ignoring it would hide every player of that class).
            if (buttons >= 24 || key.Length == 0 || b.Kind is BarKind.Player or BarKind.Self || rules.Ignored.Contains(key)) continue;
            buttons++;
            ImGui.SetNextWindowPos(new Vector2(left, top - 26));
            if (ImGui.Begin($"##whaIgnore{b.Id}", ButtonFlags))
                if (ImGui.SmallButton("+ ignore")) plugin.Ignore(key);
            ImGui.End();
        }
    }
}
