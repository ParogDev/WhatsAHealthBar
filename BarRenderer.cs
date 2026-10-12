using System;
using System.Drawing;
using System.Numerics;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>
/// Draws every bar as plain filled rectangles into one ImGui draw list (the background list, under the HUD's windows).
/// No textures, no strings built per frame and no HUD Graphics calls: everything shares the font atlas texture, so ImGui
/// batches all bars into one draw command whether there are 1 or 500. Edges are snapped to whole pixels for crisp bars.
/// </summary>
public sealed class BarRenderer
{
    private static readonly string[] Percent = BuildPercent();
    public static readonly string[] KindNames = { "Unique", "Rare", "Magic", "Normal", "Minion", "Player", "You" };

    public readonly KindStyle[] Styles = new KindStyle[7];
    private uint _outline, _bg, _trail, _heal, _es, _text, _divider;
    private float _rounding, _gloss, _shading;
    private int _outlineW, _cull, _pulse;
    private float _scale;

    public struct KindStyle
    {
        public bool Show, ShowPercent;
        public float W, H;
        public uint Life, LifeDark;
        public int Segments;
    }

    /// <summary>Reads the settings into flat colors and sizes, once per frame.</summary>
    public void Prepare(WhatsAHealthBarSettings s, float resolutionScale)
    {
        var look = s.Look;
        _scale = look.Scale.Value * (look.ScaleWithResolution ? resolutionScale : 1f);
        _rounding = look.Rounding.Value;
        _outlineW = look.Outline.Value;
        _outline = Pack(look.OutlineColor.Value);
        _bg = Pack(look.BackgroundColor.Value);
        _trail = Pack(look.TrailColor.Value);
        _heal = Pack(look.HealColor.Value);
        _es = Pack(look.EsColor.Value);
        _text = Pack(look.TextColor.Value);
        _divider = Pack(Color.FromArgb(150, 0, 0, 0));
        _gloss = look.Gloss.Value;
        _shading = look.Shading.Value;
        _cull = look.CullPercent.Value;
        _pulse = s.Animation.Enabled ? s.Animation.LowLifePulse.Value : 0;
        Set(BarKind.Unique, s.Unique); Set(BarKind.Rare, s.Rare); Set(BarKind.Magic, s.Magic); Set(BarKind.Normal, s.Normal);
        Set(BarKind.Minion, s.Minion); Set(BarKind.Player, s.Player); Set(BarKind.Self, s.Self);
    }

    private void Set(BarKind kind, KindSettings k)
    {
        var life = k.LifeColor.Value;
        Styles[(int)kind] = new KindStyle
        {
            Show = k.Show, ShowPercent = k.ShowPercent, W = k.Width.Value * _scale, H = k.Height.Value * _scale,
            Life = Pack(life), LifeDark = Pack(Color.FromArgb(life.A, Dark(life.R), Dark(life.G), Dark(life.B))),
            Segments = k.Segments.Value,
        };
        byte Dark(int c) => (byte)(c * (1 - _shading));
    }

