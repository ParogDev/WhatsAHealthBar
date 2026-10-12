using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAHealthBar;

/// <summary>
/// Animated health bars over monsters and players, built to cost the same with 1 or 500 on screen.
/// - Tracking: EntityAdded/EntityRemoved keep a flat list; components are looked up once and held.
/// - Classify (10 Hz by default): kind, hostility, distance (grid, squared) and model height. Out-of-range or hidden monsters
///   cost nothing per frame after that.
/// - Every frame, wanted bars only: one Render.Pos read, one camera-snapshot projection, off-screen cull, then life and ES.
/// - Render: flat rectangles into one ImGui draw list (BarRenderer), no textures or strings per frame.
/// - Bridge: WhatsAHealthBar.GetState (counts and own timings, for the control center), WhatsAHealthBar.Preview, and
///   pub/sub: subscribes to healthbars.preview, publishes healthbars.kill.
/// </summary>
public sealed class WhatsAHealthBar : BaseSettingsPlugin<WhatsAHealthBarSettings>
{
    private const string GetStateName = "WhatsAHealthBar.GetState", PreviewName = "WhatsAHealthBar.Preview";

    private readonly Dictionary<uint, Bar> _byId = new();
    private readonly List<Bar> _bars = new();
    private readonly List<Bar> _drawList = new(512);
    private readonly Bar[] _preview = Enumerable.Range(0, 7).Select(i => new Bar { Kind = (BarKind)i, Wanted = true }).ToArray();
    private readonly BarRenderer _renderer = new();
    private readonly AiBridgeLink _link = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int[] _byKind = new int[7];
    private readonly LinkedList<HealthBarKillDto> _kills = new();
    private readonly Random _rng = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(double seconds, string? focus, int stress)> _previewRequests = new();
    private Bar[] _previewOrder = Array.Empty<Bar>(), _stress = Array.Empty<Bar>();

    private double _now, _lastFrame, _nextClassify, _nextUiCheck, _nextWindow, _previewUntil, _nextPreviewHit;
    private float _dt, _resolutionScale = 1;
    private Vector2 _window = new(2560, 1440);
    private bool _panelsOpen, _inTown, _inHideout, _active;
    private string? _suppressed = "not in game", _area, _previewFocus;
    private int _peakDrawn, _wanted;

    // Own timings (microseconds): moving averages, and maxima over a 5 s window.
    private double _tickUs, _renderUs, _classifyUs, _buildUs, _flushUs, _tickMax, _renderMax, _tickMaxNext, _renderMaxNext, _maxWindowEnd;

    public override bool Initialise()
    {
        Name = "Whats A Health Bar";
        Settings.Preview.OnPressed += () => StartPreview(8, null);
        GameController.PluginBridge.SaveMethod(GetStateName, (Func<string>)GetStateJson);
        GameController.PluginBridge.SaveMethod(PreviewName, (Func<string, string>)PreviewFromBridge);
        AreaChange(GameController.Area.CurrentArea);
        return true;
    }

    public override void OnClose() => _link.Close();

    public override void AreaChange(AreaInstance area)
    {
        _byId.Clear();
        _bars.Clear();
        _drawList.Clear();
        _peakDrawn = 0;
        _inTown = area?.IsTown == true;
        _inHideout = area?.IsHideout == true;
        _area = area?.DisplayName;
        // Entities the HUD already knows (added before this callback, or before a plugin reload) get no EntityAdded again.
        var lists = GameController.EntityListWrapper.ValidEntitiesByType;
        foreach (var e in lists[EntityType.Monster]) EntityAdded(e);
        foreach (var e in lists[EntityType.Player]) EntityAdded(e);
    }

    public override void EntityAdded(Entity entity)
    {
        if (entity.Type is not (EntityType.Monster or EntityType.Player)) return;
        if (_byId.ContainsKey(entity.Id)) return;
        var bar = new Bar { Entity = entity, Id = entity.Id, Kind = BarKind.Normal, Index = _bars.Count };
        _byId[entity.Id] = bar;
        _bars.Add(bar);
        _nextClassify = 0;
    }

    public override void EntityRemoved(Entity entity)
    {
        if (!_byId.Remove(entity.Id, out var bar)) return;
        // Swap-remove: O(1), so an area emptying hundreds of monsters at once doesn't hitch.
        var last = _bars[^1];
        _bars[bar.Index] = last;
        last.Index = bar.Index;
        _bars.RemoveAt(_bars.Count - 1);
    }

