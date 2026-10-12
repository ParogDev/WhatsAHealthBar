using System;
using System.Collections.Generic;
using System.Numerics;

namespace WhatsAHealthBar;

/// <summary>
/// Smooth motion for overlay bars. The overlay is a separate window on its own clock: each HUD frame reads whatever game
/// frame was last written, so when the two rates differ, some HUD frames show the same game frame twice and others skip
/// one. Every position is right, yet a moving bar stutters (the game's own bars, drawn in its frame, can't).
///
/// Playback buffer: each HUD frame checks whether the game advanced (the camera matrix changed, or any bar's world
/// position did) and timestamps it. Each bar keeps its projection at the last three game frames, and is drawn at a render
/// time <see cref="DelayFrames"/> game frames behind now, between the two samples around it. Drawing between real samples
/// (rather than toward the newest one and holding there when the next is a little late) gives evenly spaced motion, at a
/// constant lag (constant lag doesn't read as jitter). Big jumps (a teleport, a bar reappearing) snap.
///
/// Jitter measurement: the second difference of each moving bar's screen position (how unevenly it moves from one HUD
/// frame to the next), raw against shown, averaged over the frame. The state reports both, so the gain is measured.
/// </summary>
public sealed class Motion
{
    /// <summary>How many game frames behind the newest one bars are drawn.</summary>
    public const float DelayFrames = 1.5f;
    private const float SnapPx = 160f;

    private Matrix4x4 _lastMatrix;
    private readonly double[] _t = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity }; // advances, oldest first
    private float _interval = 1 / 60f;
    private int _framesSinceAdvance;

    /// <summary>The measured game frame interval (seconds).</summary>
    public float Interval => _interval;
    /// <summary>The constant lag of interpolated bars (seconds).</summary>
    public float Lag => _interval * DelayFrames;
    /// <summary>Average per-bar second difference over the last frames, px: raw projection and what is drawn.</summary>
    public float JitterRaw { get; private set; }
    public float JitterShown { get; private set; }
    /// <summary>HUD frames per detected game advance (above 1: the HUD runs faster than the game, the case that stutters).</summary>
    public float FramesPerAdvance { get; private set; } = 1;

    /// <summary>
    /// Call once per HUD frame after every visible bar has its raw projection in <see cref="Bar.Screen"/>: decides whether
    /// the game advanced, updates each bar's samples, and replaces Screen with the position at the render time.
    /// </summary>
    public void Apply(List<Bar> bars, Matrix4x4 camera, bool[] worldMoved, double now, bool smooth)
    {
        var advanced = !camera.Equals(_lastMatrix);
        _lastMatrix = camera;
        for (var i = 0; i < bars.Count && !advanced; i++) advanced = worldMoved[i];

        _framesSinceAdvance++;
        if (advanced)
        {
            var gap = now - _t[2];
            // Game frames are 3-50 ms apart; longer gaps are pauses (nothing moved), not a frame rate.
            if (gap is > 0.002 and < 0.05) _interval += ((float)gap - _interval) * 0.1f;
            FramesPerAdvance += (_framesSinceAdvance - FramesPerAdvance) * 0.05f;
            _framesSinceAdvance = 0;
            _t[0] = _t[1]; _t[1] = _t[2]; _t[2] = now;
        }

        // The render time, and where it falls between the last three advances.
        var render = now - Lag;
        int seg; float u;
        if (render >= _t[1]) { seg = 1; u = Frac(render, _t[1], _t[2]); }
        else { seg = 0; u = Frac(render, _t[0], _t[1]); }

        float rawSum = 0, shownSum = 0; var moving = 0;
        foreach (var b in bars)
        {
            var raw = b.Screen;
            if (b.Samples == 0 || Vector2.DistanceSquared(raw, b.S2) > SnapPx * SnapPx)
            {
                b.S0 = b.S1 = b.S2 = raw;
                b.Samples = 1;
            }
            else if (advanced)
            {
                b.S0 = b.S1; b.S1 = b.S2; b.S2 = raw;
                b.Samples++;
            }
            var shown = !smooth ? raw : seg == 1 ? Vector2.Lerp(b.S1, b.S2, u) : Vector2.Lerp(b.S0, b.S1, u);

            if (b.Samples >= 3)
            {
                var dRaw = (raw - 2 * b.Raw1 + b.Raw2).Length();
                var dShown = (shown - 2 * b.Shown1 + b.Shown2).Length();
                if ((raw - b.Raw1).LengthSquared() > 0.01f || dRaw > 0.1f) { rawSum += dRaw; shownSum += dShown; moving++; }
            }
            b.Raw2 = b.Raw1; b.Raw1 = raw;
            b.Shown2 = b.Shown1; b.Shown1 = shown;
            b.Screen = shown;
        }
        if (moving > 0)
        {
            JitterRaw += (rawSum / moving - JitterRaw) * 0.05f;
            JitterShown += (shownSum / moving - JitterShown) * 0.05f;
        }
    }

    private static float Frac(double t, double a, double b) => b > a ? (float)Math.Clamp((t - a) / (b - a), 0, 1) : 1;
}
