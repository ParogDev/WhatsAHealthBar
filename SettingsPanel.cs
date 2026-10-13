using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore2.Shared.Nodes;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>
/// The plugin's settings panel (DrawSettings): the [Menu]-driven SettingsMenu renderer (SettingsMenu.cs, the scaffolding's
/// template) draws every node as a row and the three lists with its list editor (add, remove, reorder, fold, reset); this
/// class wires in what the renderer can't know about. The live status strip, one live sample bar per kind drawn by the
/// engine itself into the panel (the same batched renderer, so what you tune is what you get), "copy to all kinds, keep
/// colors", the template editor with token chips and the string the sample shows, the buff search that adds statuses, the
/// icon grid picker, the regex validity dot, the immune sample and the live Debug numbers. Layout ideas after syrairc's
/// ExileNameplates settings (ported, not linked). The list edits recompile the rules once the mouse lets go and stay in
/// step with the bridge (GetLayers / SetLayers).
/// Per-frame work stays allocation free: live numbers and sample texts are formatted at 4 Hz, search results only when the
/// query changes.
/// </summary>
internal sealed class SettingsPanel
{
    /// <summary>Labels per BarKind, in enum order (the enum's own names are what the preview's focus takes).</summary>
    private static readonly string[] KindTabs = { "Unique", "Rare", "Magic", "Normal", "Minions", "Players", "You" };
    private static readonly string[] PreviewFocus = Enum.GetNames<BarKind>();
    private static readonly string[] TokenChips = BuildTokenChips();
    private static readonly Vector4 Accent = Ui.V(Ui.Accent), Ok = Ui.V(Ui.Ok), Danger = Ui.V(Ui.Danger), Muted = Ui.V(Ui.Muted);
    private const string SampleTip = "Live sample, drawn by the engine with these settings: fake hits and heals, the name and data texts, status icons, a damage-over-time chunk on uniques and rares";

    private readonly WhatsAHealthBar _plugin;
    private readonly SettingsMenu _menu;
    private readonly SampleBars _samples = new();
    private readonly LiveText _live = new();
    /// <summary>The panel's own batch renderer for the sample bars (the plugin's draws the overlay and keeps its counters).</summary>
    private readonly BarRenderer _renderer = new();
    private readonly Dictionary<string, string?> _regexErrors = new();
    private readonly List<BuffDefDto> _searchHits = new();
    private int _textKind, _lastSampleKind, _searchTotal, _seenEdits;
    private string _search = "", _searchRaw = "", _searchTrim = "", _iconFilter = "";
    private string? _searchDone;
    private double _searchRetryAt;

    /// <summary>An edit not compiled yet (a widget was still active) and when the panel last drew: Tick compiles it when the menu closed on it.</summary>
    public bool Pending;
    public double DrawnAt = double.NegativeInfinity;

    public SettingsPanel(WhatsAHealthBar plugin)
    {
        _plugin = plugin;
        _menu = new SettingsMenu { Accent = Accent, RootTabName = "General" }
            .Banner(DrawBanner)
            .Custom("Preview", DrawPreviewRow)
            .Tab("Text", DrawTemplates)
            .Tab("States", DrawStateSamples)
            .Tab("Debug", DrawDebug)
            .ListExtras("Statuses", above: DrawBuffSearch, below: DrawRestoreDefaults)
            .ItemLead("Statuses", DrawStatusLead)
            .CustomField("Statuses.Icon", DrawIconField)
            .ItemLead("Overrides", DrawRegexDot)
            .CustomField("Overrides.Pattern", DrawPatternField);
        // the settings property names are the BarKind names: Unique, Rare, Magic, Normal, Minion, Player, Self
        for (var k = 0; k < PreviewFocus.Length; k++)
        {
            var kind = (BarKind)k;
            _menu.Custom(PreviewFocus[k] + ".Sample", () => DrawSample(kind));
            _menu.Custom(PreviewFocus[k] + ".CopyToAll", () => DrawCopyToAll(kind));
        }
    }

