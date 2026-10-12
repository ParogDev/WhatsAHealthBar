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
/// </summary>
public sealed class Bar
{
    public Entity? Entity;
    public uint Id;
    /// <summary>Position in the plugin's bar list (swap-remove).</summary>
    public int Index;
    public Life? Life;
    public Render? Render;

    // Classification (classify rate)
    public BarKind Kind;
    public bool Wanted;
    public float TopZ;

    // Frame data. Pools: life, energy shield and ward (current and max); ES and ward are read per frame only when the
    // monster has them (their max is read at the classify rate).
    public Vector3 World;
    public Vector2 Screen;
    public int Hp, MaxHp, Es, MaxEs, Ward, MaxWard;
    public bool Seen;

    // Animation, as a fraction of the bar's whole width (see Fraction)
    public float Shown, Trail, Flash, Glow, Alpha;
    public double TrailHoldUntil;

    // Death and kill timing
    public bool Dying;
    public double FirstHitAt;
    public bool Published;

    public float LifeFraction => MaxHp > 0 ? Math.Clamp(Hp / (float)MaxHp, 0, 1) : 0;

    /// <summary>The bar's width in pool points: life alone, or life + ES + ward when pools are stacked.</summary>
    public int MaxTotal(bool stacked) => stacked ? MaxHp + MaxEs + MaxWard : MaxHp;

    /// <summary>How full the bar is: (life + ES + ward) / their maxima when stacked, else life alone.</summary>
    public float Fraction(bool stacked)
    {
        var max = MaxTotal(stacked);
        if (max <= 0) return 0;
        var cur = stacked ? Hp + Es + Ward : Hp;
        return Math.Clamp(cur / (float)max, 0, 1);
    }

    /// <summary>
    /// Moves the animated values toward <paramref name="target"/>. A hit drops the fill at once (crisp) and leaves a trail
    /// chip that holds, then drains; a heal (or ES recharge) fills smoothly with a glow. Animations off: values jump.
    /// </summary>
    public void Animate(float target, double now, float dt, in AnimParams p)
    {
        if (!Seen)
        {
            Seen = true;
            Shown = Trail = target;
            Alpha = p.Enabled && p.FadeRate > 0 ? 0 : 1;
        }

        if (!p.Enabled)
        {
            Shown = Trail = target;
            Flash = Glow = 0;
            Alpha = Dying ? 0 : 1;
            return;
        }

        if (target < Shown - 0.0005f)
        {
            if (Trail < Shown) Trail = Shown;
            Shown = target;
            TrailHoldUntil = now + p.TrailHold;
            if (p.HitFlash) Flash = 1;
        }
        else if (target > Shown + 0.0005f)
        {
            Shown += (target - Shown) * Ease(p.HealRate, dt);
            if (target - Shown < 0.002f) Shown = target;
            if (p.HealGlow) Glow = 1;
        }

        if (!p.Trail) Trail = Shown;
        else if (now >= TrailHoldUntil)
        {
            Trail += (Shown - Trail) * Ease(p.TrailDrain, dt);
            if (Trail - Shown < 0.002f) Trail = Shown;
        }
        if (Trail < Shown) Trail = Shown;

        if (Flash > 0) Flash = MathF.Max(0, Flash - dt * 7f);
        if (Glow > 0) Glow = MathF.Max(0, Glow - dt * 2.2f);

        var fadeTo = Wanted && !Dying ? 1f : 0f;
        // A dying bar keeps full opacity while its trail drains, then fades.
        if (Dying && now < TrailHoldUntil + 0.15) fadeTo = 1f;
        Alpha = p.FadeRate <= 0 ? fadeTo : MoveTo(Alpha, fadeTo, p.FadeRate * dt);
    }

    private static float Ease(float rate, float dt) => 1f - MathF.Exp(-rate * dt);
    private static float MoveTo(float v, float to, float step) => v < to ? MathF.Min(to, v + step) : MathF.Max(to, v - step);
}

public readonly record struct AnimParams(bool Enabled, bool Trail, double TrailHold, float TrailDrain, bool HitFlash, bool HealGlow,
    float HealRate, float FadeRate);
