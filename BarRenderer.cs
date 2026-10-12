using System;
using System.Drawing;
using System.Numerics;
using ImGuiNET;

namespace WhatsAHealthBar;

/// <summary>
/// Draws every bar as axis-aligned quads, batched: <see cref="Draw"/> only appends quads (with a color per corner, so
/// gradients are free) to an array, and <see cref="Flush"/> writes them all straight into the draw list's vertex and index
/// buffers after a single PrimReserve. No P/Invoke, textures or allocations per bar: the cost of one more bar is a few
/// dozen memory writes, and the whole set is one draw command sharing the font atlas's white pixel, whether there are 1 or
/// 500 bars. Edges are whole pixels; rounded corners are pixel steps (crisper than ImGui's anti-aliased arcs at bar sizes).
/// Text (percent, preview labels) is queued and drawn after the quads, on top.
///
/// Pools (stacked): the bar is as wide as life + ES + ward. Each pool fills its current share in its own color, laid out in
/// the order damage removes them: life from the left, then ES, then ward, so the bar drains right to left the way the
/// monster does. A notch marks the end of the life share. Mostly-ES monsters read as a mostly-ES bar with a life stub.
/// </summary>
public sealed class BarRenderer
{
    private static readonly string[] Percent = BuildPercent();
    public static readonly string[] KindNames = { "Unique", "Rare", "Magic", "Normal", "Minion", "Player", "You" };

    public readonly KindStyle[] Styles = new KindStyle[7];
    private uint _outline, _bg, _trail, _heal, _es, _ward, _text;
    private float _gloss, _shading;
    private int _rounding, _outlineW, _cull, _pulse;
    private float _scale;
    private bool _esStrip, _lifeNotch;

    private Quad[] _quads = new Quad[4096];
    private int _quadCount;
    private TextItem[] _texts = new TextItem[256];
    private int _textCount;
    private float _fontSize = 13;

    /// <summary>Pools are stacked into one bar (life + ES + ward); otherwise the bar is life only.</summary>
    public bool Stacked { get; private set; }

    /// <summary>ES is read per frame (stacked, or drawn as a strip).</summary>
    public bool WantsEs => Stacked || _esStrip;

    /// <summary>Quads written by the last Flush (for the state's counters).</summary>
    public int LastQuads { get; private set; }

    public struct KindStyle
    {
        public bool Show, ShowPercent;
        public float W, H;
        public uint Life;
        public int Segments;
    }

    private struct Quad
    {
        public float X0, Y0, X1, Y1;
        public uint C0, C1, C2, C3; // top-left, top-right, bottom-right, bottom-left
    }

    private struct TextItem
    {
        public Vector2 Pos;
        public uint Col;
        public string Text;
    }

    /// <summary>Reads the settings into flat colors and sizes, once per frame.</summary>
    public void Prepare(WhatsAHealthBarSettings s, float resolutionScale)
    {
        var look = s.Look;
        _scale = look.Scale.Value * (look.ScaleWithResolution ? resolutionScale : 1f);
        _rounding = (int)MathF.Round(look.Rounding.Value);
        _outlineW = look.Outline.Value;
        _outline = Pack(look.OutlineColor.Value);
        _bg = Pack(look.BackgroundColor.Value);
        _trail = Pack(look.TrailColor.Value);
        _heal = Pack(look.HealColor.Value);
        _es = Pack(look.EsColor.Value);
        _ward = Pack(look.WardColor.Value);
        _text = Pack(look.TextColor.Value);
        _gloss = look.Gloss.Value;
        _shading = look.Shading.Value;
        _cull = look.CullPercent.Value;
        _pulse = s.Animation.Enabled ? s.Animation.LowLifePulse.Value : 0;
        var pools = look.Pools.Value;
        Stacked = pools == Bars.Stacked;
        _esStrip = pools == Bars.EsStrip;
        _lifeNotch = look.LifeNotch.Value;
        Set(BarKind.Unique, s.Unique); Set(BarKind.Rare, s.Rare); Set(BarKind.Magic, s.Magic); Set(BarKind.Normal, s.Normal);
        Set(BarKind.Minion, s.Minion); Set(BarKind.Player, s.Player); Set(BarKind.Self, s.Self);
    }

    private void Set(BarKind kind, KindSettings k) => Styles[(int)kind] = new KindStyle
    {
        Show = k.Show, ShowPercent = k.ShowPercent, W = k.Width.Value * _scale, H = k.Height.Value * _scale,
        Life = Pack(k.LifeColor.Value), Segments = k.Segments.Value,
    };