    public void Draw(WhatsAHealthBarSettings s)
    {
        // The HUD skips a disabled plugin's Tick and Render, so the panel runs on ImGui's clock and has a renderer of its
        // own (same rules and atlas, prepared from the same settings): the samples animate and edits show whether the
        // plugin is on or off, and the overlay's counters and caches are never touched.
        _renderer.Rules = _plugin.Rules;
        _renderer.AtlasId = _plugin.Renderer.AtlasId;
        _renderer.SubPixel = false;
        _renderer.Prepare(s, _plugin.ResolutionScale);
        _live.Refresh(_plugin);
        lock (_plugin.LayersLock) _menu.Draw(s);
        // Compiled here (not on the next Tick): the samples, the problems badge and the bridge must see the edit even
        // while the plugin is disabled and gets no Tick; but once the mouse lets go, not on every frame of a slider or
        // color drag (a compile renumbers the rules and makes every bar re-read its stats). The samples re-resolve their
        // statuses against the new rules on the next frame.
        if (_menu.ListEdits != _seenEdits) { _seenEdits = _menu.ListEdits; Pending = true; }
        DrawnAt = ImGui.GetTime();
        if (Pending && !ImGui.IsAnyItemActive()) { Pending = false; _plugin.CompileLayers(); _samples.Invalidate(); }
    }

    // ---- Banner: live status; the preview button is the Preview row on General ----

    private void DrawBanner()
    {
        var s = _plugin.Settings;
        var sup = s.Enable ? _plugin.Suppressed : "disabled";
        if (sup == null) _menu.Badge(_live.DrawnBadge, Ok, "Bars drawn last frame");
        else _menu.Badge(sup, Muted, "Why no bars are drawn right now");
        if (_plugin.Rules.Problems.Count > 0)
        {
            ImGui.SameLine();
            _menu.Badge(_live.ProblemsBadge, Danger, "Something in the lists didn't compile: see the Debug tab");
        }
        ImGui.Spacing();
    }

    private void DrawPreviewRow()
    {
        SettingsMenu.Label("Preview in game", "Sample bars mid-screen for 8 seconds with fake hits, heals, names, icons and an immune sample; the kind whose sample you looked at last comes first. Also from the control center");
        ImGui.SameLine(_menu.ControlColumn);
        // Only while enabled: the request is drained by Tick, which a disabled plugin doesn't get (it would fire later).
        var on = _plugin.Settings.Enable.Value;
        ImGui.BeginDisabled(!on);
        if (_menu.AccentButton("Preview in game##pv")) _plugin.RequestPreview(8, PreviewFocus[Math.Clamp(_lastSampleKind, 0, PreviewFocus.Length - 1)]);
        ImGui.EndDisabled();
        if (!on && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) SettingsMenu.Tooltip("Enable the plugin first: a disabled plugin gets no Tick to run the preview on");
    }

    // ---- Bars: the live sample at the top of each kind's card, the copy button at its bottom ----

    private void DrawSample(BarKind kind)
    {
        _lastSampleKind = (int)kind;
        _samples.Draw(_plugin, _renderer, kind, ImGui.GetContentRegionAvail().X, 96, SampleTip);
    }

    private void DrawCopyToAll(BarKind kind)
    {
        var s = _plugin.Settings;
        var k = s.For(kind);
        if (_menu.AccentButton("Copy to all kinds, keep colors")) Ui.Ask("##whaCopyAll");
        if (ImGui.IsItemHovered()) SettingsMenu.Tooltip("Gives every other kind this kind's width, height, segments, percent and texts; each keeps its own life color and Show");
        if (Ui.Confirm("##whaCopyAll", "Overwrite every kind's size, segments, percent and texts with this kind's? Life colors and Show stay as they are.", "Copy to all"))
            for (var i = 0; i < PreviewFocus.Length; i++)
            {
                var t = s.For((BarKind)i);
                if (ReferenceEquals(t, k)) continue;
                t.Width.Value = k.Width.Value; t.Height.Value = k.Height.Value; t.Segments.Value = k.Segments.Value;
                t.ShowPercent.Value = k.ShowPercent.Value;
                t.TextAbove.Value = k.TextAbove.Value; t.TextOn.Value = k.TextOn.Value; t.TextBelow.Value = k.TextBelow.Value;
            }
    }

    // ---- Text: the template editor with token chips and a live sample, under the Text card ----