    public override void Tick()
    {
        var t0 = Stopwatch.GetTimestamp();
        _now = _clock.Elapsed.TotalSeconds;
        _dt = (float)Math.Clamp(_now - _lastFrame, 0, 0.1);
        _lastFrame = _now;
        _drawList.Clear();
        Array.Clear(_byKind);

        _link.Refresh(GameController.PluginBridge, _now);
        while (_link.TryTakePreview(out var msg))
            StartPreview(msg?["seconds"]?.Value<double?>() ?? 8, (string?)msg?["focus"], msg?["stress"]?.Value<int?>() ?? 0);
        while (_previewRequests.TryDequeue(out var req)) StartPreview(req.seconds, req.focus, req.stress);

        _active = Evaluate();
        _renderer.Prepare(Settings, _resolutionScale);
        if (_active)
        {
            if (_now >= _nextClassify)
            {
                var c0 = Stopwatch.GetTimestamp();
                Classify();
                _classifyUs = Stopwatch.GetElapsedTime(c0).TotalMicroseconds;
                _nextClassify = _now + 1.0 / Math.Max(1, Settings.Visibility.ClassifyHz.Value);
            }
            UpdateFrame();
        }
        if (_now < _previewUntil) UpdatePreview();

        var us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        _tickUs += (us - _tickUs) * 0.05;
        _tickMaxNext = Math.Max(_tickMaxNext, us);
    }

    /// <summary>Whether bars are drawn at all right now; <see cref="_suppressed"/> says why not.</summary>
    private bool Evaluate()
    {
        if (!Settings.Enable) { _suppressed = "disabled"; return false; }
        // The window first: the preview draws in town and hideout too.
        if (_now >= _nextWindow)
        {
            _nextWindow = _now + 0.5;
            var r = GameController.Window.GetWindowRectangleReal();
            _window = new Vector2(r.Width, r.Height);
            _resolutionScale = r.Height > 0 ? r.Height / 1440f : 1;
        }
        if (!GameController.InGame || GameController.IngameState?.Camera == null) { _suppressed = "not in game"; return false; }
        if (_inTown && !Settings.Visibility.ShowInTown) { _suppressed = "in town"; return false; }
        if (_inHideout && !Settings.Visibility.ShowInHideout) { _suppressed = "in hideout"; return false; }

        if (Settings.Visibility.HideWhenPanelsOpen)
        {
            if (_now >= _nextUiCheck)
            {
                _nextUiCheck = _now + 0.25;
                var ui = GameController.IngameState.IngameUi;
                _panelsOpen = ui.FullscreenPanels.Any(x => x.IsVisibleLocal) || ui.LargePanels.Any(x => x.IsVisibleLocal);
            }
            if (_panelsOpen) { _suppressed = "a large panel is open"; return false; }
        }
        _suppressed = null;
        return true;
    }

    /// <summary>Kind, wanted and model height for every tracked entity, at the classify rate.</summary>
    private void Classify()
    {
        var player = GameController.Player;
        var playerAddr = player?.Address ?? 0;
        var pg = player?.GridPos ?? Vector2.Zero;
        var maxD = Settings.Visibility.MaxDistance.Value;
        var maxD2 = (float)maxD * maxD;
        var minionBelow = Settings.Visibility.MinionBelowPercent.Value / 100f;
        var hideFull = Settings.Visibility.HideFullNormals.Value;
        var publish = Settings.Bridge.PublishKills.Value;
        _wanted = 0;

        foreach (var b in _bars)
        {
            var e = b.Entity!;
            b.Wanted = false;
            if (!e.IsValid) continue;
            if (!e.IsAlive)
            {
                if (b.Seen && !b.Dying) Die(b, publish);
                continue;
            }
            b.Life ??= e.GetComponent<Life>();
            b.Render ??= e.GetComponent<Render>();
            if (b.Life == null || b.Render == null || e.IsHidden) continue;
            var life = b.Life;
            if (b.Dying)
            {
                // IsAlive can lag CurHP by a frame: stay dead unless it really came back (a revive).
                if (life.CurHP <= 0) continue;
                b.Dying = false;
                b.Published = false;
                b.FirstHitAt = 0;
            }
            // Which pools exist (max values, at the classify rate): the frame loop reads ES and ward only when present.
            b.MaxEs = _renderer.WantsEs ? life.MaxES : 0;
            b.MaxWard = _renderer.Stacked ? life.Ward.Max : 0;
            if (b.MaxEs == 0) b.Es = 0;
            if (b.MaxWard == 0) b.Ward = 0;

            b.Kind = e.Type == EntityType.Player
                ? e.Address == playerAddr ? BarKind.Self : BarKind.Player
                : !e.IsHostile ? BarKind.Minion
                : e.Rarity switch
                {
                    MonsterRarity.Unique => BarKind.Unique,
                    MonsterRarity.Rare => BarKind.Rare,
                    MonsterRarity.Magic => BarKind.Magic,
                    _ => BarKind.Normal,
                };
            if (!_renderer.Styles[(int)b.Kind].Show) continue;
            if (b.Kind != BarKind.Self && Vector2.DistanceSquared(e.GridPos, pg) > maxD2) continue;
            // Life read here (classify rate) for the hidden-until-hurt rules, so those bars cost nothing per frame until
            // they qualify, and reappear as soon as they do.
            if (b.Kind == BarKind.Minion && minionBelow < 1 && life.MaxHP > 0 && life.CurHP >= life.MaxHP * minionBelow) continue;
            if (hideFull && b.Kind == BarKind.Normal && b.FirstHitAt == 0 && life.CurHP >= life.MaxHP) continue;
            b.TopZ = -b.Render.Bounds.Z;
            b.Wanted = true;
            _wanted++;
        }
    }

