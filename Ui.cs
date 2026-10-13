using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>
/// What the settings panel draws beyond the SettingsMenu renderer's own widgets: the palette (the panel's accent and the
/// sample stage colours), the confirm modal, and the status icons from the atlas as images and picker cells. The node rows,
/// pills, chips, badges, cards and the list editor are the renderer's (SettingsMenu.cs). Everything takes TextUnformatted,
/// so a % in a label is safe; nothing here allocates per frame.
/// </summary>
internal static class Ui
{
    // Palette, ImGui ABGR. Amber accent (the rare's gold, a step warmer), so red stays free for life and errors.
    public const uint Accent = 0xFF5CB8F5;
    public const uint Muted = 0xFF9A9296, Faint = 0xFF5E585C;
    public const uint Danger = 0xFF646EFF, Ok = 0xFF78DC6E;
    public const uint CardBorder = 0xFF2E2A33, Well = 0xFF0C0B0F;

    public static Vector4 V(uint abgr) => new((abgr & 0xFF) / 255f, ((abgr >> 8) & 0xFF) / 255f, ((abgr >> 16) & 0xFF) / 255f, (abgr >> 24) / 255f);
    public static uint Alpha(uint abgr, float a) => (abgr & 0x00FFFFFF) | (uint)Math.Clamp((int)(a * 255), 0, 255) << 24;

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
        ImGui.PushStyleColor(ImGuiCol.Button, Alpha(Accent, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Accent);
        if (ImGui.Button(yes, new Vector2(120, 0))) { yesPressed = true; ImGui.CloseCurrentPopup(); }
        ImGui.PopStyleColor(2);
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