    public void Draw(ImDrawListPtr dl, Bar b, bool showEs, double now, string? label = null)
    {
        var st = Styles[(int)b.Kind];
        var a = b.Alpha;
        if (a <= 0.004f) return;

        var w = MathF.Round(st.W);
        var h = MathF.Max(2, MathF.Round(st.H));
        var x0 = MathF.Round(b.Screen.X - w * 0.5f);
        var y0 = MathF.Round(b.Screen.Y - h);
        var x1 = x0 + w;
        var y1 = y0 + h;
        var r = MathF.Min(_rounding, h * 0.5f);

        if (_outlineW > 0)
            dl.AddRectFilled(new Vector2(x0 - _outlineW, y0 - _outlineW), new Vector2(x1 + _outlineW, y1 + _outlineW), Mul(_outline, a), r + _outlineW);
        dl.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y1), Mul(_bg, a), r);

        var xf = MathF.Round(x0 + w * b.Shown);
        var xt = MathF.Round(x0 + w * b.Trail);
        if (xt > xf)
            dl.AddRectFilled(new Vector2(xf, y0), new Vector2(xt, y1), Mul(_trail, a), r, xf <= x0 ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersRight);

        if (xf > x0)
        {
            var life = st.Life;
            var dark = st.LifeDark;
            if (_pulse > 0 && b.Kind <= BarKind.Rare && b.Shown * 100 < _pulse && !b.Dying)
            {
                var k = 0.5f + 0.5f * MathF.Sin((float)(now * 9.0));
                life = Lerp(life, 0xFFFFFFFF, 0.35f * k);
            }
            var corners = xf >= x1 ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft;
            dl.AddRectFilled(new Vector2(x0, y0), new Vector2(xf, y1), Mul(life, a), r, corners);
            var ym = MathF.Round(y0 + h * 0.5f);
            // Shading: transparent at mid height to the dark tone at the bottom; gloss: white at the top to nothing at mid.
            if (_shading > 0 && h >= 4)
                dl.AddRectFilledMultiColor(new Vector2(x0, ym), new Vector2(xf, y1 - (r > 1 ? 1 : 0)),
                    Mul(dark & 0x00FFFFFF, a), Mul(dark & 0x00FFFFFF, a), Mul(dark, a), Mul(dark, a));
            if (_gloss > 0 && h >= 4)
            {
                var top = Mul(0xFFFFFFFF, a * _gloss);
                dl.AddRectFilledMultiColor(new Vector2(x0 + (r > 1 ? 1 : 0), y0), new Vector2(xf - (r > 1 ? 1 : 0), ym), top, top, 0, 0);
            }
            if (b.Glow > 0) dl.AddRectFilled(new Vector2(x0, y0), new Vector2(xf, y1), Mul(_heal, a * b.Glow * 0.7f), r, corners);
            if (b.Flash > 0) dl.AddRectFilled(new Vector2(xf - 2, y0), new Vector2(xf + 1, y1), Mul(0xFFFFFFFF, a * b.Flash));
        }

        if (showEs && b.MaxEs > 0 && b.EsShown > 0.003f)
        {
            var esh = MathF.Max(2, MathF.Round(h * 0.34f));
            var xe = MathF.Round(x0 + w * b.EsShown);
            dl.AddRectFilled(new Vector2(x0, y0), new Vector2(xe, y0 + esh), Mul(_es, a), r, ImDrawFlags.RoundCornersTopLeft);
        }

        if (st.Segments > 1)
        {
            var c = Mul(_divider, a);
            for (var i = 1; i < st.Segments; i++)
            {
                var x = MathF.Round(x0 + w * i / st.Segments);
                dl.AddRectFilled(new Vector2(x, y0 + 1), new Vector2(x + 1, y1 - 1), c);
            }
        }

        if (_cull > 0 && b.Kind <= BarKind.Normal)
        {
            var x = MathF.Round(x0 + w * _cull / 100f);
            dl.AddRectFilled(new Vector2(x, y0 - 1), new Vector2(x + 1, y1 + 1), Mul(0xFFFFFFFF, a * 0.9f));
        }

        if (st.ShowPercent || label != null)
        {
            var fs = ImGui.GetFontSize();
            var ty = MathF.Round(y0 + (h - fs) * 0.5f);
            if (st.ShowPercent)
            {
                var pct = Percent[(int)MathF.Ceiling(b.LifeFraction * 100)];
                var tp = new Vector2(x1 + 4 + _outlineW, ty);
                dl.AddText(tp + Vector2.One, Mul(0xFF000000, a * 0.8f), pct);
                dl.AddText(tp, Mul(_text, a), pct);
            }
            if (label != null)
            {
                var tw = ImGui.CalcTextSize(label).X;
                dl.AddText(new Vector2(MathF.Round(x0 - tw - 8 - _outlineW), ty), Mul(_text, a), label);
            }
        }
    }

    /// <summary>System.Drawing ARGB to ImGui's packed ABGR.</summary>
    public static uint Pack(Color c) => (uint)(c.A << 24 | c.B << 16 | c.G << 8 | c.R);

    private static uint Mul(uint abgr, float alpha)
    {
        var aa = (uint)Math.Clamp((abgr >> 24) * alpha, 0, 255);
        return (abgr & 0x00FFFFFF) | aa << 24;
    }

    private static uint Lerp(uint x, uint y, float t)
    {
        uint Ch(int sh) => (uint)(((x >> sh) & 0xFF) + (((int)((y >> sh) & 0xFF) - (int)((x >> sh) & 0xFF)) * t)) & 0xFF;
        return Ch(0) | Ch(8) << 8 | Ch(16) << 16 | (x & 0xFF000000);
    }

    private static string[] BuildPercent()
    {
        var a = new string[101];
        for (var i = 0; i <= 100; i++) a[i] = i + "%";
        return a;
    }
}
