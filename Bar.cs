using System;
using System.Numerics;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace WhatsAHealthBar;

public enum BarKind : byte { Unique, Rare, Magic, Normal, Minion, Player, Self }

/// <summary>
/// One tracked entity (or a preview sample when <see cref="Entity"/> is null). Components are looked up once and held;
/// classification (kind, wanted, height, which pools exist) runs at the classify rate, position, pools and animation every
/// frame for wanted bars only.
///
/// Pools are independent: life, energy shield and ward each have their own current/max and their own animation (fill,
/// trail, hit flash, regen glow), because they move independently in game: ES recharges while life stays low, ward
/// restores on its own timer, life regenerates or is flasked.
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

    // Frame data. ES and ward current values are read per frame only when the monster has them (their max is read at the
    // classify rate).
    public Vector3 World;
    public Vector2 Screen;
    public readonly int[] Cur = new int[PoolCount];
    public readonly int[] Max = new int[PoolCount];
    public bool Seen;
    private int _lastTotal = -1;

    // Animation per pool (fractions of that pool's own max), plus the bar's opacity.
    public readonly float[] Shown = new float[PoolCount], Trail = new float[PoolCount];
    public readonly float[] Flash = new float[PoolCount], Glow = new float[PoolCount];
    private readonly double[] _hold = new double[PoolCount];
    public float Alpha;

    // Death and kill timing
    public bool Dying;
    public double FirstHitAt;
    public bool Published;

    public int Hp => Cur[Life];
    public int MaxHp => Max[Life];
    public int MaxTotal => Max[Life] + Max[Es] + Max[Ward];
    public int Total => Cur[Life] + Cur[Es] + Cur[Ward];

    public float Fraction(int pool) => Max[pool] > 0 ? Math.Clamp(Cur[pool] / (float)Max[pool], 0, 1) : 0;

    /// <summary>The sum of the pools over the sum of their maxima (what the total line and the percent show).</summary>
    public float TotalFraction => MaxTotal > 0 ? Math.Clamp(Total / (float)MaxTotal, 0, 1) : 0;

    /// <summary>Notes the first hit (any pool going down) for time to kill; ward or ES building up after spawn is not a hit.</summary>
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
    /// Moves each pool's animated values toward its current fraction. A drop is instant (crisp) and leaves a trail chip that
    /// holds, then drains; a rise (regen, recharge, flask, ward restore) fills smoothly with a glow. Animations off: values jump.
    /// </summary>
    public void Animate(double now, float dt, in AnimParams p)
    {
        var first = !Seen;
        if (first)
        {
            Seen = true;
            Alpha = p.Enabled && p.FadeRate > 0 ? 0 : 1;
        }

        var holdEnd = 0.0;
        for (var i = 0; i < PoolCount; i++)
        {
            var target = Dying ? 0 : Fraction(i);
            if (first || !p.Enabled)
            {
                Shown[i] = Trail[i] = target;
                Flash[i] = Glow[i] = 0;
                continue;
            }
            if (target < Shown[i] - 0.0005f)
            {
                if (Trail[i] < Shown[i]) Trail[i] = Shown[i];
                Shown[i] = target;
                _hold[i] = now + p.TrailHold;
                if (p.HitFlash) Flash[i] = 1;
            }
            else if (target > Shown[i] + 0.0005f)
            {
                Shown[i] += (target - Shown[i]) * Ease(p.HealRate, dt);
                if (target - Shown[i] < 0.002f) Shown[i] = target;
                if (p.HealGlow) Glow[i] = 1;
            }
            if (!p.Trail) Trail[i] = Shown[i];
            else if (now >= _hold[i])
            {
                Trail[i] += (Shown[i] - Trail[i]) * Ease(p.TrailDrain, dt);
                if (Trail[i] - Shown[i] < 0.002f) Trail[i] = Shown[i];
            }
            if (Trail[i] < Shown[i]) Trail[i] = Shown[i];
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

public readonly record struct AnimParams(bool Enabled, bool Trail, double TrailHold, float TrailDrain, bool HitFlash, bool HealGlow,
    float HealRate, float FadeRate);
