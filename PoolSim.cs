using System;

namespace WhatsAHealthBar;

/// <summary>
/// Drives a preview bar the way pools move in game, each on its own, including the cases that trip naive displays:
/// - hits take ward, then energy shield, then life;
/// - ES recharges after a moment without hits while life stays low; ward restores on a timer after it breaks;
/// - life regenerates slowly and gets an occasional flask;
/// - every few seconds an event: a pool goes over its max (and decays back to it), a pool the monster doesn't have is granted
///   (ward or ES with no max: drawn as a veil, used up first), or max life is buffed for a while without current life
///   changing (the fill drops quietly: not a hit).
/// </summary>
public sealed class PoolSim
{
    public readonly Bar Bar;
    private readonly int _baseLife, _baseEs, _baseWard;
    private int _maxLife, _maxEs, _maxWard;
    private float _life, _es, _ward;
    private double _nextHit, _lastHit = -10, _wardBroke = -1, _lastFlask = -10, _nextEvent, _buffUntil;

    private const double EsRechargeDelay = 1.6, WardRestoreDelay = 2.4, FlaskCooldown = 3.5;
    private const float EsRechargePerSec = 0.45f, LifeRegenPerSec = 0.025f, OverDecayPerSec = 0.12f;

    public PoolSim(Bar bar, int life, int es, int ward, double now, Random rng)
    {
        Bar = bar;
        _maxLife = _baseLife = life; _maxEs = _baseEs = es; _maxWard = _baseWard = ward;
        _life = life; _es = es; _ward = ward;
        _nextHit = now + rng.NextDouble() * 1.2;
        _nextEvent = now + 3 + rng.NextDouble() * 5;
        Write();
    }

    public void Step(double now, float dt, Random rng)
    {
        if (now >= _nextEvent) Event(now, rng);
        if (_buffUntil > 0 && now >= _buffUntil) { _maxLife = _baseLife; _buffUntil = 0; _life = MathF.Min(_life, _maxLife); }

        if (now >= _nextHit)
        {
            var total = _baseLife + _baseEs + _baseWard;
            var dmg = total * (float)(rng.Next(10) == 0 ? 0.25 + rng.NextDouble() * 0.15 : 0.04 + rng.NextDouble() * 0.12);
            var w = MathF.Min(_ward, dmg); _ward -= w; dmg -= w;
            if (w > 0 && _ward <= 0 && _maxWard > 0) _wardBroke = now;
            var e = MathF.Min(_es, dmg); _es -= e; dmg -= e;
            _life = MathF.Max(1, _life - dmg);
            _lastHit = now;
            // Bursts of hits, then pauses long enough for ES to start recharging.
            _nextHit = now + (rng.Next(4) == 0 ? 2.2 + rng.NextDouble() * 1.5 : 0.35 + rng.NextDouble() * 0.6);
        }

        // Over max decays back toward it; under max regenerates by the pool's rules. A granted pool (no max) only drains.
        _es = Settle(_es, _maxEs, _maxEs > 0 && now - _lastHit > EsRechargeDelay ? _maxEs * EsRechargePerSec : 0, dt);
        _ward = Settle(_ward, _maxWard, 0, dt);
        if (_maxWard > 0 && _wardBroke >= 0 && now - _wardBroke > WardRestoreDelay) { _ward = MathF.Max(_ward, _maxWard); _wardBroke = -1; }
        _life = Settle(_life, _maxLife, _maxLife * LifeRegenPerSec, dt);
        if (_life < _maxLife * 0.25f && now - _lastFlask > FlaskCooldown)
        {
            _life = MathF.Min(_maxLife, _life + _maxLife * 0.6f);
            _lastFlask = now;
        }
        Write();
    }

    private void Event(double now, Random rng)
    {
        _nextEvent = now + 6 + rng.NextDouble() * 5;
        switch (rng.Next(3))
        {
            case 0: // overcap: the largest pool goes 40-120% over its max
                if (_maxEs >= _maxLife && _maxEs > 0) _es = _maxEs * (1.4f + (float)rng.NextDouble() * 0.8f);
                else _life = _maxLife * (1.4f + (float)rng.NextDouble() * 0.8f);
                break;
            case 1: // a pool it doesn't have is granted (no max): ward if it has none, else ES if it has none, else overcap ward
                var size = (_baseLife + _baseEs + _baseWard) * (0.25f + (float)rng.NextDouble() * 0.5f);
                if (_maxWard == 0) _ward += size;
                else if (_maxEs == 0) _es += size;
                else _ward = _maxWard * 1.6f;
                break;
            default: // max life buffed for 4 s; current life unchanged
                _maxLife = (int)(_baseLife * 1.3f);
                _buffUntil = now + 4;
                break;
        }
    }

    /// <summary>Above max: decays toward it (to 0 for a pool without a max). Below: regenerates at <paramref name="regen"/>.</summary>
    private static float Settle(float cur, int max, float regen, float dt)
    {
        if (cur > max) return MathF.Max(max, cur - MathF.Max(max, 1) * OverDecayPerSec * dt - (max == 0 ? cur * 0.08f * dt : 0));
        return MathF.Min(max, cur + regen * dt);
    }

    private void Write()
    {
        Bar.SetPool(Bar.Life, (int)_life, _maxLife);
        Bar.SetPool(Bar.Es, (int)_es, _maxEs);
        Bar.SetPool(Bar.Ward, (int)_ward, _maxWard);
    }
}
