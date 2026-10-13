using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore2.Shared.Nodes;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>
/// The plugin's settings panel (DrawSettings): tabs General / Bars / Text / Status icons / States / Overrides / Ignore /
/// Debug over the [Menu] nodes (drawn here, read by reflection elsewhere) and the three lists the nodes can't hold.
/// Every bar kind has a live sample drawn by the engine itself (the same batched renderer, into the panel's draw list),
/// so what you tune is what you get. Layout and controls after syrairc's ExileNameplates settings (tabs, sub-tabs per
/// kind, the icon grid picker, "copy to all kinds, keep colors"); the list edits recompile the rules on the next Tick and
/// stay in step with the bridge (GetLayers / SetLayers).
/// Per-frame work stays allocation free: labels come from the attributes, live numbers and sample texts are formatted at
/// 4 Hz, search results only when the query changes.
/// </summary>
public static class SettingsPanel
{
    private static readonly string[] Tabs = { "General", "Bars", "Text", "Status icons", "States", "Overrides", "Ignore", "Debug" };
    private static readonly string[] KindTabs = { "Unique", "Rare", "Magic", "Normal", "Minions", "Players", "You" };
    private static readonly string[] PreviewFocus = { "Unique", "Rare", "Magic", "Normal", "Minion", "Player", "Self" };
    private static readonly string[] DebuffBuff = { "Debuff", "Buff" };
    private static readonly string[] MatchKinds = { "Path", "Name" };
    private static readonly string[] TokenChips = BuildTokenChips();

    private static int _kind, _textKind, _openStatus = -1, _openOverride = -1;
    /// <summary>Room a list card keeps on its right for the kind badge, the two arrows and the remove button.</summary>
    private const float TrailingW = 190f;
    private static string _search = "", _ignoreInput = "", _iconFilter = "";
    private static string? _searchDone;
    private static readonly List<BuffDefDto> _searchHits = new();
    private static int _searchTotal;
    private static readonly Dictionary<string, string?> RegexErrors = new();
    private static readonly SampleBars Samples = new();
    private static readonly LiveText Live = new();

