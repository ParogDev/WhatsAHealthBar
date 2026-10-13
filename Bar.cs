using System;
using System.Numerics;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace WhatsAHealthBar;

public enum BarKind : byte { Unique, Rare, Magic, Normal, Minion, Player, Self }

/// <summary>
/// One tracked entity (or a preview sample when <see cref="Entity"/> is null). Components are looked up again at the classify rate (a boss phase change swaps them);
/// classification (kind, wanted, height, which pools exist) runs at the classify rate; position, pools and animation every
/// frame for wanted bars only.
///
/// Pools (life, energy shield, ward) are independent: each has its own current, max and animation, because they move on
/// their own in game. The cases that make a naive display lie, and what this does about each:
/// - Max goes up (a buff) or down (a debuff) while current stays: the fraction moves but nothing was hit or healed. Hits
///   and heals are judged on the raw current value, so a max change eases the fill without trail, flash or glow, and
///   doesn't start the time-to-kill clock.
/// - Current above max (overfull): the fill is full and the excess is kept as an over ratio, shown as a veil spilling past
///   the bar's end, faint for a little and bolder for more.
/// - A pool with no max but a current value (a granted shield): it has no 100%, so its high-water mark stands in for one,
///   and it is drawn as a veil layer, bolder the larger it is next to the other pools. The mark resets when it runs out.
/// - Pools appearing or vanishing: layer shares animate instead of jumping.
/// - Reserved life or ES (players): the full mark is the unreserved max.
/// - Current and max are read together from one struct each frame, so they never mix two moments.
/// </summary>
public sealed class Bar
{
    public const int Life = 0, Es = 1, Ward = 2, PoolCount = 3;

    public Entity? Entity;
    public uint Id;
    /// <summary>Position in the plugin's bar list (swap-remove).</summary>
    public int Index;
    public Life? LifeComp;
    public Render? Render;

    // Classification (classify rate)
    public BarKind Kind;
    public bool Wanted;
    public float TopZ;
    /// <summary>Which pools are read per frame (present now, or seen this classify pass).</summary>
    public readonly bool[] Tracked = { true, false, false };

    // Frame data
    public Vector3 World;
    public Vector2 Screen;

    // Motion (see Motion.cs): the projection at the last three game frames (oldest first), drawn at a render time between
    // them; the last raw and shown positions for the jitter measurement; the whole-pixel origin a bar at rest keeps
    // (hysteresis) and when it last moved.
    public Vector2 S0, S1, S2, Raw1, Raw2, Shown1, Shown2;
    public int Samples;
    public long MotionFrame = -10;
    public float RestX = float.NaN, RestY = float.NaN;
    public double MovedAt = double.NegativeInfinity, SpeedAt;
    public float Speed;
    public readonly int[] Cur = new int[PoolCount];
    /// <summary>The game's (unreserved) max; 0 when the pool has none.</summary>
    public readonly int[] BaseMax = new int[PoolCount];
    /// <summary>The 100% mark used for the fill: the base max, or the high-water mark of a pool without one.</summary>
    public readonly int[] Max = new int[PoolCount];
    /// <summary>A pool without a base max that has a value (granted): drawn as a veil.</summary>
    public readonly bool[] Temp = new bool[PoolCount];
    private readonly int[] _peak = new int[PoolCount];
    private readonly int[] _lastCur = { -1, -1, -1 };
    public bool Seen;
    private int _lastTotal = -1;

    // Animation per pool: fill and trail (fractions of Max), hit flash, regen glow, overfull veil (over ratio), and the
    // layer's share of the bar (sandwich thickness / side-by-side width).
    public readonly float[] Shown = new float[PoolCount], Trail = new float[PoolCount];
    public readonly float[] Flash = new float[PoolCount], Glow = new float[PoolCount];
    public readonly float[] Veil = new float[PoolCount], Share = new float[PoolCount];
    private readonly double[] _hold = new double[PoolCount];
    public float Alpha;

    // Death and kill timing
    public bool Dying;
    public double FirstHitAt;
    public bool Published;

    // ---- Information layers (classify rate unless noted) ----
    public Stats? StatsComp;
    public Buffs? BuffsComp;
    /// <summary>Metadata path without "@level" (overrides, ignore list) and the shown name (abbreviated), read once.</summary>
    public string PathKey = "", DisplayName = "";
    /// <summary>Bumped when <see cref="DisplayName"/> changes (the {name} token's signature).</summary>
    public int NameVersion;
    public int NameThreshold = -1;
    /// <summary>RenderName was empty: the path's tail stands in and the name is read again next pass.</summary>
    public bool NameFromPath;
    /// <summary>Plugin clock time the bar was last added to the draw list (WhyNotDrawn's drawn flag).</summary>
    public double DrawnAt = double.NegativeInfinity;
    /// <summary>The override that matched (-1: none), valid for <see cref="OverrideVersion"/> of the compiled layers.</summary>
    public int OverrideIndex = -1, OverrideVersion = -1;
    /// <summary>Kind look, then override, status and state patches.</summary>
    public BarLook Look;
    public bool Invulnerable, CannotBeCulled;
    /// <summary>Life fraction at or under which culling strike kills this monster (0: no threshold).</summary>
    public float CullAt;
    /// <summary>Grid distance from the player at the last classify pass.</summary>
    public float Dist;
    /// <summary>Why the bar is or isn't drawn, from the last classify pass (or the frame for off-screen / culled-hidden).</summary>
    public Why Why = Why.NotClassified;