    private void DrawTemplates()
    {
        var s = _plugin.Settings;
        _menu.SectionHeader("Templates", "The three text slots of one kind: click a token chip to append it; the line under each shows what the sample bar reads right now");
        SettingsMenu.Label("Kind", "Which kind's three text slots to edit");
        ImGui.SameLine(_menu.ControlColumn);
        _menu.Segmented("##kind", KindTabs, ref _textKind);
        var kind = (BarKind)_textKind;
        var k = s.For(kind);
        _samples.Draw(_plugin, _renderer, kind, ImGui.GetContentRegionAvail().X, 96, SampleTip);
        var sample = _samples.Bar(_plugin, kind);
        Template(k.TextAbove, "Text above", sample);
        Template(k.TextOn, "Text on the bar", sample);
        Template(k.TextBelow, "Text below", sample);
        ImGui.PushTextWrapPos(0f);
        SettingsMenu.Dim("Tokens: {name} {hp} {hpmax} {hp%} (life + ES + ward) {life} {lifemax} {life%} {es} {esmax} {es%} {ward} {wardmax} {ward%} {dist} {rarity} {dps} {ttk} {dotloss} {dotdps}. Anything else is literal text; an unknown token shows as written.");
        ImGui.PopTextWrapPos();
    }

    /// <summary>One text slot: the input, the token chips (click to append) and the string the sample bar shows right now.</summary>
    private void Template(TextNode node, string label, Bar sample)
    {
        ImGui.PushID(label);
        SettingsMenu.Label(label, "Template: tokens in braces, anything else literal; empty: nothing");
        ImGui.SameLine(_menu.ControlColumn);
        ImGui.SetNextItemWidth(-1);
        var v = node.Value ?? "";
        if (ImGui.InputTextWithHint("##t", "empty: nothing", ref v, 200)) node.Value = v;
        ImGui.Dummy(new Vector2(_menu.ControlColumn - 8, 0));
        for (var i = 0; i < TokenChips.Length; i++)
            if (_menu.Chip(TokenChips[i], "Append this token")) node.Value = (node.Value ?? "") + TokenChips[i];
        ImGui.Dummy(new Vector2(_menu.ControlColumn - 8, 0));
        ImGui.SameLine();
        SettingsMenu.Dim("shows:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_live.Sample(node, sample));
        ImGui.PopID();
    }

    private static string[] BuildTokenChips()
    {
        var a = new string[TextTemplate.TokenNames.Length];
        for (var i = 0; i < a.Length; i++) a[i] = "{" + TextTemplate.TokenNames[i] + "}";
        return a;
    }

    // ---- Status icons: the buff search above the list, the restore button below it, the icon in each line ----

    private void DrawBuffSearch()
    {
        var s = _plugin.Settings;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##whaBuffSearch", "add from the game's buff list: search ids and names (2+ letters)", ref _search, 64);
        if (ImGui.IsItemHovered()) SettingsMenu.Tooltip("The game's own BuffDefinitions: the id is what Buff.Name reads on a monster. Add one, then pick its icon and tint in its line below");
        if (!ReferenceEquals(_search, _searchRaw)) { _searchRaw = _search; _searchTrim = _search.Trim(); }   // trimmed once per edit, not per frame
        var q = _searchTrim;
        if (q.Length >= 2)
        {
            // Again when the text changes; an empty catalog (the read reloads the file) is tried again in game, every 2 s.
            if (q != _searchDone || (_searchTotal < 0 && _plugin.InGame && ImGui.GetTime() >= _searchRetryAt)) Search(q);
            if (_searchHits.Count == 0) SettingsMenu.Dim(_searchTotal < 0 ? "Files.BuffDefinitions is empty: enter the game, then search again" : "nothing matches");
            ImGui.BeginChild("##whaHits", new Vector2(0, Math.Min(_searchHits.Count, 8) * ImGui.GetFrameHeightWithSpacing() + 4), ImGuiChildFlags.None);
            for (var i = 0; i < _searchHits.Count; i++)
            {
                var d = _searchHits[i];
                ImGui.PushID(i);
                // Against the list itself: a disabled entry is listed too, and the compiled rules skip those.
                var listed = false;
                for (var j = 0; j < s.Statuses.Count && !listed; j++) listed = s.Statuses[j].Id == d.Id;
                if (listed) _menu.Badge("listed", Muted);
                else if (ImGui.SmallButton("Add"))
                {
                    s.Statuses.Add(new StatusRule { Id = d.Id, Label = d.Name.Length > 0 ? d.Name : d.Id });
                    _menu.OpenItem("Statuses", s.Statuses.Count - 1);
                    _menu.ListEdited();
                }
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(d.Id);
                if (d.Name.Length > 0) { ImGui.SameLine(); SettingsMenu.Dim(d.Name); }
                if (d.Invisible) { ImGui.SameLine(); SettingsMenu.Text(Ui.V(Ui.Faint), "invisible"); }
                ImGui.PopID();
            }
            ImGui.EndChild();
            if (_searchTotal > _searchHits.Count) SettingsMenu.Dim("more: refine the search");
        }
        ImGui.Spacing();
    }

    private void Search(string q)
    {
        _searchDone = q;
        _searchHits.Clear();
        _searchTotal = 0;
        var catalog = _plugin.BuffCatalog();
        // An empty catalog (not in game yet) isn't a result to keep: the search runs again, 2 s later, while in game.
        if (catalog.Count == 0) { _searchTotal = -1; _searchRetryAt = ImGui.GetTime() + 2; return; }
        foreach (var d in catalog)
        {
            if (!d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) && !d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _searchTotal++;
            if (_searchHits.Count < 30) _searchHits.Add(d);
        }
    }

    private void DrawRestoreDefaults()
    {
        var s = _plugin.Settings;
        ImGui.SameLine();   // next to the add button
        if (_menu.AccentButton("Restore the PoE2 defaults")) Ui.Ask("##whaDefaults");
        if (ImGui.IsItemHovered()) SettingsMenu.Tooltip("Replaces the whole list with the shipped PoE2 statuses (ailments, exposure, armour break, curses...)");
        if (Ui.Confirm("##whaDefaults", "Replace the status list with the PoE2 defaults? Your own entries and edits are lost.", "Restore defaults"))
        {
            s.Statuses = StatusDefaults.Make();
            _menu.OpenItem("Statuses", -1);
            _menu.ListEdited();
        }
    }

    /// <summary>The icon at the start of a status line, drawn as it will be on the bar (the compiled rule's tint when it compiled).</summary>
    private void DrawStatusLead(object o)
    {
        var r = (StatusRule)o;
        var L = _plugin.Rules;
        // The compiled rule (icon index, parsed tints) when it compiled: the rules are rebuilt on every edit here. A
        // disabled or duplicate status has none and is parsed on the spot.
        int icon; uint tint, badgeTint;
        if (L.StatusById.TryGetValue(r.Id, out var rule) && ReferenceEquals(L.Statuses[rule], r))
        { icon = L.StatusIcons[rule]; tint = L.StatusTints[rule]; badgeTint = L.StatusBadgeTints[rule]; }
        else
        {
            icon = Math.Max(0, Array.IndexOf(StatusAtlas.Names, r.Icon));
            tint = Colors.TryParse(r.Tint, out var tc) ? tc : 0xFFFFFFFF;
            badgeTint = Colors.TryParse(r.BadgeTint, out var bt) ? bt : 0xFF30262A;
        }
        var en = r.Enabled;
        Ui.IconImage(_plugin.Renderer.AtlasId, icon, r.Badge, en ? tint : Ui.Alpha(tint, 0.35f), en ? badgeTint : Ui.Alpha(badgeTint, 0.35f), 20, _plugin.Settings.Icons.Plate);
    }

    /// <summary>The Icon field: the current glyph as a button that opens the grid picker, its name beside it.</summary>
    private void DrawIconField(object o)
    {
        var r = (StatusRule)o;
        var atlas = _plugin.Renderer.AtlasId;
        var icon = Math.Max(0, Array.IndexOf(StatusAtlas.Names, r.Icon));
        var tint = Colors.TryParse(r.Tint, out var tc) ? tc : 0xFFFFFFFF;
        if (Ui.IconCell("##pick", atlas, icon, 1, tint, 26, false)) { _iconFilter = ""; ImGui.OpenPopup("##whaIconGrid"); }
        if (ImGui.IsItemHovered()) SettingsMenu.Tooltip("Click to pick another glyph from the grid");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        SettingsMenu.Dim(StatusAtlas.Names[icon]);
        if (IconGrid(r, atlas, tint, icon)) _menu.ListEdited();
    }

    /// <summary>The icon grid popup: search by glyph name, the 96 bare glyphs in the current tint, the picked one ringed.</summary>
    private bool IconGrid(StatusRule r, IntPtr atlas, uint tint, int current)
    {
        if (!ImGui.BeginPopup("##whaIconGrid")) return false;
        var changed = false;
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##f", "search glyphs", ref _iconFilter, 32);
        ImGui.SameLine();
        SettingsMenu.Dim("click to pick, Esc to close");
        const int cols = 12;
        const float cell = 34;
        // A cell button is the image plus the frame padding on both sides, then the 2 px spacing pushed below.
        var step = cell + 2 * ImGui.GetStyle().FramePadding.X + 2;
        ImGui.BeginChild("##grid", new Vector2(cols * step + 16, 300), ImGuiChildFlags.None);
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
            if (ImGui.IsItemHovered()) SettingsMenu.Tooltip(name);
        }
        ImGui.PopStyleVar();
        if (shown == 0) SettingsMenu.Dim("no glyph matches");
        ImGui.EndChild();
        ImGui.EndPopup();
        return changed;
    }

