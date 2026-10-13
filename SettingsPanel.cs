using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>
/// The list settings the [Menu] nodes can't hold: statuses (icons), overrides and the ignore list. Plain on purpose (the
/// designed panel comes later); every edit recompiles the rules on the next Tick. The same lists are editable through the
/// bridge (WhatsAHealthBar.GetLayers / SetLayers).
/// </summary>
public static class SettingsPanel
{
    private static string _search = "", _ignoreInput = "";
    private static readonly Dictionary<string, string?> RegexErrors = new();

    public static void Draw(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        ImGui.Separator();
        var problems = plugin.Rules.Problems;
        if (problems.Count > 0)
            foreach (var p in problems) ImGui.TextColored(new Vector4(1, 0.45f, 0.4f, 1), p);

        var changed = false;
        lock (plugin.LayersLock)
        {
            if (ImGui.CollapsingHeader($"Statuses ({s.Statuses.Count})###whaStatuses")) changed |= Statuses(plugin, s);
            if (ImGui.CollapsingHeader($"Overrides ({s.Overrides.Count})###whaOverrides")) changed |= Overrides(s);
            if (ImGui.CollapsingHeader($"Ignored monsters ({s.IgnoredPaths.Count})###whaIgnored")) changed |= Ignored(s);
        }
        if (changed) plugin.LayersChanged();
    }

