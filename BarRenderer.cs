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
    private int _rounding, _outlineW, _pulse;

    // Information layers (prepared once per frame from the settings)
    /// <summary>Per kind, the above / on / below templates (kind * 3 + slot), reparsed only when the setting's text changes.</summary>
    private readonly TextTemplate[] _templates = new TextTemplate[7 * 3];
    private float _textScale = 1;
    private bool _textShadow;
    private bool _cullNotch, _cullTint, _invulnHide;
    public bool CullHide { get; private set; }
    private uint _cullColor, _dotColor;
    private bool _dot;
    private bool _icons, _showStacks, _showDuration, _iconPlate;
    private BarKind _iconsUpTo;
    private int _iconCols, _iconMax;
    private float _iconSize;
    private byte _debuffAnchor, _buffAnchor;
    /// <summary>The compiled status rules (the plugin swaps it when the lists change).</summary>
    public CompiledLayers Rules = new();
    /// <summary>The status atlas texture for ImGui (0: not loaded; icons are skipped and IconProblem says why).</summary>
    public IntPtr AtlasId;
    public bool IconsWanted => _icons;
    /// <summary>Which kinds read buffs for icons (Unique..this one; hostile only unless Everything).</summary>
    public bool IconsFor(BarKind k) => _icons && (k <= _iconsUpTo);
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
        public bool Show, ShowPercent, HasText;
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
        /// <summary>Font size; 0 = the default font size.</summary>
        public float Size;
    }

    /// <summary>A textured quad from the status atlas (one extra draw command for all icons).</summary>
    private struct IconQuad
    {
        public float X0, Y0, X1, Y1, U0, V0, U1, V1;
        public uint Col;
    }

    private IconQuad[] _iconQuads = new IconQuad[256];
    private int _iconCount;

    /// <summary>Icons written by the last Flush.</summary>
    public int LastIcons { get; private set; }

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
        _pulse = s.Animation.Enabled ? s.Animation.LowLifePulse.Value : 0;

        var states = s.States;
        var cull = states.Cullable.Value;
        _cullNotch = cull is StateModes.Notch or StateModes.NotchTint;
        _cullTint = cull == StateModes.NotchTint;
        CullHide = cull == StateModes.Hide;
        _cullColor = Pack(states.CullableColor.Value);
        _invulnHide = states.Invulnerable.Value == StateModes.Hide;
        _dot = s.Dot.Enabled;
        _dotColor = Pack(s.Dot.Color.Value);
        _textScale = s.Text.Scale.Value;
        _textShadow = s.Text.Shadow;
        var ic = s.Icons;
        _icons = ic.Enabled;
        _iconsUpTo = ic.Kinds.Value switch
        {
            IconKinds.RareUnique => BarKind.Rare,
            IconKinds.Enemies => BarKind.Normal,
            IconKinds.All => BarKind.Self,
            _ => BarKind.Magic,
        };
        _iconCols = Math.Max(1, ic.Columns.Value);
        _iconMax = Math.Max(1, ic.MaxIcons.Value);
        _showStacks = ic.ShowStacks;
        _showDuration = ic.ShowDuration;
        _iconPlate = ic.Plate;
        _debuffAnchor = (byte)Math.Max(0, Array.IndexOf(Anchors.All, ic.DebuffAnchor.Value));
        _buffAnchor = (byte)Math.Max(0, Array.IndexOf(Anchors.All, ic.BuffAnchor.Value));
        var pools = look.Pools.Value;
        SideBySide = pools == Bars.SideBySide;
        Sandwich = !SideBySide && pools != Bars.LifeOnly; // anything else (an old saved value) is the default
        _dividers = look.PoolDividers.Value;
        var total = look.TotalLine.Value;
        _totalOn = total is Bars.TotalAll or Bars.TotalRareUnique;
        _totalUpTo = total == Bars.TotalAll ? BarKind.Self : BarKind.Rare;
        _iconSize = MathF.Round(ic.Size.Value * _scale);
        Set(BarKind.Unique, s.Unique); Set(BarKind.Rare, s.Rare); Set(BarKind.Magic, s.Magic); Set(BarKind.Normal, s.Normal);
        Set(BarKind.Minion, s.Minion); Set(BarKind.Player, s.Player); Set(BarKind.Self, s.Self);
    }

    private void Set(BarKind kind, KindSettings k)
    {
        Styles[(int)kind] = new KindStyle
        {
            Show = k.Show, ShowPercent = k.ShowPercent, W = k.Width.Value * _scale, H = k.Height.Value * _scale,
            Life = Pack(k.LifeColor.Value), Segments = k.Segments.Value,
        };
        Template((int)kind * 3 + 0, k.TextAbove.Value);
        Template((int)kind * 3 + 1, k.TextOn.Value);
        Template((int)kind * 3 + 2, k.TextBelow.Value);
        var any = false;
        for (var i = 0; i < 3; i++) any |= !_templates[(int)kind * 3 + i].IsEmpty;
        Styles[(int)kind].HasText = any;
    }

    /// <summary>Reparses a slot's template only when its text changed (a reference compare per frame otherwise).</summary>
    private void Template(int at, string? src)
    {
        src ??= "";
        var t = _templates[at];
        if (t == null || !ReferenceEquals(t.Source, src) && t.Source != src) _templates[at] = TextTemplate.Parse(src);
    }

    /// <summary>The kind's look before any patch (overrides, statuses, states), for the classify-rate resolve.</summary>
    public BarLook BaseLook(BarKind kind)
    {
        var life = Styles[(int)kind].Life;
        return new BarLook
        {
            Life = life, Es = _es, Ward = _ward, Bg = _bg, Outline = _outline, Text = _text,
            Name = Lerp(life, 0xFFFFFFFF, 0.4f), Opacity = 1,
        };
    }

    /// <summary>Hide the bar while invulnerable (States > Invulnerable = Hide).</summary>
    public bool InvulnerableHides => _invulnHide;

    /// <summary>A frame with nothing to draw.</summary>
    public void Idle() => LastQuads = LastIcons = 0;

    /// <summary>Starts a frame's batch (Render, main thread).</summary>
    public void Begin()
    {
        _quadCount = 0;
        _textCount = 0;
        _iconCount = 0;
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
        ref var look = ref b.Look;
        var a = b.Alpha * alphaMul * look.Opacity;
        var x1 = x0 + w;
        var y1 = y0 + h;
        var r = Math.Min(_rounding, (int)(h * 0.5f));

        // Under its cull threshold (Notch and tint): the fill and the outline take the cull color, so the whole bar says
        // "one hit".
        var cullable = b.CullAt > 0 && !b.Dying && b.MaxHp > 0 && b.Shown[Bar.Life] <= b.CullAt;
        var outline = cullable && _cullTint ? _cullColor : look.Outline;
        if (_outlineW > 0) Round(x0 - _outlineW, y0 - _outlineW, x1 + _outlineW, y1 + _outlineW, Mul(outline, a), r > 0 ? r + _outlineW : 0, true, true);
        Round(x0, y0, x1, y1, Mul(look.Bg, a), r, true, true);

        var life = look.Life; var es = look.Es; var ward = look.Ward;
        if (cullable && _cullTint) life = _cullColor;
        if (look.Grey) { life = Grey(life); es = Grey(es); ward = Grey(ward); }
        if (_pulse > 0 && b.Kind <= BarKind.Rare && !b.Dying && b.MaxHp > 0 && b.Shown[Bar.Life] * 100 < _pulse)
        {
            var k = 0.35f * (0.5f + 0.5f * MathF.Sin((float)(now * 9.0)));
            life = Lerp(life, 0xFFFFFFFF, k);
        }
        _lifeFill = float.NaN;

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

        // Damage over time still to come: a dark, finely hatched chunk at the life fill's end, as wide as the predicted loss,
        // with a light edge where the fill will end up ("this much is already gone").
        if (_dot && b.DotLossShown > 0 && !float.IsNaN(_lifeFill) && b.Max[Bar.Life] > 0)
        {
            var lw = _lifeRW * MathF.Min(1, b.DotLossShown / b.Max[Bar.Life]);
            var cx0 = MathF.Max(_lifeRX0, MathF.Round(_lifeFill - lw));
            if (_lifeFill - cx0 >= 1)
            {
                Rect(cx0, _lifeY0, _lifeFill, _lifeY1, Mul(_dotColor, a));
                Hatch(cx0, _lifeY0, _lifeFill, _lifeY1, Mul(0xFFFFFFFF, a * 0.16f), StatusAtlas.HatchFine);
                if (cx0 > _lifeRX0 + 1) Rect(cx0, _lifeY0, cx0 + 1, _lifeY1, Mul(Lerp(life, 0xFFFFFFFF, 0.6f), a * 0.9f));
            }
        }
        // Invulnerable (or a patch asking for it): light diagonal stripes across the whole bar, a shield rather than a wound.
        if (look.Hatch) Hatch(x0, y0, x1, y1, Mul(0xFFFFFFFF, a * 0.2f), StatusAtlas.HatchCoarse);

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

        if (_cullNotch && b.CullAt > 0 && b.MaxHp > 0)
        {
            // A notch at the threshold: a bright post standing 2 px proud of the bar above and below, dark-edged so it reads
            // on any fill; in the cull color once the fill is under it.
            var x = MathF.Round(lifeX0 + (lifeX1 - lifeX0) * b.CullAt);
            var nw = h >= 12 ? 2 : 1;
            var c = cullable ? _cullColor : 0xFFFFFFFF;
            Rect(x - 1, y0 - 2 - _outlineW, x + nw + 1, y1 + 2 + _outlineW, Mul(0xFF000000, a * 0.55f));
            Rect(x, y0 - 2 - _outlineW, x + nw, y1 + 2 + _outlineW, Mul(c, a * 0.95f));
        }

        // The sum: all pools against their normal maxima, as a thin line under the bar; past 100% (overfull or a granted
        // pool) the line is full and the excess spills like the pools do.
        var below = y1 + _outlineW;
        var total = b.TotalFraction;
        if (_totalOn && b.Kind <= _totalUpTo && (b.Max[Bar.Es] > 0 || b.Max[Bar.Ward] > 0 || total > 1.001f))
        {
            var th = MathF.Max(2, MathF.Round(2 * _scale));
            Rect(x0, below + 1, x1, below + 1 + th, Mul(look.Bg, a));
            Rect(x0, below + 1, MathF.Round(x0 + w * MathF.Min(1, total)), below + 1 + th, Mul(look.Text, a * 0.9f));
            if (total > 1.001f) Spill(spillX, below + 1, below + 1 + th, w, look.Text, a, total - 1);
            below += 1 + th;
        }

        // Text and icons: only for the main copy of a bar (not the sub-pixel blend copies), and only bars that have them.
        if (!text) return;
        var rightX = spillX + spillMax;
        if (st.ShowPercent || label != null)
        {
            var ty = MathF.Round(y0 + (h - _fontSize) * 0.5f);
            if (st.ShowPercent)
            {
                var pct = Percent[Math.Clamp((int)MathF.Ceiling(total * 100), 0, Percent.Length - 1)];
                var tp = new Vector2(spillX + spillMax + 4, ty);
                Text(tp + Vector2.One, Mul(0xFF000000, a * 0.8f), pct);
                Text(tp, Mul(look.Text, a), pct);
                rightX += 4 + LabelWidth(pct);
            }
            if (label != null) Text(new Vector2(MathF.Round(x0 - LabelWidth(label) - 8 - _outlineW), ty), Mul(look.Text, a), label);
        }

        var above = y0 - _outlineW;
        if (st.HasText)
        {
            var fs = _fontSize * _textScale;
            var cx = x0 + w * 0.5f;
            var k = (int)b.Kind * 3;
            if (Slot(b, 0, _templates[k], fs) is { } ta)
            {
                above -= fs + 1;
                TextAt(new Vector2(MathF.Round(cx - b.SlotWidth[0] * 0.5f), MathF.Round(above)), Mul(look.Name, a), ta, fs);
            }
            if (Slot(b, 1, _templates[k + 1], fs) is { } tn)
                TextAt(new Vector2(MathF.Round(cx - b.SlotWidth[1] * 0.5f), MathF.Round(y0 + (h - fs) * 0.5f)), Mul(look.Text, a), tn, fs);
            if (Slot(b, 2, _templates[k + 2], fs) is { } tb)
            {
                TextAt(new Vector2(MathF.Round(cx - b.SlotWidth[2] * 0.5f), MathF.Round(below + 1)), Mul(look.Text, a), tb, fs);
                below += fs + 1;
            }
        }

        if (b.StatusCount > 0 && b.ShowIcons && _icons && AtlasId != IntPtr.Zero)
        {
            // Debuffs first; buffs at the same anchor start past the debuff block instead of on top of it.
            var left = x0 - _outlineW;
            var used = IconBlock(b, true, _debuffAnchor, left, rightX, y0, above, below, a, now);
            if (_buffAnchor == _debuffAnchor && used > 0)
                switch (_buffAnchor)
                {
                    case 0: above -= used; break;
                    case 1: below += used; break;
                    case 2: left -= used; break;
                    default: rightX += used; break;
                }
            IconBlock(b, false, _buffAnchor, left, rightX, y0, above, below, a, now);
        }
    }

    /// <summary>
    /// A text slot's string: rebuilt only when the values its template reads changed (or the template or font size did);
    /// the width is measured on rebuild. Null for an empty slot.
    /// </summary>
    private string? Slot(Bar b, int slot, TextTemplate t, float fontSize)
    {
        if (t.IsEmpty) return null;
        var sig = t.Signature(b) * 31 + (long)(fontSize * 8);
        if (ReferenceEquals(b.SlotTemplate[slot], t) && b.SlotSig[slot] == sig && b.SlotText[slot] != null) return b.SlotText[slot];
        var s = t.Build(b);
        b.SlotTemplate[slot] = t;
        b.SlotSig[slot] = sig;
        b.SlotText[slot] = s;
        b.SlotWidth[slot] = s.Length == 0 ? 0 : ImGui.CalcTextSize(s).X * fontSize / _fontSize;
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// One block of status icons (debuffs or buffs) in a grid next to the bar: above (rows grow up from the text above),
    /// below, left (right-aligned to the bar) or right (after the percent). Icon size is the setting times each status's
    /// scale; stacks top-right and seconds left underneath, from prebuilt strings.
    /// </summary>
    /// <returns>The block's extent away from the bar (height above/below, width left/right, plus the gap); 0 when empty.</returns>
    private float IconBlock(Bar b, bool debuffs, byte anchor, float left, float rightX, float y0, float above, float below,
        float a, double now)
    {
        var L = Rules;
        var hits = b.Statuses!;
        var n = 0;
        var maxScale = 0f;
        for (var i = 0; i < b.StatusCount && n < _iconMax; i++)
        {
            var rule = hits[i].Rule;
            if ((uint)rule >= (uint)L.Statuses.Length || L.Statuses[rule].Debuff != debuffs) continue;
            n++;
            maxScale = MathF.Max(maxScale, Math.Clamp(L.Statuses[rule].Scale, 0.25f, 4f));
        }
        if (n == 0) return 0;
        // With plates, each cell is the icon plus its plate's 2 px rim; the gap keeps neighbouring rims (or a bare icon's
        // time line, 2 px under it) apart. A stacks pill hangs above its icon, so with stacks on every row gets that
        // much headroom and the block reserves it, instead of the pill landing on the row above or on the bar.
        var rim = _iconPlate ? 2f : 0f;
        var gap = _iconPlate ? 3f : 4f;
        var cell = MathF.Round(_iconSize * maxScale) + 2 * rim;
        var fs = MathF.Max(10, MathF.Round((cell - 2 * rim) * 0.6f));
        var pillH = MathF.Round(fs + 2);
        var head = _showStacks ? pillH - 1 : 0;
        var pitch = cell + gap + head;
        var cols = Math.Min(_iconCols, n);
        var rows = (n + _iconCols - 1) / _iconCols;
        var gridW = cols * cell + (cols - 1) * gap;
        var gridH = rows * pitch - gap;
        float gx, gy; // the grid's top-left (the first row's headroom included)
        switch (anchor)
        {
            case 0: gx = left; gy = above - gap - gridH; break;                          // above
            case 1: gx = left; gy = below + gap; break;                                  // below
            case 2: gx = left - gap - gridW; gy = y0 - head; break;                      // left
            default: gx = rightX + gap + 2; gy = y0 - head; break;                       // right
        }
        var k = 0;
        for (var i = 0; i < b.StatusCount && k < n; i++)
        {
            ref var hit = ref hits[i];
            var rule = hit.Rule;
            if ((uint)rule >= (uint)L.Statuses.Length || L.Statuses[rule].Debuff != debuffs) continue;
            var size = MathF.Round(_iconSize * Math.Clamp(L.Statuses[rule].Scale, 0.25f, 4f));
            var col = k % _iconCols;
            var row = k / _iconCols;
            // Rows above the bar grow upward: the first row sits closest to the bar. Each row's cells start under its headroom.
            var cy = (anchor == 0 ? gy + (rows - 1 - row) * pitch : gy + row * pitch) + head;
            var ix = MathF.Round(gx + col * (cell + gap) + (cell - size) * 0.5f);
            var iy = MathF.Round(cy + (cell - size) * 0.5f);
            var icon = L.StatusIcons[rule];
            var tintFull = L.StatusTints[rule];
            var tint = Mul(tintFull, a);
            if (_iconPlate)
            {
                // The plate: a dark rounded slab under the icon (so a bright glyph reads on sky, snow or fire) with a thin
                // ring in the status's own color; the quads go into the bar batch, under the icons.
                float px0 = ix - rim, py0 = iy - rim, px1 = ix + size + rim, py1 = iy + size + rim;
                Round(px0, py0, px1, py1, Mul(0xFF0E0C12, a * 0.86f), 3, true, true);
                var ring = Mul(tintFull, a * 0.45f);
                Rect(px0 + 1, py0, px1 - 1, py0 + 1, ring);
                Rect(px0 + 1, py1 - 1, px1 - 1, py1, ring);
                Rect(px0, py0 + 1, px0 + 1, py1 - 1, ring);
                Rect(px1 - 1, py0 + 1, px1, py1 - 1, ring);
            }
            switch (L.StatusBadges[rule])
            {
                case 1: // gem: icon baked on its badge
                    StatusAtlas.Uv(icon, 0, out var gu0, out var gv0, out var gu1, out var gv1);
                    Icon(ix, iy, ix + size, iy + size, gu0, gv0, gu1, gv1, tint);
                    break;
                case 2: // over a tintable badge
                    StatusAtlas.BadgeUv(out var bu0, out var bv0, out var bu1, out var bv1);
                    Icon(ix, iy, ix + size, iy + size, bu0, bv0, bu1, bv1, Mul(L.StatusBadgeTints[rule], a));
                    var pad = MathF.Round(size * 0.1f);
                    StatusAtlas.Uv(icon, 1, out var ou0, out var ov0, out var ou1, out var ov1, StatusAtlas.BareInset);
                    Icon(ix + pad, iy + pad, ix + size - pad, iy + size - pad, ou0, ov0, ou1, ov1, tint);
                    break;
                default:
                    StatusAtlas.Uv(icon, 1, out var u0, out var v0, out var u1, out var v1, StatusAtlas.BareInset);
                    Icon(ix, iy, ix + size, iy + size, u0, v0, u1, v1, tint);
                    break;
            }
            // Time left: a thin line just under the icon in the status color (on the plate's rim, or below a bare icon:
            // the bar batch draws before the icon image, so nothing may sit inside the icon's rectangle), full at 8 s
            // and draining to nothing, the same language as the bar; the seconds as text above it when asked for.
            if (hit.Until > 0)
            {
                var remaining = (float)(hit.Until - now);
                if (remaining > 0)
                {
                    var frac = MathF.Min(1, remaining / 8f);
                    var lw = MathF.Max(1, MathF.Round((size - 2) * frac));
                    Rect(ix + 1, iy + size, ix + 1 + lw, iy + size + 2, Mul(tintFull, a * 0.9f));
                    if (_showDuration)
                    {
                        var left10 = Math.Min((int)(remaining * 10), Tenths.Length - 1);
                        var s = Tenths[left10];
                        var tw = LabelWidth(s) * fs / _fontSize;
                        TextAt(new Vector2(MathF.Round(ix + (size - tw) * 0.5f), MathF.Round(iy + size - fs - 1)), Mul(0xFFFFFFFF, a), s, fs);
                    }
                }
            }
            if (_showStacks && hit.Stacks > 1)
            {
                // Stacks in a small dark pill hung off the icon's top-right corner (its bottom one pixel into the icon,
                // the rest above it, since the icon image would cover a pill drawn inside its rectangle), so "5" never
                // vanishes into the glyph; the text comes last of all, over everything.
                var s = Numbers[Math.Min(hit.Stacks, Numbers.Length - 1)];
                var tw = MathF.Round(LabelWidth(s) * fs / _fontSize);
                float sx1 = ix + size + rim, sy1 = iy + 1, sx0 = sx1 - tw - 5;
                Round(sx0, sy1 - pillH, sx1, sy1, Mul(0xFF0E0C12, a * 0.92f), 2, true, true);
                TextAt(new Vector2(sx0 + 3, MathF.Round(sy1 - pillH + 1)), Mul(0xFFFFFFFF, a), s, fs);
            }
            k++;
        }
        return anchor <= 1 ? gap + gridH : gap + gridW;
    }

    private static readonly string[] Numbers = BuildNumbers(1000);
    /// <summary>Seconds left in tenths: "0.1" .. "9.9", then whole seconds.</summary>
    private static readonly string[] Tenths = BuildTenths(1000);

    private static string[] BuildNumbers(int n)
    {
        var a = new string[n];
        for (var i = 0; i < n; i++) a[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }

    private static string[] BuildTenths(int n)
    {
        var a = new string[n];
        for (var i = 0; i < n; i++)
            a[i] = i < 100 ? (i / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : (i / 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }

    /// <summary>
    /// Diagonal stripes across a rectangle (invulnerable bars, the damage-over-time chunk): windows into the atlas's tiling
    /// hatch cell, one textured quad per 64 px of width, drawn 1:1 so the stripes stay crisp and seamless. Without the
    /// atlas, 2 px steps as quads (a 150 x 16 bar is ~220 of them; the textured path is 3).
    /// </summary>
    private void Hatch(float x0, float y0, float x1, float y1, uint col, int pattern)
    {
        var h = y1 - y0;
        // Not in a sub-pixel blend copy: the translucent stripes would stack with the base copy's (the same reason the
        // copies keep opaque quads only).
        if (h < 2 || x1 - x0 < 1 || _opaqueOnly) return;
        if (AtlasId != IntPtr.Zero)
        {
            // Textured stripes go out with the icons, after every bar's quads: a nearer bar overlapping a hatched one is
            // striped across too (the same holds for icons); rare enough that the ~70x fewer quads win.
            for (var y = y0; y < y1; y += StatusAtlas.Cell)
            {
                var ch = MathF.Min(y1 - y, StatusAtlas.Cell);
                for (var x = x0; x < x1; x += StatusAtlas.Cell)
                {
                    var cw = MathF.Min(x1 - x, StatusAtlas.Cell);
                    StatusAtlas.HatchUv(pattern, cw, ch, out var u0, out var v0, out var u1, out var v1);
                    Icon(x, y, x + cw, y + ch, u0, v0, u1, v1, col);
                }
            }
            return;
        }
        var period = pattern == StatusAtlas.HatchFine ? 4 : 8;
        for (var sx = x0 - h; sx < x1; sx += period)
            for (var k = 0; k < h; k += 2)
            {
                float px = sx + k, py = y1 - k - 2;
                var qx0 = MathF.Max(px, x0); var qx1 = MathF.Min(px + 2, x1);
                var qy0 = MathF.Max(py, y0); var qy1 = MathF.Min(py + 2, y1);
                if (qx1 > qx0 && qy1 > qy0) Rect(qx0, qy0, qx1, qy1, col);
            }
    }

    /// <summary>Desaturated and darker: the invulnerable and "grey" patch look.</summary>
    private static uint Grey(uint c)
    {
        var l = (uint)(((c & 0xFF) * 77 + ((c >> 8) & 0xFF) * 150 + ((c >> 16) & 0xFF) * 29) >> 8);
        l = l * 3 / 4;
        return (c & 0xFF000000) | l << 16 | l << 8 | l;
    }

    private static uint PoolColor(int p, uint life, uint es, uint ward) => p == Bar.Life ? life : p == Bar.Es ? es : ward;

    // The life pool's fill edge and region in the bar being drawn (for the damage-over-time chunk); NaN when not drawn.
    private float _lifeFill = float.NaN, _lifeRX0, _lifeRW, _lifeY0, _lifeY1;

    private void LifeRegion(float fill, float rx0, float rw, float y0, float y1)
    {
        _lifeFill = fill; _lifeRX0 = rx0; _lifeRW = rw; _lifeY0 = y0; _lifeY1 = y1;
    }

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
        if (p == Bar.Life) LifeRegion(xf, rx0, rw, ry0, ry1);
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
        if (p == Bar.Life) LifeRegion(xf, x0, w, ly0, ly1);
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
    /// <param name="record">False for a batch outside the frame's own (the settings panel's sample bars): the state's
    /// quad and icon counters keep the frame's numbers.</param>
    public void Flush(ImDrawListPtr dl, bool record = true)
    {
        var icons = AtlasId != IntPtr.Zero ? _iconCount : 0;
        if (record) { LastQuads = _quadCount; LastIcons = icons; }
        if (_quadCount > 0 || icons > 0)
        {
            var why = CheckLayout(dl, _quadCount + icons);
            if (why != null) Fallback(dl, why, record);
            else
            {
                if (record) Path = "batched";
                var uv = ImGui.GetIO().Fonts.TexUvWhitePixel;
                int u = BitConverter.SingleToInt32Bits(uv.X), v = BitConverter.SingleToInt32Bits(uv.Y);
                for (var done = 0; done < _quadCount;)
                {
                    // 8000 quads = 32000 vertices, under the 16-bit index limit of one draw command.
                    var n = Math.Min(_quadCount - done, 8000);
                    var vi = 0;
                    for (var i = 0; i < n; i++)
                    {
                        ref var q = ref _quads[done + i];
                        int x0 = BitConverter.SingleToInt32Bits(q.X0), y0 = BitConverter.SingleToInt32Bits(q.Y0);
                        int x1 = BitConverter.SingleToInt32Bits(q.X1), y1 = BitConverter.SingleToInt32Bits(q.Y1);
                        Vert(ref vi, x0, y0, u, v, q.C0);
                        Vert(ref vi, x1, y0, u, v, q.C1);
                        Vert(ref vi, x1, y1, u, v, q.C2);
                        Vert(ref vi, x0, y1, u, v, q.C3);
                    }
                    Commit(dl, n, vi);
                    done += n;
                }
                if (icons > 0)
                {
                    // The icons: one more draw command, bound to the status atlas instead of the font atlas.
                    dl.PushTextureID(AtlasId);
                    for (var done = 0; done < _iconCount;)
                    {
                        var n = Math.Min(_iconCount - done, 8000);
                        var vi = 0;
                        for (var i = 0; i < n; i++)
                        {
                            ref var q = ref _iconQuads[done + i];
                            int x0 = BitConverter.SingleToInt32Bits(q.X0), y0 = BitConverter.SingleToInt32Bits(q.Y0);
                            int x1 = BitConverter.SingleToInt32Bits(q.X1), y1 = BitConverter.SingleToInt32Bits(q.Y1);
                            int u0 = BitConverter.SingleToInt32Bits(q.U0), v0 = BitConverter.SingleToInt32Bits(q.V0);
                            int u1 = BitConverter.SingleToInt32Bits(q.U1), v1 = BitConverter.SingleToInt32Bits(q.V1);
                            Vert(ref vi, x0, y0, u0, v0, q.Col);
                            Vert(ref vi, x1, y0, u1, v0, q.Col);
                            Vert(ref vi, x1, y1, u1, v1, q.Col);
                            Vert(ref vi, x0, y1, u0, v1, q.Col);
                        }
                        Commit(dl, n, vi);
                        done += n;
                    }
                    dl.PopTextureID();
                }
            }
        }
        var font = ImGui.GetFont();
        for (var i = 0; i < _textCount; i++)
        {
            ref var t = ref _texts[i];
            if (t.Size > 0) dl.AddText(font, t.Size, t.Pos, t.Col, t.Text);
            else dl.AddText(t.Pos, t.Col, t.Text);
            t.Text = null!;
        }
    }

    /// <summary>
    /// Reserves n quads in the draw list and copies the vertices already in <see cref="_vtx"/> plus their indices straight
    /// into its buffers, advancing its write pointers as ImGui's own PrimRect does.
    /// </summary>
    private void Commit(ImDrawListPtr dl, int n, int vi)
    {
        dl.PrimReserve(n * 6, n * 4);
        ref var data = ref dl._Data;
        ref var vtxWrite = ref System.Runtime.CompilerServices.Unsafe.Add(ref data, 2);
        ref var idxWrite = ref System.Runtime.CompilerServices.Unsafe.Add(ref data, 3);
        var idx = dl._VtxCurrentIdx;
        var ii = 0;
        for (var i = 0; i < n; i++)
        {
            _idx[ii++] = (short)idx; _idx[ii++] = (short)(idx + 1); _idx[ii++] = (short)(idx + 2);
            _idx[ii++] = (short)idx; _idx[ii++] = (short)(idx + 2); _idx[ii++] = (short)(idx + 3);
            idx += 4;
        }
        System.Runtime.InteropServices.Marshal.Copy(_vtx, 0, vtxWrite, vi);
        System.Runtime.InteropServices.Marshal.Copy(_idx, 0, idxWrite, ii);
        vtxWrite += vi * 4;
        idxWrite += ii * 2;
        dl._VtxCurrentIdx = idx;
    }

    private long _badVtxAt = long.MinValue, _badVtxSize = -1;
    private string? _badVtxWhy, _fallbackWhy;

    /// <summary>
    /// Null when the draw list's pointer fields are where the batch writer expects them; else why not, naming the field
    /// and the numbers read (the string is built only when they change, so a stuck fallback doesn't allocate per frame).
    /// </summary>
    private string? CheckLayout(ImDrawListPtr dl, int quads)
    {
        ref var data = ref dl._Data;
        var vtxWrite = System.Runtime.CompilerServices.Unsafe.Add(ref data, 2);
        var idxWrite = System.Runtime.CompilerServices.Unsafe.Add(ref data, 3);
        var vb = dl.VtxBuffer;
        if (idxWrite != dl._IdxWritePtr) return "the draw list's _IdxWritePtr is not 3 pointers after _Data (ImGui layout changed)";
        if (vtxWrite != vb.Data + vb.Size * VertBytes)
        {
            var at = (long)vtxWrite - (long)vb.Data;
            if (at != _badVtxAt || vb.Size != _badVtxSize || _badVtxWhy == null)
            {
                _badVtxAt = at; _badVtxSize = vb.Size;
                _badVtxWhy = $"the draw list's _VtxWritePtr is {at} bytes into VtxBuffer, expected its end ({vb.Size} x {VertBytes} = " +
                             $"{(long)vb.Size * VertBytes}): something drew into it without advancing the write pointer, or ImGui's layout changed";
            }
            return _badVtxWhy;
        }
        if ((ImGui.GetIO().BackendFlags & ImGuiBackendFlags.RendererHasVtxOffset) == 0 && dl._VtxCurrentIdx + (long)quads * 4 > ushort.MaxValue)
            // Without VtxOffset the renderer can't start a new 16-bit index range: ImGui's own calls would wrap the
            // indices too. Draw what fits and say so.
            return "the vertices would pass the 16-bit index limit and the renderer has no VtxOffset support";
        return null;
    }

    private void Vert(ref int i, int x, int y, int u, int v, uint col)
    {
        _vtx[i] = x; _vtx[i + 1] = y; _vtx[i + 2] = u; _vtx[i + 3] = v; _vtx[i + 4] = (int)col;
        i += VertInts;
    }

    private void Fallback(ImDrawListPtr dl, string why, bool record)
    {
        if (record && !ReferenceEquals(_fallbackWhy, why)) { _fallbackWhy = why; Path = "fallback: " + why; }
        for (var i = 0; i < _quadCount; i++)
        {
            ref var q = ref _quads[i];
            dl.AddRectFilledMultiColor(new Vector2(q.X0, q.Y0), new Vector2(q.X1, q.Y1), q.C0, q.C1, q.C2, q.C3);
        }
        if (AtlasId == IntPtr.Zero) return;
        for (var i = 0; i < _iconCount; i++)
        {
            ref var q = ref _iconQuads[i];
            dl.AddImage(AtlasId, new Vector2(q.X0, q.Y0), new Vector2(q.X1, q.Y1), new Vector2(q.U0, q.V0), new Vector2(q.U1, q.V1), q.Col);
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

    /// <summary>Text at a font size, with the shadow when it's on.</summary>
    private void TextAt(Vector2 pos, uint col, string text, float size)
    {
        if (_textCount + 2 > _texts.Length) Array.Resize(ref _texts, _texts.Length * 2);
        if (_textShadow) _texts[_textCount++] = new TextItem { Pos = pos + Vector2.One, Col = Mul(0xFF000000, (col >> 24) / 255f * 0.95f), Text = text, Size = size };
        _texts[_textCount++] = new TextItem { Pos = pos, Col = col, Text = text, Size = size };
    }

    private void Icon(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint col)
    {
        if ((col >> 24) == 0) return;
        if (_iconCount == _iconQuads.Length) Array.Resize(ref _iconQuads, _iconQuads.Length * 2);
        ref var q = ref _iconQuads[_iconCount++];
        q.X0 = x0; q.Y0 = y0; q.X1 = x1; q.Y1 = y1; q.U0 = u0; q.V0 = v0; q.U1 = u1; q.V1 = v1; q.Col = col;
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