    // ---- Overrides: the validity dot at the start of each line, the pattern field with its error ----

    private void DrawRegexDot(object o)
    {
        var ov = (OverrideRule)o;
        var err = RegexError(ov.Pattern);
        var dl = ImGui.GetWindowDrawList();
        var dot = ImGui.GetCursorScreenPos() + new Vector2(5, ImGui.GetFrameHeight() * 0.5f);
        dl.AddCircleFilled(dot, 4, ov.Pattern.Length == 0 ? Ui.Faint : err == null ? Ui.Ok : Ui.Danger, 12);
        ImGui.Dummy(new Vector2(12, ImGui.GetFrameHeight()));
        if (ImGui.IsItemHovered()) SettingsMenu.Tooltip(ov.Pattern.Length == 0 ? "No pattern yet: matches nothing" : err ?? "Valid regex");
    }

    private void DrawPatternField(object o)
    {
        var ov = (OverrideRule)o;
        var v = ov.Pattern;
        if (ImGui.InputText("##pat", ref v, 256)) { ov.Pattern = v; _menu.ListEdited(); }
        var err = RegexError(ov.Pattern);
        if (err != null) SettingsMenu.Text(Danger, err);
    }

    /// <summary>"Invalid regex: why" for a pattern that doesn't compile, null for one that does (cached per pattern text).</summary>
    private string? RegexError(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        if (_regexErrors.TryGetValue(pattern, out var err)) return err;
        try { _ = new Regex(pattern); err = null; }
        catch (ArgumentException ex) { err = "Invalid regex: " + ex.Message; }
        if (_regexErrors.Count > 200) _regexErrors.Clear();
        return _regexErrors[pattern] = err;
    }