    /// <summary>Per frame: position, cull, life, animation for wanted (and fading) bars.</summary>
    private void UpdateFrame()
    {
        var snap = GameController.IngameState.Camera.Snapshot;
        var z = Settings.Visibility.ZOffset.Value;
        var anim = Anim();
        var stacked = _renderer.Stacked;
        var margin = 64f;

        foreach (var b in _bars)
        {
            if (!b.Wanted && (b.Alpha <= 0 || !b.Seen)) continue;
            if (!b.Dying && b.Render != null && b.Entity!.IsValid)
            {
                var p = b.Render.Pos;
                b.World = p with { Z = p.Z + b.TopZ + z };
            }
            var s = snap.WorldToScreen(b.World);
            if (s == Vector2.Zero || s.X < -margin || s.Y < -margin || s.X > _window.X + margin || s.Y > _window.Y + margin)
            {
                if (!b.Wanted) b.Alpha = 0;
                continue;
            }
            b.Screen = s;

            if (!b.Dying && b.Life != null)
            {
                var life = b.Life;
                b.Hp = life.CurHP;
                b.MaxHp = life.MaxHP;
                if (b.MaxEs > 0) b.Es = life.CurES;
                if (b.MaxWard > 0) b.Ward = life.Ward.Current;
                if (b.MaxHp <= 0) continue;
                if (b.FirstHitAt == 0 && b.Seen && b.Hp + b.Es + b.Ward < b.MaxHp + b.MaxEs + b.MaxWard) b.FirstHitAt = _now;
                if (b.Hp <= 0 && b.Seen) Die(b, Settings.Bridge.PublishKills.Value);
            }
            b.Animate(b.Dying ? 0 : b.Fraction(stacked), _now, _dt, anim);
            if (b.Alpha <= 0.004f) continue;
            _drawList.Add(b);
            _byKind[(int)b.Kind]++;
        }
        if (_drawList.Count > _peakDrawn) _peakDrawn = _drawList.Count;
    }

    private AnimParams Anim()
    {
        var a = Settings.Animation;
        var fade = a.FadeMs.Value;
        return new AnimParams(a.Enabled, a.DamageTrail, a.TrailHoldMs.Value / 1000.0, a.TrailDrain.Value, a.HitFlash, a.HealGlow, 9f,
            fade <= 0 ? 0 : 1000f / fade);
    }

    private void Die(Bar b, string publish)
    {
        b.Dying = true;
        b.Wanted = false;
        b.Hp = b.Es = b.Ward = 0;
        if (b.Published || b.Entity == null) return;
        var publishIt = publish switch
        {
            "Uniques" => b.Kind == BarKind.Unique,
            "Rares and uniques" => b.Kind is BarKind.Unique or BarKind.Rare,
            _ => false,
        };
        if (!publishIt) return;
        b.Published = true;
        var kill = new HealthBarKillDto
        {
            Name = b.Entity.RenderName ?? "",
            Rarity = b.Kind.ToString(),
            Path = b.Entity.Path,
            MaxLife = b.MaxHp,
            MaxEs = b.MaxEs,
            MaxWard = b.MaxWard,
            TimeToKillMs = b.FirstHitAt > 0 ? (int)((_now - b.FirstHitAt) * 1000) : null,
            Area = _area,
            At = DateTime.UtcNow.ToString("o"),
        };
        lock (_kills)
        {
            _kills.AddFirst(kill);
            while (_kills.Count > 10) _kills.RemoveLast();
        }
        _link.Publish(AiBridgeLink.KillKind, kill);
    }

