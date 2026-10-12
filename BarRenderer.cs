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
/// Pools are independent (life, ES, ward each fill, drain and regenerate on their own, with their own trail and glow):
/// sandwich (default), they are layers stacked top to bottom (ward, ES, life), each as thick as its share of the combined
/// max so together they always fill the bar, each filling left to right by its own value; side by side, each owns a region
/// of the width by its max share. The sum of the pools shows as a thin total
/// line under the bar and as the percent.
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
    private bool _dividers;
    private BarKind _totalUpTo; // the total line shows for kinds up to this one (Unique..), or none when Off
    private bool _totalOn;

    private Quad[] _quads = new Quad[4096];
    private int _quadCount;
    private TextItem[] _texts = new TextItem[256];
    private int _textCount;
    private float _fontSize = 13;
    private readonly System.Collections.Generic.Dictionary<string, float> _labelWidths = new();
    private float _labelFontSize;

    /// <summary>Pool layout: side by side (regions by max share), layered (full-width strips), or life only.</summary>
    public bool Sandwich { get; private set; } = true;
    public bool SideBySide { get; private set; }

    /// <summary>ES and ward are read per frame (not in Life only).</summary>
    public bool WantsPools => Sandwich || SideBySide;

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
        SideBySide = pools == Bars.SideBySide;
        Sandwich = !SideBySide && pools != Bars.LifeOnly; // anything else (an old saved value) is the default
        _dividers = look.PoolDividers.Value;
        var total = look.TotalLine.Value;
        _totalOn = total is Bars.TotalAll or Bars.TotalRareUnique;
        _totalUpTo = total == Bars.TotalAll ? BarKind.Self : BarKind.Rare;
        Set(BarKind.Unique, s.Unique); Set(BarKind.Rare, s.Rare); Set(BarKind.Magic, s.Magic); Set(BarKind.Normal, s.Normal);
        Set(BarKind.Minion, s.Minion); Set(BarKind.Player, s.Player); Set(BarKind.Self, s.Self);
    }

    private void Set(BarKind kind, KindSettings k) => Styles[(int)kind] = new KindStyle
    {
        Show = k.Show, ShowPercent = k.ShowPercent, W = k.Width.Value * _scale, H = k.Height.Value * _scale,
        Life = Pack(k.LifeColor.Value), Segments = k.Segments.Value,
    };

    /// <summary>A frame with nothing to draw.</summary>
    public void Idle() => LastQuads = 0;

    /// <summary>Starts a frame's batch (Render, main thread).</summary>
    public void Begin()
    {
        _quadCount = 0;
        _textCount = 0;
        _fontSize = ImGui.GetFontSize();
    }

    /// <summary>Appends one bar's quads (and text) to the batch.</summary>
    /// <summary>Moving bars are drawn at fractional positions; bars at rest on whole pixels (with hysteresis).</summary>
    public bool SubPixel { get; set; } = true;

    /// <summary>Screen speed above which a bar counts as moving (sub-pixel blending on); below, it snaps to whole pixels.</summary>
    private const float MovingPxPerSec = 40f;

    /// <summary>
    /// The blended copies redraw only opaque quads: a second copy of a translucent quad (the background, veils, gloss) would
    /// stack opacity by the fraction, which changes every frame and reads as a pulse.
    /// </summary>
    private bool _opaqueOnly;
    private float _copyAlpha = 1;

    /// <summary>
    /// Appends one bar. Its top-left is fractional (the interpolated position). At rest (no motion for 0.25 s) the bar keeps
    /// a whole-pixel origin until it drifts 0.75 px, so it stays crisp and never flickers between two pixels. In motion it
    /// is drawn at the whole pixel below, then again one pixel right at the fractional x as alpha, and one pixel down at
    /// the fractional y: the blend places it between pixels (a linear resample of the opaque bar), so slow motion glides
    /// instead of stepping a pixel at a time. Motion blurs the extra softness away; at rest nothing is blended.
    /// </summary>
    public void Draw(Bar b, double now, string? label = null)
    {
        var st = Styles[(int)b.Kind];
        if (b.Alpha <= 0.004f || b.MaxTotal <= 0) return;
        var w = MathF.Round(st.W);
        var h = MathF.Max(2, MathF.Round(st.H));
        var ox = b.Screen.X - w * 0.5f;
        var oy = b.Screen.Y - h;
        // Real movement only: a smoothed speed over ~0.1 s. Your own bar while the camera follows you sits nearly still on
        // screen with sub-pixel wobble; reading that as motion switched it to blending every other frame (a flicker).
        var step = (b.Shown1 - b.Shown2).Length();
        var dt = MathF.Max(0.001f, (float)(now - b.SpeedAt));
        b.SpeedAt = now;
        b.Speed += (MathF.Min(step / dt, 5000) - b.Speed) * MathF.Min(1, dt / 0.1f);
        if (b.Speed > MovingPxPerSec) b.MovedAt = now;
        var moving = SubPixel && now - b.MovedAt < 0.25;

        if (!moving)
        {
            if (float.IsNaN(b.RestX) || MathF.Abs(ox - b.RestX) > 0.75f || MathF.Abs(oy - b.RestY) > 0.75f)
            {
                b.RestX = MathF.Round(ox);
                b.RestY = MathF.Round(oy);
            }
            DrawAt(b, b.RestX, b.RestY, w, h, 1, now, label);
            return;
        }
        b.RestX = MathF.Round(ox);
        b.RestY = MathF.Round(oy);
        var ix = MathF.Floor(ox); var fx = ox - ix;
        var iy = MathF.Floor(oy); var fy = oy - iy;
        DrawAt(b, ix, iy, w, h, 1, now, label);
        _opaqueOnly = true;
        _copyAlpha = b.Alpha * fx;
        if (fx > 0.02f) DrawAt(b, ix + 1, iy, w, h, fx, now, null, false);
        _copyAlpha = b.Alpha * fy;
        if (fy > 0.02f) DrawAt(b, ix, iy + 1, w, h, fy, now, null, false);
        _opaqueOnly = false;
    }

    private void DrawAt(Bar b, float x0, float y0, float w, float h, float alphaMul, double now, string? label, bool text = true)
    {
        var st = Styles[(int)b.Kind];
        var a = b.Alpha * alphaMul;
        var x1 = x0 + w;
        var y1 = y0 + h;
        var r = Math.Min(_rounding, (int)(h * 0.5f));

        if (_outlineW > 0) Round(x0 - _outlineW, y0 - _outlineW, x1 + _outlineW, y1 + _outlineW, Mul(_outline, a), r > 0 ? r + _outlineW : 0, true, true);
        Round(x0, y0, x1, y1, Mul(_bg, a), r, true, true);

        var life = st.Life; var es = _es; var ward = _ward;
        if (_pulse > 0 && b.Kind <= BarKind.Rare && !b.Dying && b.MaxHp > 0 && b.Shown[Bar.Life] * 100 < _pulse)
        {
            var k = 0.35f * (0.5f + 0.5f * MathF.Sin((float)(now * 9.0)));
            life = Lerp(life, 0xFFFFFFFF, k);
        }


        // Overfull spills past the bar's right end (outside the outline); the percent text moves past the longest spill.
        var spillX = x1 + _outlineW;
        var spillMax = 0f;

        // Life's region (for the culling marker): the whole bar unless side by side.
        float lifeX0 = x0, lifeX1 = x1;
        if (SideBySide)
        {
            // Each pool owns a region of the width by its (animated) share of the combined max, in damage order: life, ES, ward.
            var shareSum = b.Share[0] + b.Share[1] + b.Share[2];
            var cursor = x0;
            var first = true;
            for (var p = 0; p < Bar.PoolCount; p++)
            {
                if (b.Share[p] <= 0.001f || shareSum <= 0) continue;
                var rx0 = MathF.Round(cursor);
                cursor += w * b.Share[p] / shareSum;
                var rx1 = MathF.Round(cursor);
                if (rx1 >= x1 - 0.5f) rx1 = x1;
                if (rx1 <= rx0) continue;
                if (p == Bar.Life) { lifeX0 = rx0; lifeX1 = rx1; }
                var col = PoolColor(p, life, es, ward);
                Pool(b, p, rx0, rx1, y0, y1, col, a, r, rx0 <= x0, rx1 >= x1);
                if (!first && _dividers) Rect(rx0, y0, rx0 + 1, y1, Mul(0xFF000000, a * 0.85f));
                if (b.Veil[p] > 0.002f && !b.Temp[p])
                    spillMax = MathF.Max(spillMax, Spill(spillX, y0, y1, w, col, a, b.Veil[p]));
                first = false;
            }
        }
        else if (Sandwich)
        {
            // Layers top to bottom in damage order (ward, ES, life); their thicknesses are the pools' (animated) shares of
            // the combined max, so together they always fill the bar's whole height. Each layer fills left to right by its
            // own cur/max; an overfull layer spills past the end on its own row.
            var layers = Layers(b, (int)h);
            var ly = y0;
            int top = -1, bottom = -1;
            for (var p = Bar.PoolCount - 1; p >= 0; p--)
                if (layers[p] > 0) { if (top < 0) top = p; bottom = p; }
            for (var p = Bar.PoolCount - 1; p >= 0; p--)
            {
                if (layers[p] <= 0) continue;
                var ly1 = ly + layers[p];
                var col = PoolColor(p, life, es, ward);
                Layer(b, p, x0, x1, ly, ly1, col, a, r > 0, p == top, p == bottom);
                if (b.Veil[p] > 0.002f && !b.Temp[p])
                    spillMax = MathF.Max(spillMax, Spill(spillX, ly, ly1, w, col, a, b.Veil[p]));
                ly = ly1;
            }
        }
        else
        {
            Pool(b, Bar.Life, x0, x1, y0, y1, life, a, r, true, true);
            if (b.Veil[Bar.Life] > 0.002f) spillMax = Spill(spillX, y0, y1, w, life, a, b.Veil[Bar.Life]);
        }

        // Shading and gloss over the whole bar (a glass tube): dark from mid to bottom, white from top to mid.
        var inset = r > 0 ? 1 : 0;
        if (h >= 4)
        {
            var ym = MathF.Round(y0 + h * 0.5f);
            if (_shading > 0) Grad(x0 + inset, ym, x1 - inset, y1 - inset, 0, Mul(0xFF000000, a * _shading));
            if (_gloss > 0) Grad(x0 + inset, y0, x1 - inset, ym, Mul(0xFFFFFFFF, a * _gloss), 0);
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

        if (_cull > 0 && b.Kind <= BarKind.Normal && b.MaxHp > 0)
        {
            var x = MathF.Round(lifeX0 + (lifeX1 - lifeX0) * _cull / 100f);
            Rect(x, y0 - 1, x + 1, y1 + 1, Mul(0xFFFFFFFF, a * 0.9f));
        }

        // The sum: all pools against their normal maxima, as a thin line under the bar; past 100% (overfull or a granted
        // pool) the line is full and the excess spills like the pools do.
        var below = y1 + _outlineW;
        var total = b.TotalFraction;
        if (_totalOn && b.Kind <= _totalUpTo && (b.Max[Bar.Es] > 0 || b.Max[Bar.Ward] > 0 || total > 1.001f))
        {
            var th = MathF.Max(2, MathF.Round(2 * _scale));
            Rect(x0, below + 1, x1, below + 1 + th, Mul(_bg, a));
            Rect(x0, below + 1, MathF.Round(x0 + w * MathF.Min(1, total)), below + 1 + th, Mul(_text, a * 0.9f));
            if (total > 1.001f) Spill(spillX, below + 1, below + 1 + th, w, _text, a, total - 1);
        }

        if (text && (st.ShowPercent || label != null))
        {
            var ty = MathF.Round(y0 + (h - _fontSize) * 0.5f);
            if (st.ShowPercent)
            {
                var pct = Percent[Math.Clamp((int)MathF.Ceiling(total * 100), 0, Percent.Length - 1)];
                var tp = new Vector2(spillX + spillMax + 4, ty);
                Text(tp + Vector2.One, Mul(0xFF000000, a * 0.8f), pct);
                Text(tp, Mul(_text, a), pct);
            }
            if (label != null) Text(new Vector2(MathF.Round(x0 - LabelWidth(label) - 8 - _outlineW), ty), Mul(_text, a), label);
        }
    }

    private static uint PoolColor(int p, uint life, uint es, uint ward) => p == Bar.Life ? life : p == Bar.Es ? es : ward;

    /// <summary>How opaque an overfull veil or a granted pool is: faint for a little, bolder as more is added.</summary>
    private static float VeilAlpha(float over) => 0.22f + 0.53f * MathF.Min(1, over);

    /// <summary>
    /// The overfull part of a pool, outside the bar's right end on that pool's row: as long as 6% of the bar for a sliver
    /// up to 30% at double, and from faint to bold, so 5% over and 300% over don't look alike. Returns its length.
    /// </summary>
    private float Spill(float x, float sy0, float sy1, float w, uint col, float a, float over)
    {
        var len = MathF.Max(2, MathF.Round(w * (0.06f + 0.24f * MathF.Min(1, over))));
        var c = Mul(col, a * VeilAlpha(over));
        // Fades out toward its tip so it reads as overflow, not as more bar.
        Grad4(x, sy0, x + len, sy1, c, Mul(c, 0.25f), Mul(c, 0.25f), c);
        return len;
    }

    /// <summary>One pool in its region: trail chip, fill, regen glow, hit flash, from that pool's own animation.</summary>
    private void Pool(Bar b, int p, float rx0, float rx1, float ry0, float ry1, uint col, float a, int r, bool roundLeft, bool roundRight)
    {
        var rw = rx1 - rx0;
        var xf = MathF.Round(rx0 + rw * b.Shown[p]);
        var xt = MathF.Round(rx0 + rw * b.Trail[p]);
        if (xt > xf) Round(xf, ry0, xt, ry1, Mul(_trail, a), r, roundLeft && xf <= rx0, roundRight && xt >= rx1);
        if (xf <= rx0) return;
        var right = roundRight && xf >= rx1;
        // A granted pool (no max of its own) is a veil: translucent, bolder the bigger it is next to the other pools.
        var fill = b.Temp[p] ? a * VeilAlpha(b.Veil[p]) : a;
        Round(rx0, ry0, xf, ry1, Mul(col, fill), r, roundLeft, right);
        if (b.Glow[p] > 0) Round(rx0, ry0, xf, ry1, Mul(_heal, a * b.Glow[p] * 0.7f), r, roundLeft, right);
        if (b.Flash[p] > 0) Rect(MathF.Max(rx0, xf - 2), ry0, MathF.Min(rx1, xf + 1), ry1, Mul(0xFFFFFFFF, a * b.Flash[p]));
    }

    private readonly int[] _layers = new int[Bar.PoolCount];

    /// <summary>
    /// Pixel thickness of each pool's layer: proportional to its animated share (so a pool appearing or a max changing
    /// glides instead of jumping), summing exactly to the bar height (largest remainder), at least 2 px for any pool present
    /// when the bar is tall enough, so a small ES or ward share stays visible.
    /// </summary>
    private int[] Layers(Bar b, int h)
    {
        Array.Clear(_layers);
        var total = 0f;
        var present = 0;
        for (var p = 0; p < Bar.PoolCount; p++)
            if (b.Share[p] > 0.001f) { present++; total += b.Share[p]; }
        if (present == 0) return _layers;
        var min = h >= present * 2 ? 2 : h >= present ? 1 : 0;
        var free = h - min * present;
        var used = 0;
        Span<float> rem = stackalloc float[Bar.PoolCount];
        for (var p = 0; p < Bar.PoolCount; p++)
        {
            if (b.Share[p] <= 0.001f) continue;
            var exact = free * b.Share[p] / total;
            _layers[p] = min + (int)exact;
            rem[p] = exact - (int)exact;
            used += _layers[p];
        }
        while (used < h)
        {
            var best = -1;
            for (var p = 0; p < Bar.PoolCount; p++)
                if (b.Share[p] > 0.001f && (best < 0 || rem[p] > rem[best])) best = p;
            if (best < 0) break;
            _layers[best]++;
            rem[best] = -1;
            used++;
        }
        return _layers;
    }

    /// <summary>
    /// One sandwich layer: trail chip, fill, glow, flash from the pool's own animation. With rounding on, the bar's outer
    /// corners stay cut by one pixel (the top layer's top corners, the bottom layer's bottom corners).
    /// </summary>
    private void Layer(Bar b, int p, float x0, float x1, float ly0, float ly1, uint col, float a, bool cut, bool isTop, bool isBottom)
    {
        var w = x1 - x0;
        var xf = MathF.Round(x0 + w * b.Shown[p]);
        var xt = MathF.Round(x0 + w * b.Trail[p]);
        float t = cut && isTop ? 1 : 0, bt = cut && isBottom ? 1 : 0;
        if (xt > xf) CutRect(xf, ly0, xt, ly1, Mul(_trail, a), x0, x1, t, bt);
        if (xf <= x0) return;
        var fill = b.Temp[p] ? a * VeilAlpha(b.Veil[p]) : a;
        CutRect(x0, ly0, xf, ly1, Mul(col, fill), x0, x1, t, bt);
        if (b.Glow[p] > 0) CutRect(x0, ly0, xf, ly1, Mul(_heal, a * b.Glow[p] * 0.7f), x0, x1, t, bt);
        if (b.Flash[p] > 0) Rect(MathF.Max(x0, xf - 2), ly0, MathF.Min(x1, xf + 1), ly1, Mul(0xFFFFFFFF, a * b.Flash[p]));
    }

    /// <summary>A rect whose 1-px end columns, where they touch the bar's ends, are shortened by the corner cut.</summary>
    private void CutRect(float rx0, float ry0, float rx1, float ry1, uint col, float x0, float x1, float cutTop, float cutBottom)
    {
        if (cutTop == 0 && cutBottom == 0) { Rect(rx0, ry0, rx1, ry1, col); return; }
        var left = rx0 <= x0; var right = rx1 >= x1;
        Rect(left ? rx0 + 1 : rx0, ry0, right ? rx1 - 1 : rx1, ry1, col);
        if (left) Rect(rx0, ry0 + cutTop, rx0 + 1, ry1 - cutBottom, col);
        if (right && rx1 - 1 > rx0) Rect(rx1 - 1, ry0 + cutTop, rx1, ry1 - cutBottom, col);
    }


    private float LabelWidth(string label)
    {
        if (_labelFontSize != _fontSize) { _labelWidths.Clear(); _labelFontSize = _fontSize; }
        if (!_labelWidths.TryGetValue(label, out var w)) _labelWidths[label] = w = ImGui.CalcTextSize(label).X;
        return w;
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
            else if ((ImGui.GetIO().BackendFlags & ImGuiBackendFlags.RendererHasVtxOffset) == 0 &&
                     dl._VtxCurrentIdx + (long)_quadCount * 4 > ushort.MaxValue)
                // Without VtxOffset the renderer can't start a new 16-bit index range: ImGui's own calls would wrap the
                // indices too. Draw what fits and say so.
                Fallback(dl, $"{_quadCount * 4} vertices would pass the 16-bit index limit and the renderer has no VtxOffset support");
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
        if (_opaqueOnly)
        {
            // In a blended copy: keep quads that were opaque before the copy's fraction was applied (_copyAlpha), scaled back.
            var min = Math.Min(Math.Min(c0 >> 24, c1 >> 24), Math.Min(c2 >> 24, c3 >> 24));
            if (min < 0.85f * 255 * _copyAlpha) return;
        }
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
        var a = new string[1000];
        for (var i = 0; i < a.Length; i++) a[i] = i + "%";
        return a;
    }
}