    /// <summary>Statuses found at the last classify pass (icons), up to the array's length.</summary>
    public StatusHit[]? Statuses;
    public int StatusCount;
    /// <summary>The kind shows icons (else the statuses were read for their patches only).</summary>
    public bool ShowIcons;
    /// <summary>When stats and buffs were last read (staggered for magic and normal monsters); -1: never.</summary>
    public double LayersReadAt = -1;

    /// <summary>Damage over time: pool loss per second and when the longest running DoT ends (per frame: <see cref="DotLossShown"/>).</summary>
    public float DotRate, DotLossShown;
    public double DotUntil;
    /// <summary>The rate came from the ES-first stat: current ES absorbs that much of the predicted loss before life.</summary>
    public bool DotThroughEs;

    /// <summary>Damage per second on all pools (an EMA) and the time to kill at that rate (-1: no damage).</summary>
    public float Dps, Ttk = -1;
    private int _dpsLastTotal = -1;
    private double _dpsLastT;

    /// <summary>Data text slots (above, on, below): the last string, its signature, width and the template it came from.</summary>
    public readonly string?[] SlotText = new string?[3];
    public readonly long[] SlotSig = new long[3];
    public readonly float[] SlotWidth = new float[3];
    public readonly TextTemplate?[] SlotTemplate = new TextTemplate?[3];

    /// <summary>EMA of the combined pool drop, updated at most every 0.1 s (syrairc's DpsMeter), and the time to kill.</summary>
    public void UpdateDps(double now, float tau)
    {
        var total = Total;
        if (_dpsLastTotal < 0) { _dpsLastTotal = total; _dpsLastT = now; return; }
        var dt = now - _dpsLastT;
        if (dt < 0.1) return;
        var drop = _dpsLastTotal - total;
        var rate = drop > 0 ? (float)(drop / dt) : 0f;
        Dps += (float)((1 - Math.Exp(-dt / Math.Max(0.05f, tau))) * (rate - Dps));
        if (Dps < 1) Dps = 0;
        Ttk = Dps > 0 && total > 0 ? total / Dps : -1;
        _dpsLastTotal = total;
        _dpsLastT = now;
    }

    public int Hp => Cur[Life];
    public int MaxHp => BaseMax[Life];
    public int MaxTotal => Max[Life] + Max[Es] + Max[Ward];
    public int BaseTotal => BaseMax[Life] + BaseMax[Es] + BaseMax[Ward];
    public int Total => Cur[Life] + Cur[Es] + Cur[Ward];

    /// <summary>Sets one pool from the game's current and (unreserved) max. False when the values are implausible (a moved offset).</summary>
    public bool SetPool(int p, int cur, int baseMax)
    {
        if (cur < 0 || baseMax < 0 || cur > 100_000_000 || baseMax > 100_000_000) return false;
        Cur[p] = cur;
        BaseMax[p] = baseMax;
        if (baseMax > 0) { Temp[p] = false; Max[p] = baseMax; _peak[p] = 0; }
        else if (cur > 0) { Temp[p] = true; _peak[p] = Math.Max(_peak[p], cur); Max[p] = _peak[p]; }
        else { Temp[p] = false; Max[p] = 0; _peak[p] = 0; }
        return true;
    }

    public void ClearPool(int p)
    {
        Cur[p] = BaseMax[p] = Max[p] = _peak[p] = 0;
        Temp[p] = false;
    }

    public float Fraction(int pool) => Max[pool] > 0 ? Math.Clamp(Cur[pool] / (float)Max[pool], 0, 1) : 0;

    /// <summary>
    /// How far over full a pool is: (cur - max) / max for a pool with a base max; for a granted pool, its size next to the
    /// other pools' base total (or 1 when it is the only pool). 0 when not over.
    /// </summary>
    public float Over(int p)
    {
        if (Temp[p])
        {
            var others = BaseTotal;
            return others > 0 ? Cur[p] / (float)others : 1;
        }
        return BaseMax[p] > 0 && Cur[p] > BaseMax[p] ? (Cur[p] - BaseMax[p]) / (float)BaseMax[p] : 0;
    }

    /// <summary>All pools against their normal maxima: can pass 1 when overfull or granted (the percent and total line).</summary>
    public float TotalFraction
    {
        get
        {
            var baseTotal = BaseTotal;
            if (baseTotal > 0) return Total / (float)baseTotal;
            return MaxTotal > 0 ? Total / (float)MaxTotal : 0;
        }
    }