    public override void Render()
    {
        var t0 = Stopwatch.GetTimestamp();
        var previewing = _now < _previewUntil;
        if ((_active && _drawList.Count > 0) || previewing)
        {
            _renderer.Begin();
            foreach (var b in _drawList) _renderer.Draw(b, _now);
            if (previewing)
            {
                foreach (var b in _stress) _renderer.Draw(b, _now);
                foreach (var b in _previewOrder) _renderer.Draw(b, _now, BarRenderer.KindNames[(int)b.Kind]);
            }
            var f0 = Stopwatch.GetTimestamp();
            _buildUs += (Stopwatch.GetElapsedTime(t0, f0).TotalMicroseconds - _buildUs) * 0.05;
            _renderer.Flush(ImGui.GetBackgroundDrawList());
            _flushUs += (Stopwatch.GetElapsedTime(f0).TotalMicroseconds - _flushUs) * 0.05;
        }

        var us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        _renderUs += (us - _renderUs) * 0.05;
        _renderMaxNext = Math.Max(_renderMaxNext, us);
        if (_now >= _maxWindowEnd)
        {
            _tickMax = _tickMaxNext; _renderMax = _renderMaxNext;
            _tickMaxNext = _renderMaxNext = 0;
            _maxWindowEnd = _now + 5;
        }
    }

    // ---- Preview: sample bars in the middle of the screen with fake hits and heals ----

    /// <summary>
    /// Sample pools per kind, so the preview shows every mix: life + ES (unique), mostly ES (rare), life + ward (magic),
    /// life only (normal, minion), and all three (you).
    /// </summary>
    private static readonly (int life, int es, int ward)[] PreviewPools =
        { (600, 400, 0), (150, 850, 0), (400, 0, 600), (1000, 0, 0), (1000, 0, 0), (700, 300, 0), (500, 300, 200) };

    /// <summary>Main thread only (Tick, the menu button): bridge calls queue through <see cref="_previewRequests"/>.</summary>
    private void StartPreview(double seconds, string? focus, int stress = 0)
    {
        _previewUntil = _clock.Elapsed.TotalSeconds + Math.Clamp(seconds, 0, 60);
        _previewFocus = focus;
        foreach (var b in _preview)
        {
            var (life, es, ward) = PreviewPools[(int)b.Kind];
            b.Seen = false; b.Dying = false; b.FirstHitAt = 0;
            b.Hp = b.MaxHp = life; b.Es = b.MaxEs = es; b.Ward = b.MaxWard = ward;
        }
        // The focused kind first, then the rest in their natural order.
        _previewOrder = _preview.OrderBy(b => string.Equals(b.Kind.ToString(), focus, StringComparison.OrdinalIgnoreCase) ? -1 : (int)b.Kind).ToArray();
        _stress = new Bar[Math.Clamp(stress, 0, 2000)];
        for (var i = 0; i < _stress.Length; i++)
        {
            var kind = (BarKind)(i % 4 == 0 ? i / 4 % 4 : 3); // mostly normals, like a real pack
            var (life, es, ward) = PreviewPools[(int)kind];
            _stress[i] = new Bar { Kind = kind, Wanted = true, Hp = life, MaxHp = life, Es = es, MaxEs = es, Ward = ward, MaxWard = ward };
        }
    }

