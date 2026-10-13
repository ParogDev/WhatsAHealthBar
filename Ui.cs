using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>
/// The settings panel's widget kit: the [Menu] nodes drawn as rows (label, control, tooltip from the node's own [Menu]
/// attribute, read once by reflection so the panel and the control center say the same thing), plus pills, chips, badges,
/// segmented choices, cards and section headers in the HUD's custom-panel style (techniques from syrairc's ExileImGui2 kit
/// and his ExileNameplates settings, ported, not linked). Everything takes TextUnformatted, so a % in a label is safe.
/// Nothing here allocates per frame beyond ImGui's own work: labels are the attribute strings, state is keyed by node.
/// </summary>
internal static class Ui
{
    // Palette, ImGui ABGR. Amber accent (the rare's gold, a step warmer), so red stays free for life and errors.
    public const uint Accent = 0xFF5CB8F5, AccentDim = 0xFF3C78A0, AccentFaint = 0x303C78A0;
    public const uint Text = 0xFFE1E6E8, Muted = 0xFF9A9296, Faint = 0xFF5E585C;
    public const uint Danger = 0xFF646EFF, Ok = 0xFF78DC6E;
    public const uint CardBg = 0xF216141A, CardBorder = 0xFF2E2A33, Well = 0xFF0C0B0F;

    /// <summary>Width of the label column; the control takes the rest up to <see cref="ControlW"/>.</summary>
    public const float LabelW = 196f;
    public static float ControlW { get; private set; } = 220f;