    /// <summary>Notes the first hit (any pool's current going down) for time to kill; pools building up after spawn are not a hit.</summary>
    public void TrackHit(double now)
    {
        var total = Total;
        if (FirstHitAt == 0 && _lastTotal >= 0 && total < _lastTotal) FirstHitAt = now;
        _lastTotal = total;
    }

    public void Zero()
    {
        Array.Clear(Cur);
        _lastTotal = 0;
    }

    /// <summary>
    /// Moves each pool's animated values. A drop of the current value is instant (crisp) and leaves a trail chip that holds,
    /// then drains; a rise (regen, recharge, flask, ward restore) fills smoothly with a glow. A fraction change from the max
    /// moving eases quietly. The overfull veil fades fast and builds slower; layer shares glide. Animations off: values jump.
    /// </summary>
    public void Animate(double now, float dt, in AnimParams p)
    {
        var first = !Seen;
        if (first)
        {
            Seen = true;
            Alpha = p.Enabled && p.FadeRate > 0 ? 0 : 1;
        }

        var maxTotal = MaxTotal;
        var holdEnd = 0.0;
        for (var i = 0; i < PoolCount; i++)
        {
            var target = Dying ? 0 : Fraction(i);
            var over = Dying ? 0 : Over(i);
            var share = maxTotal > 0 ? Max[i] / (float)maxTotal : 0;
            var dCur = _lastCur[i] < 0 ? 0 : Cur[i] - _lastCur[i];
            _lastCur[i] = Cur[i];
            if (first || !p.Enabled)
            {
                Shown[i] = Trail[i] = target;
                Veil[i] = over;
                Share[i] = share;
                Flash[i] = Glow[i] = 0;
                continue;
            }

            if (target < Shown[i] - 0.0005f)
            {
                if (dCur < 0 || Dying)
                {
                    if (Trail[i] < Shown[i]) Trail[i] = Shown[i];
                    Shown[i] = target;
                    _hold[i] = now + p.TrailHold;
                    if (p.HitFlash) Flash[i] = 1;
                }
                else Shown[i] += (target - Shown[i]) * Ease(12f, dt); // the max went up: not a hit
            }
            else if (target > Shown[i] + 0.0005f)
            {
                Shown[i] += (target - Shown[i]) * Ease(p.HealRate, dt);
                if (target - Shown[i] < 0.002f) Shown[i] = target;
                if (p.HealGlow && dCur > 0) Glow[i] = 1; // the max went down: no glow
            }

            if (!p.Trail) Trail[i] = Shown[i];
            else if (now >= _hold[i])
            {
                Trail[i] += (Shown[i] - Trail[i]) * Ease(p.TrailDrain, dt);
                if (Trail[i] - Shown[i] < 0.002f) Trail[i] = Shown[i];
            }
            if (Trail[i] < Shown[i]) Trail[i] = Shown[i];

            Veil[i] += (over - Veil[i]) * Ease(over < Veil[i] ? 20f : 6f, dt);
            if (MathF.Abs(over - Veil[i]) < 0.002f) Veil[i] = over;
            Share[i] += (share - Share[i]) * Ease(10f, dt);
            if (MathF.Abs(share - Share[i]) < 0.002f) Share[i] = share;

            if (Flash[i] > 0) Flash[i] = MathF.Max(0, Flash[i] - dt * 7f);
            if (Glow[i] > 0) Glow[i] = MathF.Max(0, Glow[i] - dt * 2.2f);
            holdEnd = Math.Max(holdEnd, _hold[i]);
        }

        if (!p.Enabled) { Alpha = Dying ? 0 : 1; return; }
        var fadeTo = Wanted && !Dying ? 1f : 0f;
        // A dying bar keeps full opacity while its trails drain, then fades.
        if (Dying && now < holdEnd + 0.15) fadeTo = 1f;
        Alpha = p.FadeRate <= 0 ? fadeTo : MoveTo(Alpha, fadeTo, p.FadeRate * dt);
    }

    private static float Ease(float rate, float dt) => 1f - MathF.Exp(-rate * dt);
    private static float MoveTo(float v, float to, float step) => v < to ? MathF.Min(to, v + step) : MathF.Max(to, v - step);
}

/// <summary>One status icon on a bar: the rule (index into the compiled list), stacks summed over its instances, and when the longest ends.</summary>
public struct StatusHit
{
    public int Rule;
    public int Stacks;
    /// <summary>Plugin clock time the status runs out; 0 when it has no timer.</summary>
    public double Until;
}

/// <summary>Why a bar is or isn't drawn (the debug overlay and WhatsAHealthBar.WhyNotDrawn).</summary>
public enum Why : byte
{
    Drawn, NotClassified, Invalid, Dead, NoLifeComponent, NoRenderComponent, Hidden, Ignored, Untargetable, KindHidden,
    TooFar, MinionNotHurt, NormalNotHit, OverrideHidden, StatusHidden, Invulnerable, Cullable, OffScreen, NoPools,
}

public readonly record struct AnimParams(bool Enabled, bool Trail, double TrailHold, float TrailDrain, bool HitFlash, bool HealGlow,
    float HealRate, float FadeRate);