    public static void Draw(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        Ui.Index(s);
        // The HUD skips a disabled plugin's Tick and Render, so the panel runs on ImGui's clock and prepares the renderer
        // itself: the samples animate and edits show whether the plugin is on or off.
        plugin.Renderer.Prepare(s, plugin.ResolutionScale);
        Ui.PushStyle();
        Header(plugin, s);
        var changed = false;
        if (ImGui.BeginTabBar("##whaTabs", ImGuiTabBarFlags.NoCloseWithMiddleMouseButton))
        {
            for (var i = 0; i < Tabs.Length; i++)
            {
                if (!ImGui.BeginTabItem(Tabs[i])) continue;
                ImGui.Dummy(new Vector2(0, 2));
                ImGui.BeginChild("##whaBody", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.None);
                lock (plugin.LayersLock)
                {
                    switch (i)
                    {
                        case 0: General(s); break;
                        case 1: Bars(plugin, s); break;
                        case 2: Text(plugin, s); break;
                        case 3: changed |= Icons(plugin, s); break;
                        case 4: States(plugin, s); break;
                        case 5: changed |= Overrides(plugin, s); break;
                        case 6: changed |= Ignored(s); break;
                        case 7: Debug(plugin, s); break;
                    }
                }
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        Ui.PopStyle();
        if (changed) plugin.LayersChanged();
    }

    // ---- Header: name, enable, live status, preview ----

    private static void Header(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        Ui.Toggle(s.Enable, "Whats A Health Bar");
        ImGui.SameLine(0, 14);
        Live.Refresh(plugin);
        var sup = s.Enable ? plugin.Suppressed : "disabled";
        if (sup == null) Ui.Badge(Live.DrawnBadge, Ui.Ok);
        else Ui.Badge(sup, Ui.Muted, outline: true);
        Ui.Tip(sup == null ? "Bars drawn last frame" : "Why no bars are drawn right now");
        var problems = plugin.Rules.Problems.Count;
        if (problems > 0)
        {
            ImGui.SameLine();
            Ui.Badge(Live.ProblemsBadge, Ui.Danger);
            Ui.Tip("Something in the lists didn't compile: see the Debug tab");
        }
        ImGui.SameLine(ImGui.GetContentRegionMax().X - 150);
        if (Ui.AccentButton("Preview in game", "Sample bars mid-screen for 8 seconds with fake hits, heals, names, icons and an immune sample, the kind whose tab is open first. Also from the control center", 150))
            plugin.RequestPreview(8, PreviewFocus[Math.Clamp(_kind, 0, 6)]);
        ImGui.Dummy(new Vector2(0, 2));
    }

    // ---- General ----

    private static void General(WhatsAHealthBarSettings s)
    {
        var v = s.Visibility;
        Ui.Section("Where bars show");
        Ui.Toggle(v.ShowInTown); Ui.Toggle(v.ShowInHideout); Ui.Toggle(v.HideWhenPanelsOpen); Ui.Toggle(v.HideUntargetable);
        Ui.Toggle(v.HideFullNormals);
        Ui.Slider(v.MaxDistance); Ui.Slider(v.MinionBelowPercent);
        Ui.Section("Placement");
        Ui.Toggle(v.AlignToGameBar); Ui.Slider(v.ZOffset);
        Ui.Toggle(s.Motion.Interpolate); Ui.Toggle(s.Motion.SubPixel);
        Ui.Slider(v.ClassifyHz);
        var a = s.Animation;
        Ui.Section("Animation");
        Ui.Toggle(a.Enabled);
        ImGui.BeginDisabled(!a.Enabled);
        Ui.Toggle(a.DamageTrail); Ui.Slider(a.TrailHoldMs); Ui.Slider(a.TrailDrain, "%.1f");
        Ui.Toggle(a.HitFlash); Ui.Toggle(a.HealGlow); Ui.Slider(a.FadeMs); Ui.Slider(a.LowLifePulse);
        ImGui.EndDisabled();
        Ui.Section("Bridge");
        Ui.Segmented(s.Bridge.PublishKills);
    }

    // ---- Bars: shared look, then one sub-tab per kind with its live sample ----

    private static void Bars(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        if (ImGui.BeginTabBar("##whaKinds"))
        {
            if (ImGui.BeginTabItem("All bars")) { Look(s); ImGui.EndTabItem(); }
            for (var k = 0; k < KindTabs.Length; k++)
            {
                if (!ImGui.BeginTabItem(KindTabs[k])) continue;
                _kind = k;
                Kind(plugin, s, (BarKind)k);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private static void Look(WhatsAHealthBarSettings s)
    {
        var l = s.Look;
        Ui.Note("The look every kind shares. Sizes are for 1440p and scale with the window; each kind sets its own width, height and life color in its tab.");
        Ui.Section("Size and shape");
        Ui.Slider(l.Scale, "%.2f"); Ui.Toggle(l.ScaleWithResolution); Ui.Slider(l.Rounding, "%.0f"); Ui.Slider(l.Outline);
        Ui.Section("Colors");
        Ui.Color(l.OutlineColor); Ui.Color(l.BackgroundColor); Ui.Color(l.EsColor); Ui.Color(l.WardColor);
        Ui.Color(l.TrailColor); Ui.Color(l.HealColor); Ui.Color(l.TextColor);
        Ui.Section("Pools");
        Ui.Segmented(l.Pools); Ui.Segmented(l.TotalLine); Ui.Toggle(l.PoolDividers);
        Ui.Section("Shine");
        Ui.Slider(l.Gloss, "%.2f"); Ui.Slider(l.Shading, "%.2f");
    }

    private static KindSettings Of(WhatsAHealthBarSettings s, BarKind k) => k switch
    {
        BarKind.Unique => s.Unique, BarKind.Rare => s.Rare, BarKind.Magic => s.Magic, BarKind.Normal => s.Normal,
        BarKind.Minion => s.Minion, BarKind.Player => s.Player, _ => s.Self,
    };

    private static void Kind(WhatsAHealthBar plugin, WhatsAHealthBarSettings s, BarKind kind)
    {
        var k = Of(s, kind);
        Samples.Draw(plugin, kind, ImGui.GetContentRegionAvail().X, 96);
        Ui.Tip("Live sample, drawn by the engine with these settings: fake hits and heals, the name and data texts, status icons, a damage-over-time chunk on uniques and rares");
        Ui.Toggle(k.Show);
        ImGui.BeginDisabled(!k.Show);
        Ui.Slider(k.Width); Ui.Slider(k.Height); Ui.Color(k.LifeColor); Ui.Slider(k.Segments); Ui.Toggle(k.ShowPercent);
        Ui.Section("Texts");
        Template(k.TextAbove, "Text above", Samples.Bar(plugin, kind), false);
        Template(k.TextOn, "Text on the bar", Samples.Bar(plugin, kind), false);
        Template(k.TextBelow, "Text below", Samples.Bar(plugin, kind), false);
        Ui.Note("Token chips and a live sample are in the Text tab.");
        ImGui.EndDisabled();
        ImGui.Dummy(new Vector2(0, 4));
        if (Ui.Button("Copy to all kinds, keep colors", "Gives every other kind this kind's width, height, segments, percent and texts; each keeps its own life color and Show"))
            Ui.Ask("##whaCopyAll");
        if (Ui.Confirm("##whaCopyAll", "Overwrite every kind's size, segments, percent and texts with this kind's? Life colors and Show stay as they are.", "Copy to all"))
            for (var i = 0; i < 7; i++)
            {
                var t = Of(s, (BarKind)i);
                if (ReferenceEquals(t, k)) continue;
                t.Width.Value = k.Width.Value; t.Height.Value = k.Height.Value; t.Segments.Value = k.Segments.Value;
                t.ShowPercent.Value = k.ShowPercent.Value;
                t.TextAbove.Value = k.TextAbove.Value; t.TextOn.Value = k.TextOn.Value; t.TextBelow.Value = k.TextBelow.Value;
            }
    }

    // ---- Text: the template editor with token chips and a live sample ----

    private static void Text(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        var t = s.Text;
        Ui.Section("Names and data texts");
        Ui.Slider(t.AbbreviateOver); Ui.Slider(t.Scale, "%.2f"); Ui.Toggle(t.Shadow); Ui.Slider(t.DpsWindow, "%.1f");
        Ui.Section("Templates");
        Ui.RowLabel("Kind", "Which kind's three text slots to edit");
        if (Ui.SegmentedButtons(KindTabs, KindTabs[_textKind], out var picked)) _textKind = Array.IndexOf(KindTabs, picked);
        var kind = (BarKind)_textKind;
        var k = Of(s, kind);
        Samples.Draw(plugin, kind, ImGui.GetContentRegionAvail().X, 96);
        var sample = Samples.Bar(plugin, kind);
        Template(k.TextAbove, "Text above", sample, true);
        Template(k.TextOn, "Text on the bar", sample, true);
        Template(k.TextBelow, "Text below", sample, true);
        Ui.Note("Tokens: {name} {hp} {hpmax} {hp%} (life + ES + ward) {life} {lifemax} {life%} {es} {esmax} {es%} {ward} {wardmax} {ward%} {dist} {rarity} {dps} {ttk} {dotloss} {dotdps}. Anything else is literal text; an unknown token shows as written.");
    }

    /// <summary>One text slot: the input, the token chips (click to append) and the string the sample bar shows right now.</summary>
    private static void Template(TextNode node, string label, Bar sample, bool chips)
    {
        ImGui.PushID(node.GetHashCode());
        Ui.RowLabel(label, Ui.TipOf(node));
        ImGui.SetNextItemWidth(-1);
        var v = node.Value ?? "";
        if (ImGui.InputTextWithHint("##t", "empty: nothing", ref v, 200)) node.Value = v;
        Ui.Tip(Ui.TipOf(node));
        if (chips)
        {
            ImGui.Dummy(new Vector2(Ui.LabelW - 8, 0));
            for (var i = 0; i < TokenChips.Length; i++)
                if (Ui.Chip(TokenChips[i], "Append this token", true)) node.Value = (node.Value ?? "") + TokenChips[i];
            ImGui.Dummy(new Vector2(Ui.LabelW - 8, 0)); ImGui.SameLine();
            Ui.Colored(Ui.Muted, "shows:"); ImGui.SameLine();
            Ui.Colored(Ui.Text, Live.Sample(node, sample));
        }
        ImGui.PopID();
    }

    private static string[] BuildTokenChips()
    {
        var a = new string[TextTemplate.TokenNames.Length];
        for (var i = 0; i < a.Length; i++) a[i] = "{" + TextTemplate.TokenNames[i] + "}";
        return a;
    }

    // ---- Status icons: the block settings, the buff search, and the configured list ----

    private static bool Icons(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        var ic = s.Icons;
        var changed = false;
        Ui.Section("Icon blocks");
        Ui.Toggle(ic.Enabled);
        ImGui.BeginDisabled(!ic.Enabled);
        Ui.Segmented(ic.Kinds); Ui.Segmented(ic.DebuffAnchor); Ui.Segmented(ic.BuffAnchor);
        Ui.Slider(ic.Size); Ui.Toggle(ic.Plate); Ui.Slider(ic.Columns); Ui.Slider(ic.MaxIcons); Ui.Toggle(ic.ShowStacks); Ui.Toggle(ic.ShowDuration);
        ImGui.EndDisabled();
        if (plugin.AtlasProblem is { } ap) Ui.Colored(Ui.Danger, ap);

        Ui.Section("Add a status from the game's buff list");
        ImGui.SetNextItemWidth(Ui.ControlW + 60);
        ImGui.InputTextWithHint("##whaBuffSearch", "search buff ids and names (2+ letters)", ref _search, 64);
        Ui.Tip("The game's own BuffDefinitions: the id is what Buff.Name reads on a monster. Add one, then pick its icon and tint below");
        var q = _search.Trim();
        if (q.Length >= 2)
        {
            if (q != _searchDone) Search(plugin, q);
            if (_searchHits.Count == 0) Ui.Note(_searchTotal < 0 ? "Files.BuffDefinitions is empty (not in game yet?)" : "nothing matches");
            ImGui.BeginChild("##whaHits", new Vector2(0, Math.Min(_searchHits.Count, 8) * (ImGui.GetFrameHeightWithSpacing()) + 4), ImGuiChildFlags.None);
            for (var i = 0; i < _searchHits.Count; i++)
            {
                var d = _searchHits[i];
                ImGui.PushID(i);
                // Against the list itself, not the compiled rules: those are rebuilt by Tick, which a disabled plugin doesn't get.
                var listed = false;
                for (var j = 0; j < s.Statuses.Count && !listed; j++) listed = s.Statuses[j].Id == d.Id;
                if (listed) { Ui.Badge("listed", Ui.Faint); }
                else if (ImGui.SmallButton("Add"))
                {
                    s.Statuses.Add(new StatusRule { Id = d.Id, Label = d.Name.Length > 0 ? d.Name : d.Id });
                    _openStatus = s.Statuses.Count - 1;
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(d.Id);
                if (d.Name.Length > 0) { ImGui.SameLine(); Ui.Colored(Ui.Muted, d.Name); }
                if (d.Invisible) { ImGui.SameLine(); Ui.Colored(Ui.Faint, "invisible"); }
                ImGui.PopID();
            }
            ImGui.EndChild();
            if (_searchTotal > _searchHits.Count) Ui.Note("more: refine the search");
        }

        Ui.Section("Statuses shown as icons");
        Ui.Note("Order is draw order. Click a row to edit it; the icon on the left is drawn as it will be on the bar.");
        var atlas = plugin.Renderer.AtlasId;
        for (var i = 0; i < s.Statuses.Count; i++)
        {
            var r = s.Statuses[i];
            ImGui.PushID(i);
            var open = _openStatus == i;
            Ui.BeginCard("##st", open);
            var en = r.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { r.Enabled = en; changed = true; }
            Ui.Tip("Shown as an icon when on");
            ImGui.SameLine();
            var icon = Math.Max(0, Array.IndexOf(StatusAtlas.Names, r.Icon));
            var tint = Colors.TryParse(r.Tint, out var tc) ? tc : 0xFFFFFFFF;
            Ui.IconImage(atlas, icon, r.Badge == Badges.Gem ? 0 : 1, en ? tint : Ui.Alpha(tint, 0.35f), 20);
            ImGui.SameLine();
            if (ImGui.Selectable(r.Label.Length > 0 ? r.Label : r.Id, open, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - TrailingW, 0)))
                _openStatus = open ? -1 : i;
            Ui.Tip(r.Id);
            ImGui.SameLine(ImGui.GetContentRegionMax().X - TrailingW);
            Ui.Badge(r.Debuff ? "debuff" : "buff", r.Debuff ? 0xFF4A4AC8 : 0xFF3C9A64);
            ImGui.SameLine();
            if (ImGui.ArrowButton("##up", ImGuiDir.Up) && i > 0) { (s.Statuses[i - 1], s.Statuses[i]) = (s.Statuses[i], s.Statuses[i - 1]); if (open) _openStatus = i - 1; changed = true; }
            ImGui.SameLine();
            if (ImGui.ArrowButton("##dn", ImGuiDir.Down) && i < s.Statuses.Count - 1) { (s.Statuses[i + 1], s.Statuses[i]) = (s.Statuses[i], s.Statuses[i + 1]); if (open) _openStatus = i + 1; changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) { s.Statuses.RemoveAt(i); if (_openStatus >= i) _openStatus = -1; changed = true; Ui.EndCard(); ImGui.PopID(); break; }
            Ui.Tip("Remove this status");
            if (open) changed |= StatusBody(r, atlas);
            Ui.EndCard();
            ImGui.PopID();
        }
        ImGui.Dummy(new Vector2(0, 2));
        if (Ui.Button("Restore the PoE2 defaults", "Replaces the whole list with the shipped PoE2 statuses (ailments, exposure, armour break, curses...)")) Ui.Ask("##whaDefaults");
        if (Ui.Confirm("##whaDefaults", "Replace the status list with the PoE2 defaults? Your own entries and edits are lost.", "Restore defaults"))
        {
            s.Statuses = StatusDefaults.Make();
            _openStatus = -1;
            changed = true;
        }
        return changed;
    }

    private static void Search(WhatsAHealthBar plugin, string q)
    {
        _searchDone = q;
        _searchHits.Clear();
        _searchTotal = 0;
        var catalog = plugin.BuffCatalog();
        if (catalog.Count == 0) { _searchTotal = -1; return; }
        foreach (var d in catalog)
        {
            if (!d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) && !d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _searchTotal++;
            if (_searchHits.Count < 30) _searchHits.Add(d);
        }
    }

    private static bool StatusBody(StatusRule r, IntPtr atlas)
    {
        var changed = false;
        ImGui.Separator();
        var id = r.Id;
        if (Field("Buff id", "The game's buff definition id, exactly as Buff.Name reads it", ref id)) { r.Id = id.Trim(); changed = true; }
        var label = r.Label;
        if (Field("Label", "Your name for it (the panel and the bridge)", ref label)) { r.Label = label; changed = true; }
        Ui.RowLabel("Kind", "Debuffs and buffs are separate icon blocks (their anchors are above)");
        if (Ui.SegmentedButtons(DebuffBuff, r.Debuff ? "Debuff" : "Buff", out var db)) { r.Debuff = db == "Debuff"; changed = true; }
        Ui.RowLabel("Icon", "One of the 96 atlas glyphs (syrairc's). Click to pick from the grid");
        var icon = Math.Max(0, Array.IndexOf(StatusAtlas.Names, r.Icon));
        var tint = Colors.TryParse(r.Tint, out var tc) ? tc : 0xFFFFFFFF;
        if (Ui.IconCell("##pick", atlas, icon, 1, tint, 26, false)) { _iconFilter = ""; ImGui.OpenPopup("##whaIconGrid"); }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Ui.Colored(Ui.Muted, StatusAtlas.Names[icon]);
        changed |= IconGrid(r, atlas, tint, icon);
        Ui.RowLabel("Tint", "The icon's color (the glyph is white in the atlas, so this is the color you see)");
        var hex = r.Tint;
        if (Ui.HexColor("##tint", ref hex)) { r.Tint = hex; changed = true; }
        Ui.RowLabel("Badge", "Bare: the glyph alone. Gem: baked on the dark gem badge. Over badge: the glyph over a badge of its own color");
        if (Ui.SegmentedButtons(Badges.All, r.Badge, out var badge)) { r.Badge = badge; changed = true; }
        if (r.Badge == Badges.OverBadge)
        {
            Ui.RowLabel("Badge tint", "Color of the badge under the glyph");
            var bh = r.BadgeTint;
            if (Ui.HexColor("##btint", ref bh)) { r.BadgeTint = bh; changed = true; }
        }
        Ui.RowLabel("Scale", "Multiplies the icon size for this status alone (a big curse, a small chill)");
        var scale = r.Scale;
        if (ImGui.SliderFloat("##scale", ref scale, 0.5f, 3f, "%.2f")) { r.Scale = scale; changed = true; }
        if (ImGui.TreeNodeEx("Bar while present", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            Ui.Note("Changes the whole bar while the monster has this status (an orange outline while cursed, hide while shielded...). Ticked fields apply; the rest stay as the kind draws them.");
            r.Patch ??= new StylePatch();
            changed |= Patch(r.Patch);
            ImGui.TreePop();
        }
        return changed;
    }

    /// <summary>The icon grid popup: search by glyph name, the 96 bare glyphs in the current tint, the picked one ringed.</summary>
    private static bool IconGrid(StatusRule r, IntPtr atlas, uint tint, int current)
    {
        if (!ImGui.BeginPopup("##whaIconGrid")) return false;
        var changed = false;
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##f", "search glyphs", ref _iconFilter, 32);
        ImGui.SameLine();
        Ui.Colored(Ui.Muted, "click to pick, Esc to close");
        const int cols = 12;
        const float cell = 34;
        ImGui.BeginChild("##grid", new Vector2(cols * (cell + 6) + 16, 300), ImGuiChildFlags.None);
        var shown = 0;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 2));
        for (var i = 0; i < StatusAtlas.Count; i++)
        {
            var name = StatusAtlas.Names[i];
            if (_iconFilter.Length > 0 && !name.Contains(_iconFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (shown++ % cols != 0) ImGui.SameLine();
            ImGui.PushID(i);
            if (Ui.IconCell("##c", atlas, i, 1, tint, cell, i == current)) { r.Icon = name; changed = true; ImGui.CloseCurrentPopup(); }
            ImGui.PopID();
            Ui.Tip(name);
        }
        ImGui.PopStyleVar();
        if (shown == 0) Ui.Note("no glyph matches");
        ImGui.EndChild();
        ImGui.EndPopup();
        return changed;
    }

    // ---- States ----

    private static void States(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        var st = s.States;
        Ui.Section("Invulnerable");
        Ui.Note("A monster that can't be damaged right now (the CannotBeDamaged stat: a boss's immune phase, a shielded add). Its bar goes grey with light stripes; the sample at the bottom shows it.");
        Ui.Segmented(st.Invulnerable);
        ImGui.BeginDisabled(st.Invulnerable.Value != StateModes.GreyHatch);
        Ui.Color(st.InvulnerableColor);
        ImGui.EndDisabled();
        Ui.Section("Cullable");
        Ui.Note("Culling strike kills at a life % by rarity. A bright notch marks the threshold; under it the fill and outline take the cull color.");
        Ui.Segmented(st.Cullable);
        ImGui.BeginDisabled(st.Cullable.Value == StateModes.Off);
        Ui.Slider(st.CullNormal); Ui.Slider(st.CullMagic); Ui.Slider(st.CullRare); Ui.Slider(st.CullUnique);
        Ui.Slider(st.CullMultiplier, "%.2f");
        ImGui.BeginDisabled(st.Cullable.Value != StateModes.NotchTint);
        Ui.Color(st.CullableColor);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        Ui.Section("Hidden monsters");
        Ui.Segmented(st.Hidden);
        Ui.Section("Damage over time");
        var d = s.Dot;
        Ui.Toggle(d.Enabled);
        ImGui.BeginDisabled(!d.Enabled);
        Ui.Color(d.Color); Ui.Slider(d.FallbackSeconds, "%.1f");
        ImGui.EndDisabled();
        Ui.Section("Samples");
        Samples.Draw(plugin, BarKind.Unique, ImGui.GetContentRegionAvail().X, 96);
        Ui.Tip("A unique with statuses and a running damage over time");
        Samples.DrawImmune(plugin, ImGui.GetContentRegionAvail().X, 80);
        Ui.Tip("The same unique in an immune phase");
    }

    // ---- Overrides ----

    private static bool Overrides(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        var changed = false;
        Ui.Note("A look for specific monsters: a regex on the metadata path (\"Monsters/.*Boss\") or the display name. The first enabled match wins, so order matters. The why overlay (Debug) shows each monster's path.");
        for (var i = 0; i < s.Overrides.Count; i++)
        {
            var o = s.Overrides[i];
            ImGui.PushID(i);
            var open = _openOverride == i;
            Ui.BeginCard("##ov", open);
            var en = o.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { o.Enabled = en; changed = true; }
            Ui.Tip("Matched when on");
            ImGui.SameLine();
            var err = RegexError(o.Pattern);
            var dl = ImGui.GetWindowDrawList();
            var dot = ImGui.GetCursorScreenPos() + new Vector2(5, ImGui.GetFrameHeight() * 0.5f);
            dl.AddCircleFilled(dot, 4, o.Pattern.Length == 0 ? Ui.Faint : err == null ? Ui.Ok : Ui.Danger, 12);
            ImGui.Dummy(new Vector2(12, ImGui.GetFrameHeight()));
            Ui.Tip(o.Pattern.Length == 0 ? "No pattern yet: matches nothing" : err ?? "Valid regex");
            ImGui.SameLine();
            if (ImGui.Selectable(o.Name.Length > 0 ? o.Name : "(unnamed)", open, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - TrailingW, 0)))
                _openOverride = open ? -1 : i;
            Ui.Tip(o.Pattern.Length > 0 ? o.Pattern : "Click to edit");
            ImGui.SameLine(ImGui.GetContentRegionMax().X - TrailingW);
            Ui.Badge(o.Match == "Name" ? "name" : "path", Ui.Faint);
            ImGui.SameLine();
            if (ImGui.ArrowButton("##up", ImGuiDir.Up) && i > 0) { (s.Overrides[i - 1], s.Overrides[i]) = (s.Overrides[i], s.Overrides[i - 1]); if (open) _openOverride = i - 1; changed = true; }
            ImGui.SameLine();
            if (ImGui.ArrowButton("##dn", ImGuiDir.Down) && i < s.Overrides.Count - 1) { (s.Overrides[i + 1], s.Overrides[i]) = (s.Overrides[i], s.Overrides[i + 1]); if (open) _openOverride = i + 1; changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) { s.Overrides.RemoveAt(i); if (_openOverride >= i) _openOverride = -1; changed = true; Ui.EndCard(); ImGui.PopID(); break; }
            Ui.Tip("Remove this override");
            if (open)
            {
                ImGui.Separator();
                var name = o.Name;
                if (Field("Name", "Your name for it", ref name)) { o.Name = name; changed = true; }
                Ui.RowLabel("Match", "Path: the metadata path (stable across languages and patches). Name: the display name (can be empty in PoE2)");
                if (Ui.SegmentedButtons(MatchKinds, o.Match, out var m)) { o.Match = m; changed = true; }
                var pattern = o.Pattern;
                if (Field("Pattern", "A .NET regex, case-insensitive, e.g. Monsters/Rhoas/ or ^Xesht", ref pattern)) { o.Pattern = pattern; changed = true; }
                if (err != null) Ui.Colored(Ui.Danger, err);
                o.Patch ??= new StylePatch();
                Ui.Section("Look while matched");
                changed |= Patch(o.Patch);
            }
            Ui.EndCard();
            ImGui.PopID();
        }
        if (Ui.AccentButton("Add override", "A new, empty override at the end of the list"))
        {
            s.Overrides.Add(new OverrideRule());
            _openOverride = s.Overrides.Count - 1;
            changed = true;
        }
        return changed;
    }

    /// <summary>"Invalid regex: why" for a pattern that doesn't compile, null for one that does (cached per pattern text).</summary>
    private static string? RegexError(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        if (RegexErrors.TryGetValue(pattern, out var err)) return err;
        try { _ = new Regex(pattern); err = null; }
        catch (ArgumentException ex) { err = "Invalid regex: " + ex.Message; }
        if (RegexErrors.Count > 200) RegexErrors.Clear();
        return RegexErrors[pattern] = err;
    }

    // ---- Ignore ----

    private static bool Ignored(WhatsAHealthBarSettings s)
    {
        var changed = false;
        Ui.Note("Metadata paths (without @level) that never get a bar. The why overlay (Debug tab) adds one with a click on the monster.");
        Ui.Section("Ignored paths");
        if (s.IgnoredPaths.Count == 0) Ui.Colored(Ui.Faint, "none");
        for (var i = 0; i < s.IgnoredPaths.Count; i++)
        {
            ImGui.PushID(i);
            if (Ui.Chip(s.IgnoredPaths[i], "Click to stop ignoring it", i > 0)) { s.IgnoredPaths.RemoveAt(i); changed = true; ImGui.PopID(); break; }
            ImGui.PopID();
        }
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.SetNextItemWidth(Ui.ControlW + 160);
        var enter = ImGui.InputTextWithHint("##whaIgnoreAdd", "Metadata/Monsters/...", ref _ignoreInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((Ui.Button("Ignore", "Adds the path to the list") || enter) && _ignoreInput.Trim().Length > 0)
        {
            var key = CompiledLayers.PathKey(_ignoreInput.Trim());
            if (!s.IgnoredPaths.Contains(key, StringComparer.OrdinalIgnoreCase)) s.IgnoredPaths.Add(key);
            _ignoreInput = "";
            changed = true;
        }
        return changed;
    }

    // ---- Debug ----

    private static void Debug(WhatsAHealthBar plugin, WhatsAHealthBarSettings s)
    {
        Ui.Section("Why overlay");
        Ui.Toggle(s.Debug.WhyOverlay); Ui.Slider(s.Debug.WhyRange);
        Ui.Section("Live");
        Live.Refresh(plugin);
        ImGui.PushStyleColor(ImGuiCol.Text, Ui.Muted);
        for (var i = 0; i < Live.Lines.Length; i++)
        {
            ImGui.TextUnformatted(Live.Labels[i]);
            ImGui.SameLine(Ui.LabelW);
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Text);
            ImGui.TextUnformatted(Live.Lines[i] ?? "");
            ImGui.PopStyleColor();
        }
        ImGui.PopStyleColor();
        if (plugin.AtlasProblem is { } ap) Ui.Colored(Ui.Danger, ap);
        if (Live.PoolProblem is { } pp) Ui.Colored(Ui.Danger, pp);
        var problems = plugin.Rules.Problems;
        if (problems.Count > 0)
        {
            Ui.Section("Didn't compile");
            foreach (var p in problems) Ui.Colored(Ui.Danger, p);
        }
    }

    // ---- Shared pieces ----

    /// <summary>A text field row; by ref rather than a setter lambda, so nothing is allocated per frame.</summary>
    private static bool Field(string label, string tip, ref string value)
    {
        Ui.RowLabel(label, tip);
        ImGui.PushID(label);
        ImGui.SetNextItemWidth(Ui.ControlW + 100);
        var v = value ?? "";
        var changed = ImGui.InputText("##f", ref v, 256);
        ImGui.PopID();
        Ui.Tip(tip);
        if (changed) value = v;
        return changed;
    }

    /// <summary>The optional fields of a patch: a checkbox sets each one; unset fields leave the kind's look alone.</summary>
    private static bool Patch(StylePatch p)
    {
        var changed = false;
        var hide = p.Hide; if (OptBool("Hide the bar", "No bar at all while this applies", ref hide)) { p.Hide = hide; changed = true; }
        var c = p.LifeColor; if (OptColor("Life color", ref c)) { p.LifeColor = c; changed = true; }
        c = p.EsColor; if (OptColor("ES color", ref c)) { p.EsColor = c; changed = true; }
        c = p.WardColor; if (OptColor("Ward color", ref c)) { p.WardColor = c; changed = true; }
        c = p.BackgroundColor; if (OptColor("Background", ref c)) { p.BackgroundColor = c; changed = true; }
        c = p.OutlineColor; if (OptColor("Outline", ref c)) { p.OutlineColor = c; changed = true; }
        c = p.TextColor; if (OptColor("Text color", ref c)) { p.TextColor = c; changed = true; }
        var hasOp = p.Opacity.HasValue;
        if (ImGui.Checkbox("Opacity##o", ref hasOp)) { p.Opacity = hasOp ? 1f : null; changed = true; }
        Ui.Tip("Multiplies the whole bar's opacity");
        if (p.Opacity.HasValue)
        {
            ImGui.SameLine(Ui.LabelW);
            var op = p.Opacity.Value;
            ImGui.SetNextItemWidth(Ui.ControlW);
            if (ImGui.SliderFloat("##opv", ref op, 0f, 1f, "%.2f")) { p.Opacity = op; changed = true; }
        }
        var grey = p.Grey; if (OptBool("Grey", "Desaturates and darkens the fills", ref grey)) { p.Grey = grey; changed = true; }
        var hatch = p.Hatch; if (OptBool("Hatch", "Light diagonal stripes across the bar (the invulnerable look)", ref hatch)) { p.Hatch = hatch; changed = true; }
        return changed;
    }

    private static bool OptBool(string label, string tip, ref bool? value)
    {
        var on = value == true;
        var changed = ImGui.Checkbox(label, ref on);
        Ui.Tip(tip);
        if (changed) value = on ? true : null;
        return changed;
    }

    private static bool OptColor(string label, ref string? value)
    {
        var has = value != null;
        var changed = false;
        ImGui.PushID(label);
        if (ImGui.Checkbox(label, ref has)) { value = has ? "#FFFFFFFF" : null; changed = true; }
        Ui.Tip("Tick to set this color while the rule applies");
        if (value != null)
        {
            ImGui.SameLine(Ui.LabelW);
            var hex = value;
            if (Ui.HexColor("##c", ref hex)) { value = hex; changed = true; }
        }
        ImGui.PopID();
        return changed;
    }

    /// <summary>
    /// Strings that change with live data, rebuilt at 4 Hz (never per frame): the header badges, the Debug lines, the
    /// template samples.
    /// </summary>
    private sealed class LiveText
    {
        public string DrawnBadge = "0 bars", ProblemsBadge = "";
        public readonly string[] Labels =
        {
            "Tracked / wanted / drawn", "Peak drawn", "With statuses / invulnerable / DoT", "Tick (avg / max)", "Render (avg / max)",
            "Classify pass", "Render split (build / flush)", "Quads / icons", "Render path", "Motion lag / jitter", "Rules version",
        };
        public readonly string?[] Lines = new string?[11];
        public string? PoolProblem;
        private double _at = -1;
        private int _problems = -1, _drawn = -1;
        private PanelNumbers _n;
        private readonly Dictionary<TextNode, (string src, TextTemplate template, string text, double at)> _samples = new(ReferenceEqualityComparer.Instance);

        public void Refresh(WhatsAHealthBar plugin)
        {
            var now = ImGui.GetTime();
            var problems = plugin.Rules.Problems.Count;
            if (problems != _problems) { _problems = problems; ProblemsBadge = problems == 1 ? "1 problem" : I(problems) + " problems"; }
            if (now - _at < 0.25 && _at >= 0) return;
            _at = now;
            plugin.Snapshot(ref _n);
            if (_n.Drawn != _drawn) { _drawn = _n.Drawn; DrawnBadge = _drawn == 1 ? "1 bar" : I(_drawn) + " bars"; }
            Lines[0] = I(_n.Tracked) + " / " + I(_n.Wanted) + " / " + I(_n.Drawn);
            Lines[1] = I(_n.PeakDrawn);
            Lines[2] = I(_n.WithStatuses) + " / " + I(_n.Invulnerable) + " / " + I(_n.WithDot);
            Lines[3] = F(_n.TickUs) + " / " + F(_n.TickMaxUs) + " us";
            Lines[4] = F(_n.RenderUs) + " / " + F(_n.RenderMaxUs) + " us";
            Lines[5] = F(_n.ClassifyUs) + " us";
            Lines[6] = F(_n.BuildUs) + " / " + F(_n.FlushUs) + " us";
            Lines[7] = I(_n.Quads) + " / " + I(_n.Icons) + (_n.Previewing ? "  (previewing)" : "");
            Lines[8] = _n.RenderPath;
            Lines[9] = F(_n.LagMs) + " ms / " + F(_n.JitterShownPx) + " px";
            Lines[10] = I(_n.LayersVersion);
            PoolProblem = _n.PoolProblem;
        }

        /// <summary>What a template shows on the sample bar right now (rebuilt when the text or the sample's values change, at most 4 Hz).</summary>
        public string Sample(TextNode node, Bar sample)
        {
            var src = node.Value ?? "";
            var same = _samples.TryGetValue(node, out var c) && ReferenceEquals(c.src, src);
            if (same && _at - c.at < 0.5) return c.text;
            var template = same ? c.template : TextTemplate.Parse(src);
            var text = template.IsEmpty ? "(nothing)" : template.Build(sample);
            if (text.Length == 0) text = "(nothing)";
            _samples[node] = (src, template, text, _at);
            return text;
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One animated sample bar per kind (plus an immune unique), driven by the preview's pool simulation and drawn by the
    /// engine itself into the panel's draw list: the batched quads, the icon atlas, the texts, exactly as in game.
    /// </summary>
    private sealed class SampleBars
    {
        private readonly PoolSim?[] _sims = new PoolSim?[8];
        private readonly double[] _stepped = new double[8], _looked = new double[8];

        public Bar Bar(WhatsAHealthBar plugin, BarKind kind) => Sim(plugin, (int)kind, kind).Bar;

        private PoolSim Sim(WhatsAHealthBar plugin, int slot, BarKind kind)
        {
            var sim = _sims[slot];
            if (sim != null) return sim;
            sim = plugin.MakeSim(kind);
            if (slot == 7) { sim.Bar.Invulnerable = true; sim.Bar.DisplayName = "Immune phase"; }
            sim.Bar.Dist = 30;
            return _sims[slot] = sim;
        }

        public void Draw(WhatsAHealthBar plugin, BarKind kind, float width, float height) => Draw(plugin, (int)kind, kind, width, height);

        public void DrawImmune(WhatsAHealthBar plugin, float width, float height) => Draw(plugin, 7, BarKind.Unique, width, height);

        private void Draw(WhatsAHealthBar plugin, int slot, BarKind kind, float width, float height)
        {
            var sim = Sim(plugin, slot, kind);
            var b = sim.Bar;
            // ImGui's clock, not the plugin's: a disabled plugin gets no Tick, and the panel must still show its samples.
            var now = ImGui.GetTime();
            if (_stepped[slot] != now)
            {
                // Once per HUD frame: the simulated pools, the look (statuses, DoT, cull threshold) at 10 Hz, the animation.
                var dt = _stepped[slot] > 0 ? (float)Math.Clamp(now - _stepped[slot], 0, 0.1) : 0f;
                _stepped[slot] = now;
                sim.Step(now, dt, plugin.Rng);
                if (now - _looked[slot] >= 0.1) { _looked[slot] = now; plugin.PreviewLook(b, now); }
                b.UpdateDps(now, plugin.Settings.Text.DpsWindow.Value);
                WhatsAHealthBar.DotLoss(b, now);
                b.Animate(now, dt, plugin.AnimNow());
            }
            // The stage: a dark well with a faint ground line, the bar's bottom centre two thirds down (room for the name and
            // icons above, texts and the total line below).
            var org = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(width, height));
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(org, org + new Vector2(width, height), Ui.Well, 4f);
            dl.AddRectFilledMultiColor(org, org + new Vector2(width, height * 0.5f), 0x18FFFFFF, 0x18FFFFFF, 0x00FFFFFF, 0x00FFFFFF);
            dl.AddRect(org, org + new Vector2(width, height), Ui.CardBorder, 4f);
            var label = BarRenderer.KindNames[(int)kind];
            dl.AddText(org + new Vector2(8, 5), Ui.Faint, slot == 7 ? "Immune" : label);
            b.Screen = new Vector2(MathF.Round(org.X + width * 0.5f), MathF.Round(org.Y + height * 0.66f));
            b.RestX = float.NaN; // re-snap to the panel's position (the panel can move between frames)
            var r = plugin.Renderer;
            r.Begin();
            r.Draw(b, now);
            r.Flush(dl, record: false);
        }
    }
}