    public static Vector4 V(uint abgr) => new((abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f, (abgr >> 24) / 255f);
    /// <summary>Packed ABGR as given (not GetColorU32, which would multiply the alpha by the style's, e.g. inside BeginDisabled).</summary>
    public static uint U(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
    public static uint Alpha(uint abgr, float a) => (abgr & 0x00FFFFFF) | (uint)Math.Clamp((int)(a * 255), 0, 255) << 24;
    public static Vector4 ToV(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    public static System.Drawing.Color ToColor(Vector4 v) => System.Drawing.Color.FromArgb(B(v.W), B(v.X), B(v.Y), B(v.Z));
    private static int B(float f) => Math.Clamp((int)MathF.Round(f * 255), 0, 255);

    // ---- [Menu] labels and tooltips, by node reference ----

    private static readonly Dictionary<object, (string Label, string? Tip, int Id)> Menu = new(ReferenceEqualityComparer.Instance);
    private static object? _menuRoot;

    /// <summary>Reads every [Menu] node's label and tooltip under <paramref name="root"/> (once; again if the settings object was replaced).</summary>
    public static void Index(object root)
    {
        if (ReferenceEquals(_menuRoot, root)) return;
        Menu.Clear();
        _menuRoot = root;
        Walk(root, 0);
    }

    private static void Walk(object o, int depth)
    {
        if (depth > 4) return;
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var attr = p.GetCustomAttribute<MenuAttribute>();
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v == null) continue;
            if (v is ToggleNode or RangeNode<int> or RangeNode<float> or ColorNode or ListNode or TextNode or ButtonNode)
                // A node without [Menu] (Enable) is labelled by its property name, so a row never shows a bare "?". The
                // id is the node's place in the walk: unique, and the same every frame whatever else is drawn.
                Menu[v] = (attr?.MenuName ?? p.Name, string.IsNullOrEmpty(attr?.Tooltip) ? null : attr!.Tooltip, Menu.Count + 1);
            else if (p.PropertyType.IsClass && p.PropertyType.Namespace == typeof(Ui).Namespace) Walk(v, depth + 1);
        }
    }

    /// <summary>The node's [Menu] label (its property name without one); a node the index never reached says so.</summary>
    public static string Label(object node) => Menu.TryGetValue(node, out var m) ? m.Label : "(node not under the settings root)";
    public static string? TipOf(object node) => Menu.TryGetValue(node, out var m) ? m.Tip : null;

    /// <summary>Pushes the node's ImGui id (its index in the settings walk; 0 for a node the walk never reached).</summary>
    public static void PushNodeId(object node) => ImGui.PushID(Menu.TryGetValue(node, out var m) ? m.Id : 0);

    // ---- Style ----

    private static int _styleVars;

    private static readonly (ImGuiCol Col, uint Value)[] StyleColors =
    {
        (ImGuiCol.FrameBg, 0xFF1A1820u), (ImGuiCol.FrameBgHovered, 0xFF262330u), (ImGuiCol.FrameBgActive, 0xFF2E2A3Au),
        (ImGuiCol.SliderGrab, Accent), (ImGuiCol.SliderGrabActive, 0xFF8AD2FFu), (ImGuiCol.CheckMark, Accent),
        (ImGuiCol.Button, 0xFF2A2733u), (ImGuiCol.ButtonHovered, 0xFF3A3646u), (ImGuiCol.ButtonActive, AccentDim),
        (ImGuiCol.Header, AccentFaint), (ImGuiCol.HeaderHovered, 0x503C78A0u), (ImGuiCol.HeaderActive, 0x703C78A0u),
        (ImGuiCol.Tab, 0xFF1C1A22u), (ImGuiCol.TabHovered, 0xFF3A3646u), (ImGuiCol.TabActive, 0xFF3A3040u),
        (ImGuiCol.Border, CardBorder), (ImGuiCol.Separator, CardBorder), (ImGuiCol.PopupBg, 0xF8141218u),
        (ImGuiCol.TextSelectedBg, AccentFaint), (ImGuiCol.Text, Text), (ImGuiCol.TextDisabled, Muted),
    };

    /// <summary>The panel's look: rounded frames, amber grabs and checks, dark wells. Pop with <see cref="PopStyle"/>.</summary>
    public static void PushStyle()
    {
        var avail = ImGui.GetContentRegionAvail().X;
        ControlW = Math.Clamp(avail - LabelW - 24, 150f, 320f);
        for (var i = 0; i < StyleColors.Length; i++) ImGui.PushStyleColor(StyleColors[i].Col, StyleColors[i].Value);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3));
        _styleVars = 6;
    }

    public static void PopStyle()
    {
        ImGui.PopStyleVar(_styleVars);
        ImGui.PopStyleColor(StyleColors.Length);
    }

    // ---- Text and layout ----

    /// <summary>A section title in the accent with a hairline under it.</summary>
    public static void Section(string title)
    {
        ImGui.Dummy(new Vector2(0, 2));
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        ImGui.PushStyleColor(ImGuiCol.Text, Accent);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        var w = ImGui.GetContentRegionAvail().X;
        var y = p.Y + ImGui.GetTextLineHeight() + 3;
        dl.AddLine(new Vector2(p.X, y), new Vector2(p.X + w, y), Alpha(Accent, 0.22f), 1f);
        ImGui.Dummy(new Vector2(0, 2));
    }

    /// <summary>A muted, wrapped explanation line.</summary>
    public static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    public static void Colored(uint col, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, col);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>The tooltip for the last item, wrapped (no printf formatting, so a % is safe).</summary>
    public static void Tip(string? tip)
    {
        if (tip == null || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(340);
        ImGui.TextUnformatted(tip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>The label column of a row (its tooltip on hover), then the cursor at the control column.</summary>
    public static void RowLabel(string label, string? tip)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        Tip(tip);
        ImGui.SameLine(LabelW);
        ImGui.SetNextItemWidth(ControlW);
    }

    private static readonly Dictionary<object, float> Anim = new(ReferenceEqualityComparer.Instance);

    // ---- Node rows ----

    /// <summary>A toggle row: the pill, then the label; the node's tooltip on both.</summary>
    public static bool Toggle(ToggleNode node, string? label = null)
    {
        var tip = TipOf(node);
        var v = node.Value;
        PushNodeId(node);
        Anim.TryGetValue(node, out var a);
        var changed = Pill("##p", ref v, ref a);
        Anim[node] = a;
        Tip(tip);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label ?? Label(node));
        Tip(tip);
        ImGui.PopID();
        if (changed) node.Value = v;
        return changed;
    }

    /// <summary>A pill switch (40 x 20): track slides from dark to the accent, the knob follows.</summary>
    public static bool Pill(string id, ref bool value, ref float anim)
    {
        var dl = ImGui.GetWindowDrawList();
        var cur = ImGui.GetCursorScreenPos();
        const float w = 38f, h = 20f, r = h * 0.5f;
        var pad = (ImGui.GetFrameHeight() - h) * 0.5f;
        if (pad > 0) cur.Y += pad;
        ImGui.InvisibleButton(id, new Vector2(w, ImGui.GetFrameHeight()));
        var changed = ImGui.IsItemClicked();
        if (changed) value = !value;
        var dt = ImGui.GetIO().DeltaTime;
        var target = value ? 1f : 0f;
        anim = Math.Clamp(anim + (target - anim) * Math.Min(dt * 12f, 1f), 0f, 1f);
        // Faded with the style's alpha, so a pill inside BeginDisabled looks as dead as it is.
        var fade = ImGui.GetStyle().Alpha;
        var track = Colors.Lerp(0xFF2A2733, AccentDim, anim);
        if (ImGui.IsItemHovered()) track = Colors.Lerp(track, 0xFFFFFFFF, 0.08f);
        dl.AddRectFilled(cur, cur + new Vector2(w, h), Alpha(track, fade), r);
        var knobX = cur.X + r + anim * (w - h);
        dl.AddCircleFilled(new Vector2(knobX, cur.Y + r), r - 3f, Alpha(Colors.Lerp(0xFF8A8A90, 0xFFFFF4E0, anim), fade), 16);
        return changed;
    }

    public static bool Slider(RangeNode<int> node, string? label = null)
    {
        RowLabel(label ?? Label(node), TipOf(node));
        var v = node.Value;
        PushNodeId(node);
        var changed = ImGui.SliderInt("##s", ref v, node.Min, node.Max);
        ImGui.PopID();
        Tip(TipOf(node));
        if (changed) node.Value = v;
        return changed;
    }

    public static bool Slider(RangeNode<float> node, string fmt = "%.2f", string? label = null)
    {
        RowLabel(label ?? Label(node), TipOf(node));
        var v = node.Value;
        PushNodeId(node);
        var changed = ImGui.SliderFloat("##s", ref v, node.Min, node.Max, fmt);
        ImGui.PopID();
        Tip(TipOf(node));
        if (changed) node.Value = v;
        return changed;
    }

    /// <summary>A color row: swatch with the picker popup (alpha bar), the node's label and tooltip.</summary>
    public static bool Color(ColorNode node, string? label = null)
    {
        RowLabel(label ?? Label(node), TipOf(node));
        var v = ToV(node.Value);
        PushNodeId(node);
        var changed = ImGui.ColorEdit4("##c", ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
        ImGui.PopID();
        Tip(TipOf(node));
        if (changed) node.Value = ToColor(v);
        return changed;
    }

    /// <summary>A "#AARRGGBB" string color (the lists' tints and patches), same swatch.</summary>
    public static bool HexColor(string id, ref string hex)
    {
        var abgr = Colors.TryParse(hex ?? "", out var c) ? c : 0xFFFFFFFF;
        var v = V(abgr);
        if (!ImGui.ColorEdit4(id, ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf)) return false;
        hex = Colors.Format(U(v));
        return true;
    }

    /// <summary>A row of buttons, one per value, the chosen one lit: for 2-4 short choices.</summary>
    public static bool Segmented(ListNode node, string? label = null)
    {
        RowLabel(label ?? Label(node), TipOf(node));
        PushNodeId(node);
        var changed = SegmentedButtons(node.Values, node.Value, out var picked);
        ImGui.PopID();
        if (changed) node.Value = picked;
        return changed;
    }

    public static bool SegmentedButtons(IReadOnlyList<string> values, string? current, out string picked)
    {
        picked = current ?? "";
        var changed = false;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 6));
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            var on = values[i] == current;
            if (on) { ImGui.PushStyleColor(ImGuiCol.Button, AccentDim); ImGui.PushStyleColor(ImGuiCol.Text, 0xFFFFF4E0); }
            if (ImGui.Button(values[i]) && !on) { picked = values[i]; changed = true; }
            if (on) ImGui.PopStyleColor(2);
        }
        ImGui.PopStyleVar();
        return changed;
    }

    public static bool Button(string label, string? tip, float width = 0)
    {
        var r = ImGui.Button(label, new Vector2(width, 0));
        Tip(tip);
        return r;
    }

    public static bool AccentButton(string label, string? tip, float width = 0)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, AccentDim);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, 0xFF14120E);
        var r = ImGui.Button(label, new Vector2(width, 0));
        ImGui.PopStyleColor(3);
        Tip(tip);
        return r;
    }

    /// <summary>A small chip (token, path): click to act. Wraps onto the next line when the row is full.</summary>
    public static bool Chip(string text, string? tip, bool wrap)
    {
        if (wrap)
        {
            var next = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(text).X + 12;
            if (next < ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
        }
        ImGui.PushStyleColor(ImGuiCol.Button, 0xFF242130);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0xFF3C3850);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, 2));
        var r = ImGui.SmallButton(text);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        Tip(tip);
        return r;
    }

    /// <summary>A small colored pill of text with no widget behind it (a status, a count).</summary>
    public static void Badge(string text, uint fill, bool outline = false)
    {
        if (text.Length == 0) return;
        var ts = ImGui.CalcTextSize(text);
        var size = new Vector2(ts.X + 10, ts.Y + 3);
        var org = ImGui.GetCursorScreenPos();
        org.Y += (ImGui.GetFrameHeight() - size.Y) * 0.5f;
        ImGui.Dummy(new Vector2(size.X, ImGui.GetFrameHeight()));
        var dl = ImGui.GetWindowDrawList();
        var r = size.Y * 0.5f;
        if (outline) { dl.AddRect(org, org + size, fill, r); dl.AddText(org + new Vector2(5, 1.5f), fill, text); }
        else { dl.AddRectFilled(org, org + size, fill, r); dl.AddText(org + new Vector2(5, 1.5f), Contrast(fill), text); }
    }

    /// <summary>Readable text on a fill: near-black on light, white on dark.</summary>
    private static uint Contrast(uint c) => Colors.Luma(c) > 150 ? 0xFF14120E : 0xFFFFFFFF;

    // ---- Cards ----

    /// <summary>A bordered, filled box that hugs its content. Always pair with <see cref="EndCard"/>.</summary>
    public static void BeginCard(string id, bool highlight = false)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, highlight ? AccentDim : CardBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysAutoResize);
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    // ---- Confirm modal ----

    /// <summary>Opens the modal <paramref name="id"/>; <see cref="Confirm"/> draws it and answers true when confirmed.</summary>
    public static void Ask(string id) => ImGui.OpenPopup(id);

    public static bool Confirm(string id, string question, string yes)
    {
        var yesPressed = false;
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        var open = true;
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar)) return false;
        ImGui.PushTextWrapPos(360);
        ImGui.TextUnformatted(question);
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, 4));
        if (AccentButton(yes, null, 120)) { yesPressed = true; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return yesPressed;
    }

    // ---- Icons from the status atlas ----

    /// <summary>One status icon as an image (no widget) at <paramref name="size"/>, drawn as the bar draws it: bare, on its gem, or over a tinted badge, on a dark plate.</summary>
    public static void IconImage(IntPtr atlas, int icon, string badge, uint tint, uint badgeTint, float size, bool plate)
    {
        var org = ImGui.GetCursorScreenPos();
        org.Y += (ImGui.GetFrameHeight() - size) * 0.5f;
        ImGui.Dummy(new Vector2(size, ImGui.GetFrameHeight()));
        var dl = ImGui.GetWindowDrawList();
        if (plate) dl.AddRectFilled(org - new Vector2(2), org + new Vector2(size + 2), 0xFF0E0C12, 3f);
        if (atlas == IntPtr.Zero) { dl.AddRect(org, org + new Vector2(size), Muted); return; }
        float u0, v0, u1, v1;
        var end = org + new Vector2(size);
        if (badge == Badges.Gem)
        {
            StatusAtlas.Uv(icon, 0, out u0, out v0, out u1, out v1);
            dl.AddImage(atlas, org, end, new Vector2(u0, v0), new Vector2(u1, v1), tint);
            return;
        }
        var pad = Vector2.Zero;
        if (badge == Badges.OverBadge)
        {
            StatusAtlas.BadgeUv(out u0, out v0, out u1, out v1);
            dl.AddImage(atlas, org, end, new Vector2(u0, v0), new Vector2(u1, v1), badgeTint);
            pad = new Vector2(MathF.Round(size * 0.1f));
        }
        StatusAtlas.Uv(icon, 1, out u0, out v0, out u1, out v1, StatusAtlas.BareInset);
        dl.AddImage(atlas, org + pad, end - pad, new Vector2(u0, v0), new Vector2(u1, v1), tint);
    }

    /// <summary>An icon cell button for the picker grid: transparent frame, hover highlight, a ring when selected.</summary>
    public static bool IconCell(string id, IntPtr atlas, int icon, int sheet, uint tint, float size, bool selected)
    {
        bool clicked;
        if (atlas == IntPtr.Zero) clicked = ImGui.Button(id, new Vector2(size, size));
        else
        {
            StatusAtlas.Uv(icon, sheet, out var u0, out var v0, out var u1, out var v1, sheet == 1 ? StatusAtlas.BareInset : 0);
            ImGui.PushStyleColor(ImGuiCol.Button, 0u);
            clicked = ImGui.ImageButton(id, atlas, new Vector2(size, size), new Vector2(u0, v0), new Vector2(u1, v1), Vector4.Zero, V(tint));
            ImGui.PopStyleColor();
        }
        if (selected) ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), Accent, 3f, ImDrawFlags.None, 2f);
        return clicked;
    }
}