    private static bool Statuses(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        var changed = false;
        ImGui.TextWrapped("Buffs and debuffs shown as icons, matched by the game's buff id. Add from the game's own list:");
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##whaBuffSearch", "search buff ids and names", ref _search, 64);
        if (_search.Trim().Length >= 2)
        {
            var catalog = plugin.BuffCatalog();
            var q = _search.Trim();
            var shown = 0;
            foreach (var d in catalog)
            {
                if (!d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) && !d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                if (++shown > 25) { ImGui.TextDisabled("... more: refine the search"); break; }
                ImGui.PushID("cat" + d.Id);
                var listed = s.Statuses.Any(r => r.Id == d.Id);
                if (listed) ImGui.TextDisabled("listed");
                else if (ImGui.SmallButton("add"))
                {
                    s.Statuses.Add(new StatusRule { Id = d.Id, Label = d.Name.Length > 0 ? d.Name : d.Id });
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.Text($"{d.Id}  {d.Name}{(d.Invisible ? "  (invisible)" : "")}");
                ImGui.PopID();
            }
            if (catalog.Count == 0) ImGui.TextDisabled("Files.BuffDefinitions is empty (not in game yet?)");
        }
        ImGui.Separator();

        for (var i = 0; i < s.Statuses.Count; i++)
        {
            var r = s.Statuses[i];
            ImGui.PushID("st" + i);
            var en = r.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { r.Enabled = en; changed = true; }
            ImGui.SameLine();
            var open = ImGui.TreeNode($"{(r.Label.Length > 0 ? r.Label : r.Id)}  [{r.Id}]###node");
            ImGui.SameLine();
            if (ImGui.SmallButton("^") && i > 0) { (s.Statuses[i - 1], s.Statuses[i]) = (s.Statuses[i], s.Statuses[i - 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("v") && i < s.Statuses.Count - 1) { (s.Statuses[i + 1], s.Statuses[i]) = (s.Statuses[i], s.Statuses[i + 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("remove")) { s.Statuses.RemoveAt(i); changed = true; if (open) ImGui.TreePop(); ImGui.PopID(); break; }
            if (open)
            {
                changed |= Text("Buff id", r.Id, v => r.Id = v.Trim());
                changed |= Text("Label", r.Label, v => r.Label = v);
                var debuff = r.Debuff;
                if (ImGui.Checkbox("Debuff (else buff)", ref debuff)) { r.Debuff = debuff; changed = true; }
                var icon = Math.Max(0, Array.IndexOf(StatusAtlas.Names, r.Icon));
                if (ImGui.Combo("Icon", ref icon, StatusAtlas.Names, StatusAtlas.Names.Length)) { r.Icon = StatusAtlas.Names[icon]; changed = true; }
                changed |= Color("Tint", r.Tint, v => r.Tint = v);
                var badge = Math.Max(0, Array.IndexOf(Badges.All, r.Badge));
                if (ImGui.Combo("Badge", ref badge, Badges.All, Badges.All.Length)) { r.Badge = Badges.All[badge]; changed = true; }
                if (r.Badge == Badges.OverBadge) changed |= Color("Badge tint", r.BadgeTint, v => r.BadgeTint = v);
                var scale = r.Scale;
                if (ImGui.SliderFloat("Scale", ref scale, 0.5f, 3f)) { r.Scale = scale; changed = true; }
                if (ImGui.TreeNode("Bar while present"))
                {
                    r.Patch ??= new StylePatch();
                    changed |= Patch(r.Patch);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        if (ImGui.Button("Restore the PoE2 defaults")) { s.Statuses = StatusDefaults.Make(); changed = true; }
        return changed;
    }

    private static bool Overrides(WhatsAHealthBarSettings s)
    {
        var changed = false;
        ImGui.TextWrapped("A look for specific monsters: a regex on the metadata path (\"Monsters/.*Boss\") or the name. The first match wins.");
        for (var i = 0; i < s.Overrides.Count; i++)
        {
            var o = s.Overrides[i];
            ImGui.PushID("ov" + i);
            var en = o.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { o.Enabled = en; changed = true; }
            ImGui.SameLine();
            var open = ImGui.TreeNode($"{o.Name}  /{o.Pattern}/###node");
            ImGui.SameLine();
            if (ImGui.SmallButton("^") && i > 0) { (s.Overrides[i - 1], s.Overrides[i]) = (s.Overrides[i], s.Overrides[i - 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("v") && i < s.Overrides.Count - 1) { (s.Overrides[i + 1], s.Overrides[i]) = (s.Overrides[i], s.Overrides[i + 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("remove")) { s.Overrides.RemoveAt(i); changed = true; if (open) ImGui.TreePop(); ImGui.PopID(); break; }
            if (open)
            {
                changed |= Text("Name", o.Name, v => o.Name = v);
                var match = o.Match == "Name" ? 1 : 0;
                if (ImGui.Combo("Match", ref match, MatchKinds, 2)) { o.Match = MatchKinds[match]; changed = true; }
                changed |= Text("Pattern", o.Pattern, v => o.Pattern = v);
                if (RegexError(o.Pattern) is { } err) ImGui.TextColored(new Vector4(1, 0.45f, 0.4f, 1), "Invalid regex: " + err);
                o.Patch ??= new StylePatch();
                changed |= Patch(o.Patch);
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        if (ImGui.Button("Add override")) { s.Overrides.Add(new OverrideRule()); changed = true; }
        return changed;
    }

    private static readonly string[] MatchKinds = { "Path", "Name" };

    private static bool Ignored(WhatsAHealthBarSettings s)
    {
        var changed = false;
        ImGui.TextWrapped("Metadata paths (without @level) that never get a bar. Debug > Why overlay adds them with a click.");
        for (var i = 0; i < s.IgnoredPaths.Count; i++)
        {
            ImGui.PushID("ig" + i);
            if (ImGui.SmallButton("remove")) { s.IgnoredPaths.RemoveAt(i); changed = true; ImGui.PopID(); break; }
            ImGui.SameLine();
            ImGui.Text(s.IgnoredPaths[i]);
            ImGui.PopID();
        }
        ImGui.SetNextItemWidth(360);
        ImGui.InputTextWithHint("##whaIgnoreAdd", "Metadata/Monsters/...", ref _ignoreInput, 256);
        ImGui.SameLine();
        if (ImGui.Button("Ignore") && _ignoreInput.Trim().Length > 0)
        {
            var key = CompiledLayers.PathKey(_ignoreInput.Trim());
            if (!s.IgnoredPaths.Contains(key, StringComparer.OrdinalIgnoreCase)) s.IgnoredPaths.Add(key);
            _ignoreInput = "";
            changed = true;
        }
        return changed;
    }

    /// <summary>The optional fields of a patch: each one is off (unchanged) until ticked.</summary>
    private static bool Patch(StylePatch p)
    {
        var changed = false;
        changed |= OptBool("Hide the bar", p.Hide, v => p.Hide = v);
        changed |= OptColor("Life color", p.LifeColor, v => p.LifeColor = v);
        changed |= OptColor("ES color", p.EsColor, v => p.EsColor = v);
        changed |= OptColor("Ward color", p.WardColor, v => p.WardColor = v);
        changed |= OptColor("Background", p.BackgroundColor, v => p.BackgroundColor = v);
        changed |= OptColor("Outline", p.OutlineColor, v => p.OutlineColor = v);
        changed |= OptColor("Text color", p.TextColor, v => p.TextColor = v);
        var hasOp = p.Opacity.HasValue;
        if (ImGui.Checkbox("Opacity##o", ref hasOp)) { p.Opacity = hasOp ? 1f : null; changed = true; }
        if (p.Opacity.HasValue)
        {
            ImGui.SameLine();
            var op = p.Opacity.Value;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("##opv", ref op, 0f, 1f)) { p.Opacity = op; changed = true; }
        }
        changed |= OptBool("Grey", p.Grey, v => p.Grey = v);
        changed |= OptBool("Hatch", p.Hatch, v => p.Hatch = v);
        return changed;
    }

    private static bool OptBool(string label, bool? value, Action<bool?> set)
    {
        var on = value == true;
        if (!ImGui.Checkbox(label, ref on)) return false;
        set(on ? true : null);
        return true;
    }

    private static bool OptColor(string label, string? value, Action<string?> set)
    {
        var has = value != null;
        var changed = false;
        if (ImGui.Checkbox($"##has{label}", ref has)) { set(has ? "#FFFFFFFF" : null); value = has ? "#FFFFFFFF" : null; changed = true; }
        ImGui.SameLine();
        if (value == null) { ImGui.TextDisabled(label); return changed; }
        return Color(label, value, v => set(v)) | changed;
    }

    private static bool Text(string label, string value, Action<string> set)
    {
        var v = value ?? "";
        ImGui.SetNextItemWidth(320);
        if (!ImGui.InputText(label, ref v, 256)) return false;
        set(v);
        return true;
    }

    private static bool Color(string label, string hex, Action<string> set)
    {
        var abgr = Colors.TryParse(hex ?? "", out var c) ? c : 0xFFFFFFFF;
        var v = new Vector4((abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f, (abgr >> 24) / 255f);
        if (!ImGui.ColorEdit4(label, ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar)) return false;
        uint B(float f) => (uint)Math.Clamp((int)MathF.Round(f * 255), 0, 255);
        set(Colors.Format(B(v.W) << 24 | B(v.Z) << 16 | B(v.Y) << 8 | B(v.X)));
        return true;
    }

    private static string? RegexError(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        if (RegexErrors.TryGetValue(pattern, out var err)) return err;
        try { _ = new Regex(pattern); err = null; }
        catch (ArgumentException ex) { err = ex.Message; }
        if (RegexErrors.Count > 200) RegexErrors.Clear();
        return RegexErrors[pattern] = err;
    }
}