    /// <summary>Starts a frame's batch (Render, main thread).</summary>
    public void Begin()
    {
        _quadCount = 0;
        _textCount = 0;
        _fontSize = ImGui.GetFontSize();
    }

    /// <summary>Appends one bar's quads (and text) to the batch.</summary>
    public void Draw(Bar b, double now, string? label = null)
    {
        var st = Styles[(int)b.Kind];
        var a = b.Alpha;
        var maxTotal = b.MaxTotal(Stacked);
        if (a <= 0.004f || maxTotal <= 0) return;

        var w = MathF.Round(st.W);
        var h = MathF.Max(2, MathF.Round(st.H));
        var x0 = MathF.Round(b.Screen.X - w * 0.5f);
        var y0 = MathF.Round(b.Screen.Y - h);
        var x1 = x0 + w;
        var y1 = y0 + h;
        var r = Math.Min(_rounding, (int)(h * 0.5f));

        if (_outlineW > 0) Round(x0 - _outlineW, y0 - _outlineW, x1 + _outlineW, y1 + _outlineW, Mul(_outline, a), r > 0 ? r + _outlineW : 0, true, true);
        Round(x0, y0, x1, y1, Mul(_bg, a), r, true, true);

        var xf = MathF.Round(x0 + w * b.Shown);
        var xt = MathF.Round(x0 + w * b.Trail);
        if (xt > xf) Round(xf, y0, xt, y1, Mul(_trail, a), r, xf <= x0, xt >= x1);

        if (xf > x0)
        {
            var life = st.Life; var es = _es; var ward = _ward;
            if (_pulse > 0 && b.Kind <= BarKind.Rare && b.Shown * 100 < _pulse && !b.Dying)
            {
                var k = 0.35f * (0.5f + 0.5f * MathF.Sin((float)(now * 9.0)));
                life = Lerp(life, 0xFFFFFFFF, k); es = Lerp(es, 0xFFFFFFFF, k); ward = Lerp(ward, 0xFFFFFFFF, k);
            }

            // Pool boundaries from the current values, clipped to the animated fill; the last non-empty pool reaches the
            // fill's edge so rounding never leaves a gap.
            var px = w / maxTotal;
            var lifeEnd = xf;
            var esEnd = xf;
            if (Stacked)
            {
                lifeEnd = b.Es <= 0 && b.Ward <= 0 ? xf : MathF.Round(x0 + b.Hp * px);
                esEnd = b.Ward <= 0 ? xf : MathF.Round(lifeEnd + b.Es * px);
            }
            Seg(x0, MathF.Min(lifeEnd, xf), y0, y1, Mul(life, a), r, x0, x1);
            if (Stacked)
            {
                Seg(MathF.Max(x0, lifeEnd), MathF.Min(esEnd, xf), y0, y1, Mul(es, a), r, x0, x1);
                Seg(MathF.Max(x0, esEnd), xf, y0, y1, Mul(ward, a), r, x0, x1);
            }

            var ym = MathF.Round(y0 + h * 0.5f);
            var inset = r > 0 ? 1 : 0;
            // Shading: nothing at mid height to dark at the bottom; gloss: white at the top to nothing at mid. Pool-neutral.
            if (h >= 4 && xf - x0 > 2 * inset)
            {
                if (_shading > 0) Grad(x0 + inset, ym, xf - inset, y1 - inset, 0, Mul(0xFF000000, a * _shading));
                if (_gloss > 0) Grad(x0 + inset, y0, xf - inset, ym, Mul(0xFFFFFFFF, a * _gloss), 0);
            }
            if (b.Glow > 0) Round(x0, y0, xf, y1, Mul(_heal, a * b.Glow * 0.7f), r, true, xf >= x1);
            if (b.Flash > 0) Rect(xf - 2, y0, xf + 1, y1, Mul(0xFFFFFFFF, a * b.Flash));
        }

        if (Stacked && _lifeNotch && b.MaxHp < maxTotal)
        {
            var x = MathF.Round(x0 + w * b.MaxHp / maxTotal);
            Rect(x, y0, x + 1, y1, Mul(0xFF000000, a * 0.85f));
        }

        if (_esStrip && b.MaxEs > 0 && b.Es > 0)
        {
            var esh = MathF.Max(2, MathF.Round(h * 0.34f));
            Round(x0, y0, MathF.Round(x0 + w * Math.Clamp(b.Es / (float)b.MaxEs, 0, 1)), y0 + esh, Mul(_es, a), r, true, false);
        }

        if (st.Segments > 1)
        {
            var c = Mul(0x96000000, a);
            for (var i = 1; i < st.Segments; i++)
            {
                var x = MathF.Round(x0 + w * i / st.Segments);
                Rect(x, y0 + 1, x + 1, y1 - 1, c);
            }
        }

        if (_cull > 0 && b.Kind <= BarKind.Normal)
        {
            var x = MathF.Round(x0 + w * _cull / 100f);
            Rect(x, y0 - 1, x + 1, y1 + 1, Mul(0xFFFFFFFF, a * 0.9f));
        }

        if (st.ShowPercent || label != null)
        {
            var ty = MathF.Round(y0 + (h - _fontSize) * 0.5f);
            if (st.ShowPercent)
            {
                var pct = Percent[(int)MathF.Ceiling(b.Fraction(Stacked) * 100)];
                var tp = new Vector2(x1 + 4 + _outlineW, ty);
                Text(tp + Vector2.One, Mul(0xFF000000, a * 0.8f), pct);
                Text(tp, Mul(_text, a), pct);
            }
            if (label != null)
                Text(new Vector2(MathF.Round(x0 - ImGui.CalcTextSize(label).X - 8 - _outlineW), ty), Mul(_text, a), label);
        }
    }