    /// <summary>PluginBridge WhatsAHealthBar.Preview (any thread): queued for Tick.</summary>
    private string PreviewFromBridge(string arg)
    {
        double seconds = 8; string? focus = null; var stress = 0;
        if (!string.IsNullOrWhiteSpace(arg))
        {
            if (double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) seconds = s;
            else
                try
                {
                    var j = JObject.Parse(arg);
                    seconds = j["seconds"]?.Value<double?>() ?? 8;
                    focus = (string?)j["focus"];
                    stress = j["stress"]?.Value<int?>() ?? 0;
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new PreviewReplyDto
                        { Ok = false, Error = "bad_argument", Message = $"Preview takes seconds or {{seconds, focus}} JSON: {ex.Message}" });
                }
        }
        _previewRequests.Enqueue((seconds, focus, stress));
        return JsonConvert.SerializeObject(new PreviewReplyDto { Ok = true, Seconds = seconds, Focus = focus, Stress = stress });
    }

    private void UpdatePreview()
    {
        var anim = Anim();
        var stacked = _renderer.Stacked;
        var hit = _now >= _nextPreviewHit;
        if (hit) _nextPreviewHit = _now + 0.7;
        var y = _window.Y * 0.30f;
        for (var i = 0; i < _previewOrder.Length; i++)
        {
            var b = _previewOrder[i];
            if (hit)
            {
                // Damage takes ward, then ES, then life; near empty, everything refills (a heal).
                var total = b.MaxHp + b.MaxEs + b.MaxWard;
                if (b.Hp + b.Es + b.Ward <= total * 0.12) { b.Hp = b.MaxHp; b.Es = b.MaxEs; b.Ward = b.MaxWard; }
                else
                {
                    var dmg = (int)(total * (0.06 + _rng.NextDouble() * 0.16));
                    var w = Math.Min(b.Ward, dmg); b.Ward -= w; dmg -= w;
                    var e = Math.Min(b.Es, dmg); b.Es -= e; dmg -= e;
                    b.Hp = Math.Max(1, b.Hp - dmg);
                }
            }
            b.Screen = new Vector2(_window.X * 0.5f, y + i * 34 * _resolutionScale);
            b.Animate(b.Fraction(stacked), _now, _dt, anim);
        }

        // Stress: N bars on a grid over the screen, a few hit each frame, to measure the many-bars cost.
        if (_stress.Length == 0) return;
        var cols = Math.Max(1, (int)(_window.X / 90));
        for (var i = 0; i < _stress.Length; i++)
        {
            var b = _stress[i];
            if (_rng.Next(40) == 0)
            {
                var total = b.MaxHp + b.MaxEs + b.MaxWard;
                if (b.Hp + b.Es + b.Ward <= total * 0.15) { b.Hp = b.MaxHp; b.Es = b.MaxEs; b.Ward = b.MaxWard; }
                else
                {
                    var dmg = (int)(total * 0.12);
                    var w = Math.Min(b.Ward, dmg); b.Ward -= w; dmg -= w;
                    var e = Math.Min(b.Es, dmg); b.Es -= e; dmg -= e;
                    b.Hp = Math.Max(1, b.Hp - dmg);
                }
            }
            b.Screen = new Vector2(45 + i % cols * 90, 60 + i / cols * 22);
            b.Animate(b.Fraction(stacked), _now, _dt, anim);
        }
    }

    // ---- Bridge ----

    private string GetStateJson()
    {
        var previewing = _now < _previewUntil;
        var previewBars = previewing ? _previewOrder.Length + _stress.Length : 0;
        var barsNow = _drawList.Count + previewBars;
        var state = new HealthBarStateDto
        {
            Enabled = Settings.Enable,
            Suppressed = _suppressed,
            Area = _area,
            Counts = new HealthBarCountsDto
            {
                Tracked = _bars.Count,
                Wanted = _wanted,
                Drawn = _drawList.Count,
                PeakDrawn = _peakDrawn,
                ByKind = Enum.GetValues<BarKind>().ToDictionary(k => k.ToString().ToLowerInvariant(), k => _byKind[(int)k]),
            },
            Timings = new HealthBarTimingsDto
            {
                TickUs = Math.Round(_tickUs, 1),
                TickMaxUs = Math.Round(Math.Max(_tickMax, _tickMaxNext), 1),
                RenderUs = Math.Round(_renderUs, 1),
                RenderMaxUs = Math.Round(Math.Max(_renderMax, _renderMaxNext), 1),
                ClassifyUs = Math.Round(_classifyUs, 1),
                PerBarNs = barsNow > 0 ? Math.Round((_tickUs + _renderUs) * 1000 / barsNow) : 0,
                ClassifyHz = Settings.Visibility.ClassifyHz.Value,
                Quads = _renderer.LastQuads,
                BuildUs = Math.Round(_buildUs, 1),
                FlushUs = Math.Round(_flushUs, 1),
                RenderPath = _renderer.Path,
            },
            Preview = new HealthBarPreviewDto
            {
                Active = previewing,
                Bars = previewBars,
                SecondsLeft = Math.Max(0, Math.Round(_previewUntil - _now, 1)),
                Focus = _previewFocus,
            },
            Bridge = new HealthBarBridgeDto { Linked = _link.Available, Why = _link.Why, PublishKills = Settings.Bridge.PublishKills.Value },
        };
        lock (_kills) state.Kills = _kills.ToList();
        return JsonConvert.SerializeObject(state);
    }
}
