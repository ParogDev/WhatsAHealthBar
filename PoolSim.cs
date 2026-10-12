using System;

namespace WhatsAHealthBar;

/// <summary>
/// Drives a preview bar the way pools move in game, each on its own: hits take ward, then energy shield, then life; ES
/// starts recharging after a moment without hits while life stays where it is; ward restores on a timer after it breaks;
/// life regenerates slowly and gets an occasional flask. So the preview shows ES refilling over a low life bar, a broken
/// ward coming back, and all the in-between states.
/// </summary>
public sealed class PoolSim
{
    public readonly Bar Bar;
    private float _life, _es, _ward;
    private double _nextHit, _lastHit = -10, _wardBroke = -1, _lastFlask = -10;

    private const double EsRechargeDelay = 1.6, WardRestoreDelay = 2.4, FlaskCooldown = 3.5;
    private const float EsRechargePerSec = 0.45f, LifeRegenPerSec = 0.025f;

    public PoolSim(Bar bar, int life, int es, int ward, double now, Random rng)
    {
        Bar = bar;
        bar.Max[Bar.Life] = life; bar.Max[Bar.Es] = es; bar.Max[Bar.Ward] = ward;
        _life = life; _es = es; _ward = ward;
        _nextHit = now + rng.NextDouble() * 1.2;
        Write();
    }

    public void Step(double now, float dt, Random rng)
    {
        var b = Bar;
        if (now >= _nextHit)
        {
            var total = b.MaxTotal;
            var dmg = total * (float)(rng.Next(10) == 0 ? 0.25 + rng.NextDouble() * 0.15 : 0.04 + rng.NextDouble() * 0.12);
            var w = MathF.Min(_ward, dmg); _ward -= w; dmg -= w;
            if (w > 0 && _ward <= 0) _wardBroke = now;
            var e = MathF.Min(_es, dmg); _es -= e; dmg -= e;
            _life = MathF.Max(1, _life - dmg);
            _lastHit = now;
            // Bursts of hits, then pauses long enough for ES to start recharging.
            _nextHit = now + (rng.Next(4) == 0 ? 2.2 + rng.NextDouble() * 1.5 : 0.35 + rng.NextDouble() * 0.6);
        }

        var maxLife = b.Max[Bar.Life];
        var maxEs = b.Max[Bar.Es];
        var maxWard = b.Max[Bar.Ward];
        if (maxEs > 0 && now - _lastHit > EsRechargeDelay) _es = MathF.Min(maxEs, _es + maxEs * EsRechargePerSec * dt);
        if (maxWard > 0 && _wardBroke >= 0 && now - _wardBroke > WardRestoreDelay) { _ward = maxWard; _wardBroke = -1; }
        _life = MathF.Min(maxLife, _life + maxLife * LifeRegenPerSec * dt);
        if (_life < maxLife * 0.25f && now - _lastFlask > FlaskCooldown)
        {
            _life = MathF.Min(maxLife, _life + maxLife * 0.6f);
            _lastFlask = now;
        }
        Write();
    }

    private void Write()
    {
        Bar.Cur[Bar.Life] = (int)_life;
        Bar.Cur[Bar.Es] = (int)_es;
        Bar.Cur[Bar.Ward] = (int)_ward;
    }
}