    /// <summary>"batched", or "fallback: why" when the draw list's layout check failed (then each quad is an ImGui call).</summary>
    public string Path { get; private set; } = "batched";

    private int[] _vtx = new int[8000 * 4 * VertInts];
    private short[] _idx = new short[8000 * 6];
    private const int VertInts = 5, VertBytes = VertInts * 4; // ImDrawVert: Vector2 pos, Vector2 uv, uint col

    /// <summary>
    /// Writes the batch into <paramref name="dl"/>: one PrimReserve per 8k quads, then the vertices and indices are copied
    /// straight into the draw list's buffers and its write pointers advanced, exactly as ImGui's own PrimRect does. The HUD
    /// compiles plugins without /unsafe, so the pointer fields are reached by ref from <c>_Data</c> (ImDrawList: _Data,
    /// _OwnerName, _VtxWritePtr, _IdxWritePtr) and checked against the public view before every write; if an ImGui update
    /// moved them, the quads go through AddRectFilledMultiColor instead and <see cref="Path"/> says why.
    /// </summary>
    public void Flush(ImDrawListPtr dl)
    {
        LastQuads = _quadCount;
        if (_quadCount > 0)
        {
            ref var data = ref dl._Data;
            ref var vtxWrite = ref System.Runtime.CompilerServices.Unsafe.Add(ref data, 2);
            ref var idxWrite = ref System.Runtime.CompilerServices.Unsafe.Add(ref data, 3);
            var vb = dl.VtxBuffer;
            if (idxWrite != dl._IdxWritePtr)
                Fallback(dl, "the draw list's _IdxWritePtr is not 3 pointers after _Data (ImGui layout changed)");
            else if (vtxWrite != vb.Data + vb.Size * VertBytes)
                Fallback(dl, "the draw list's _VtxWritePtr is not at the end of VtxBuffer (ImGui layout changed)");
            else
            {
                Path = "batched";
                var uv = ImGui.GetIO().Fonts.TexUvWhitePixel;
                int u = BitConverter.SingleToInt32Bits(uv.X), v = BitConverter.SingleToInt32Bits(uv.Y);
                var done = 0;
                while (done < _quadCount)
                {
                    // 8000 quads = 32000 vertices, under the 16-bit index limit of one draw command.
                    var n = Math.Min(_quadCount - done, 8000);
                    dl.PrimReserve(n * 6, n * 4);
                    var idx = dl._VtxCurrentIdx;
                    var vi = 0; var ii = 0;
                    for (var i = 0; i < n; i++)
                    {
                        ref var q = ref _quads[done + i];
                        int x0 = BitConverter.SingleToInt32Bits(q.X0), y0 = BitConverter.SingleToInt32Bits(q.Y0);
                        int x1 = BitConverter.SingleToInt32Bits(q.X1), y1 = BitConverter.SingleToInt32Bits(q.Y1);
                        Vert(ref vi, x0, y0, u, v, q.C0);
                        Vert(ref vi, x1, y0, u, v, q.C1);
                        Vert(ref vi, x1, y1, u, v, q.C2);
                        Vert(ref vi, x0, y1, u, v, q.C3);
                        _idx[ii++] = (short)idx; _idx[ii++] = (short)(idx + 1); _idx[ii++] = (short)(idx + 2);
                        _idx[ii++] = (short)idx; _idx[ii++] = (short)(idx + 2); _idx[ii++] = (short)(idx + 3);
                        idx += 4;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(_vtx, 0, vtxWrite, vi);
                    System.Runtime.InteropServices.Marshal.Copy(_idx, 0, idxWrite, ii);
                    vtxWrite += vi * 4;
                    idxWrite += ii * 2;
                    dl._VtxCurrentIdx = idx;
                    done += n;
                }
            }
        }
        for (var i = 0; i < _textCount; i++)
        {
            ref var t = ref _texts[i];
            dl.AddText(t.Pos, t.Col, t.Text);
            t.Text = null!;
        }
    }

    private void Vert(ref int i, int x, int y, int u, int v, uint col)
    {
        _vtx[i] = x; _vtx[i + 1] = y; _vtx[i + 2] = u; _vtx[i + 3] = v; _vtx[i + 4] = (int)col;
        i += VertInts;
    }

    private void Fallback(ImDrawListPtr dl, string why)
    {
        Path = "fallback: " + why;
        for (var i = 0; i < _quadCount; i++)
        {
            ref var q = ref _quads[i];
            dl.AddRectFilledMultiColor(new Vector2(q.X0, q.Y0), new Vector2(q.X1, q.Y1), q.C0, q.C1, q.C2, q.C3);
        }
    }

    // ---- Quad builders ----

    private void Rect(float x0, float y0, float x1, float y1, uint col) => Grad4(x0, y0, x1, y1, col, col, col, col);

    private void Grad(float x0, float y0, float x1, float y1, uint top, uint bottom) => Grad4(x0, y0, x1, y1, top, top, bottom, bottom);

    private void Grad4(float x0, float y0, float x1, float y1, uint c0, uint c1, uint c2, uint c3)
    {
        if (x1 - x0 < 0.5f || y1 - y0 < 0.5f || ((c0 | c1 | c2 | c3) >> 24) == 0) return;
        if (_quadCount == _quads.Length) Array.Resize(ref _quads, _quads.Length * 2);
        ref var q = ref _quads[_quadCount++];
        q.X0 = x0; q.Y0 = y0; q.X1 = x1; q.Y1 = y1;
        q.C0 = c0; q.C1 = c1; q.C2 = c2; q.C3 = c3;
    }

    /// <summary>
    /// A rectangle with pixel-stepped rounded corners on the chosen ends: a body plus one 1-px column per step, each inset to
    /// follow a circle of radius <paramref name="r"/> (r = 2 cuts each corner by one pixel).
    /// </summary>
    private void Round(float x0, float y0, float x1, float y1, uint col, int r, bool left, bool right)
    {
        r = Math.Min(Math.Min(r, (int)((x1 - x0) * 0.5f)), Insets.Length - 1);
        var cols = r > 0 ? Insets[r].Length : 0; // only the columns that are actually inset; the rest is body
        if (cols == 0 || (!left && !right)) { Rect(x0, y0, x1, y1, col); return; }
        Rect(left ? x0 + cols : x0, y0, right ? x1 - cols : x1, y1, col);
        var ins = Insets[r];
        for (var k = 0; k < cols; k++)
        {
            if (left) Rect(x0 + k, y0 + ins[k], x0 + k + 1, y1 - ins[k], col);
            if (right) Rect(x1 - k - 1, y0 + ins[k], x1 - k, y1 - ins[k], col);
        }
    }

    /// <summary>Per radius, the vertical inset of each corner column that is inset at all (a circle, rounded to pixels).</summary>
    private static readonly int[][] Insets = BuildInsets(12);

    private static int[][] BuildInsets(int max)
    {
        var all = new int[max + 1][];
        for (var r = 0; r <= max; r++)
        {
            var list = new System.Collections.Generic.List<int>();
            for (var k = 0; k < r; k++)
            {
                var d = r - k - 0.5f;
                var inset = (int)MathF.Round(r - MathF.Sqrt(r * r - d * d));
                if (inset <= 0) break;
                list.Add(inset);
            }
            all[r] = list.ToArray();
        }
        return all;
    }

    /// <summary>One pool's rectangle; rounded only where it touches the bar's ends.</summary>
    private void Seg(float from, float to, float y0, float y1, uint col, int r, float x0, float x1)
    {
        if (to - from < 0.5f) return;
        Round(from, y0, to, y1, col, r, from <= x0, to >= x1);
    }

    private void Text(Vector2 pos, uint col, string text)
    {
        if (_textCount == _texts.Length) Array.Resize(ref _texts, _texts.Length * 2);
        _texts[_textCount++] = new TextItem { Pos = pos, Col = col, Text = text };
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