    // ---- States: the samples under the cards ----

    private void DrawStateSamples()
    {
        _menu.SectionHeader("Samples");
        _samples.Draw(_plugin, _renderer, BarKind.Unique, ImGui.GetContentRegionAvail().X, 96, "A unique with statuses and a running damage over time");
        _samples.DrawImmune(_plugin, _renderer, ImGui.GetContentRegionAvail().X, 80, "The same unique in an immune phase");
    }

    // ---- Debug: the live numbers under the Debug card ----

    private void DrawDebug()
    {
        _menu.SectionHeader("Live");
        for (var i = 0; i < _live.Lines.Length; i++)
        {
            SettingsMenu.Dim(_live.Labels[i]);
            ImGui.SameLine(Math.Max(_menu.ControlColumn, 200f));
            ImGui.TextUnformatted(_live.Lines[i] ?? "");
        }
        if (_plugin.AtlasProblem is { } ap) SettingsMenu.Text(Danger, ap);
        if (_live.PoolProblem is { } pp) SettingsMenu.Text(Danger, pp);
        var problems = _plugin.Rules.Problems;
        if (problems.Count > 0)
        {
            _menu.SectionHeader("Didn't compile");
            foreach (var p in problems) SettingsMenu.Text(Danger, p);
        }
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
        // The same DTOs GetState answers with, kept and refilled, so the panel and the bridge can't disagree.
        private readonly HealthBarCountsDto _c = new();
        private readonly HealthBarTimingsDto _t = new();
        private readonly Dictionary<TextNode, (string src, TextTemplate template, string text, double at)> _samples = new(ReferenceEqualityComparer.Instance);

        public void Refresh(WhatsAHealthBar plugin)
        {
            var now = ImGui.GetTime();
            var problems = plugin.Rules.Problems.Count;
            if (problems != _problems) { _problems = problems; ProblemsBadge = problems == 1 ? "1 problem" : I(problems) + " problems"; }
            if (now - _at < 0.25 && _at >= 0) return;
            _at = now;
            var c = plugin.FillCounts(_c);
            var t = plugin.FillTimings(_t);
            if (c.Drawn != _drawn) { _drawn = c.Drawn; DrawnBadge = _drawn == 1 ? "1 bar" : I(_drawn) + " bars"; }
            Lines[0] = I(c.Tracked) + " / " + I(c.Wanted) + " / " + I(c.Drawn);
            Lines[1] = I(c.PeakDrawn);
            Lines[2] = I(c.WithStatuses) + " / " + I(c.Invulnerable) + " / " + I(c.WithDot);
            Lines[3] = F(t.TickUs) + " / " + F(t.TickMaxUs) + " us";
            Lines[4] = F(t.RenderUs) + " / " + F(t.RenderMaxUs) + " us";
            Lines[5] = F(t.ClassifyUs) + " us";
            Lines[6] = F(t.BuildUs) + " / " + F(t.FlushUs) + " us";
            Lines[7] = I(t.Quads) + " / " + I(t.Icons) + (plugin.Previewing ? "  (previewing)" : "");
            Lines[8] = t.RenderPath;
            Lines[9] = F(t.Motion.LagMs) + " ms / " + F(t.Motion.JitterShownPx) + " px";
            Lines[10] = I(plugin.LayersVersion);
            PoolProblem = plugin.PoolProblemNow;
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

        /// <summary>The rules changed: every sample resolves its look again on its next frame.</summary>
        public void Invalidate() => Array.Clear(_looked);

        public Bar Bar(WhatsAHealthBar plugin, BarKind kind) => Sim(plugin, (int)kind, kind).Bar;

        private PoolSim Sim(WhatsAHealthBar plugin, int slot, BarKind kind)
        {
            var sim = _sims[slot];
            if (sim != null) return sim;
            sim = plugin.MakeSim(kind, ImGui.GetTime());
            if (slot == 7) { sim.Bar.Invulnerable = true; sim.Bar.DisplayName = "Immune phase"; }
            sim.Bar.Dist = 30;
            return _sims[slot] = sim;
        }

        public void Draw(WhatsAHealthBar plugin, BarRenderer renderer, BarKind kind, float width, float height, string tip) => Draw(plugin, renderer, (int)kind, kind, width, height, tip);

        public void DrawImmune(WhatsAHealthBar plugin, BarRenderer renderer, float width, float height, string tip) => Draw(plugin, renderer, 7, BarKind.Unique, width, height, tip);

        private void Draw(WhatsAHealthBar plugin, BarRenderer renderer, int slot, BarKind kind, float width, float height, string tip)
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
                if (now - _looked[slot] >= 0.1) { _looked[slot] = now; plugin.PreviewLook(b, now, renderer); }
                b.UpdateDps(now, plugin.Settings.Text.DpsWindow.Value);
                WhatsAHealthBar.DotLoss(b, now);
                b.Animate(now, dt, plugin.AnimNow());
            }
            // The stage: a dark well with a faint ground line, the bar's bottom centre two thirds down (room for the name and
            // icons above, texts and the total line below).
            var org = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(width, height));
            if (ImGui.IsItemHovered()) SettingsMenu.Tooltip(tip);
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(org, org + new Vector2(width, height), Ui.Well, 4f);
            dl.AddRectFilledMultiColor(org, org + new Vector2(width, height * 0.5f), 0x18FFFFFF, 0x18FFFFFF, 0x00FFFFFF, 0x00FFFFFF);
            dl.AddRect(org, org + new Vector2(width, height), Ui.CardBorder, 4f);
            dl.AddText(org + new Vector2(8, 5), Ui.Faint, slot == 7 ? "Immune" : KindTabs[(int)kind]);
            b.Screen = new Vector2(MathF.Round(org.X + width * 0.5f), MathF.Round(org.Y + height * 0.66f));
            b.RestX = float.NaN; // re-snap to the panel's position (the panel can move between frames)
            renderer.Begin();
            renderer.Draw(b, now);
            renderer.Flush(dl);
        }
    }
}
