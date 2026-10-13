using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
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
/// - Tracking: EntityAdded/EntityRemoved keep a flat list; components are looked up again at the classify rate (a boss phase change swaps them).
/// - Classify (10 Hz by default): kind, hostility, distance (grid, squared) and model height. Out-of-range or hidden monsters
///   cost nothing per frame after that. The information layers resolve here too: the look (kind, override, status and state
///   patches), names, status icons (buffs), invulnerable and cullable states, damage over time.
/// - Every frame, wanted bars only: one Render.Pos read, one camera-snapshot projection, off-screen cull, then life and ES.
/// - Render: flat rectangles into one ImGui draw list (BarRenderer), icons as one more batch, text last.
/// - Bridge: WhatsAHealthBar.GetState (counts and own timings, for the control center), Preview, GetLayers / SetLayers (the
///   status, override and ignore lists), SearchBuffs (the game's buff ids), WhyNotDrawn (per-entity reasons); pub/sub:
///   subscribes to healthbars.preview, publishes healthbars.kill.
/// </summary>
public sealed class WhatsAHealthBar : BaseSettingsPlugin<WhatsAHealthBarSettings>
{
    private const string GetStateName = "WhatsAHealthBar.GetState", PreviewName = "WhatsAHealthBar.Preview";
    private const string GetLayersName = "WhatsAHealthBar.GetLayers", SetLayersName = "WhatsAHealthBar.SetLayers";
    private const string SearchBuffsName = "WhatsAHealthBar.SearchBuffs", WhyName = "WhatsAHealthBar.WhyNotDrawn";

    private readonly Dictionary<uint, Bar> _byId = new();
    private readonly List<Bar> _bars = new();
    private readonly List<Bar> _drawList = new(512);

    private readonly BarRenderer _renderer = new();
    private readonly Motion _motion = new();
    private bool[] _moved = new bool[512];
    private readonly AiBridgeLink _link = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int[] _byKind = new int[7];
    private readonly LinkedList<HealthBarKillDto> _kills = new();
    private readonly Random _rng = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(double seconds, string? focus, int stress)> _previewRequests = new();
    private PoolSim[] _previewSims = Array.Empty<PoolSim>(), _stressSims = Array.Empty<PoolSim>();
    private string[] _previewLabels = Array.Empty<string>();

    private double _now, _lastFrame, _nextClassify, _nextUiCheck, _nextWindow, _previewUntil, _nextPreviewLook;
    private float _dt, _resolutionScale = 1;
    private Vector2 _window = new(2560, 1440);
    private bool _panelsOpen, _inTown, _inHideout, _active;
    private string? _suppressed = "not in game", _area, _previewFocus, _poolProblem;
    private double _poolProblemAt;
    private int _peakDrawn, _wanted, _withStatuses, _invulnerable, _withDot;

    // Information layers: the lists compiled at most once per change (UI, bridge), swapped into the renderer.
    internal readonly object LayersLock = new();
    private int _layersVersion;
    private volatile bool _layersDirty = true;
    private string? _atlasProblem = "not loaded yet";
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainWork = new();
    private int _mainThread;
    private List<BuffDefDto>? _buffCatalog;

    // Own timings (microseconds): moving averages, and maxima over a 5 s window.
    private double _tickUs, _renderUs, _classifyUs, _buildUs, _flushUs, _tickMax, _renderMax, _tickMaxNext, _renderMaxNext, _maxWindowEnd;

    public override bool Initialise()
    {
        Name = "Whats A Health Bar";
        _mainThread = Environment.CurrentManagedThreadId;
        Settings.Preview.OnPressed += () => StartPreview(8, null);
        GameController.PluginBridge.SaveMethod(GetStateName, (Func<string>)GetStateJson);
        GameController.PluginBridge.SaveMethod(PreviewName, (Func<string, string>)PreviewFromBridge);
        GameController.PluginBridge.SaveMethod(GetLayersName, (Func<string>)GetLayersJson);
        GameController.PluginBridge.SaveMethod(SetLayersName, (Func<string, string>)SetLayersFromBridge);
        GameController.PluginBridge.SaveMethod(SearchBuffsName, (Func<string, string>)SearchBuffsFromBridge);
        GameController.PluginBridge.SaveMethod(WhyName, (Func<string, string>)WhyFromBridge);
        Migrate();
        LoadAtlas();
        AreaChange(GameController.Area.CurrentArea);
        return true;
    }

    /// <summary>Settings from before the layers: the single culling marker % becomes the per-rarity notch.</summary>
    private void Migrate()
    {
        Settings.Statuses ??= StatusDefaults.Make();
        Settings.Overrides ??= new List<OverrideRule>();
        Settings.IgnoredPaths ??= new List<string>();
        if (Settings.Look.CullPercent.Value > 0)
        {
            Settings.States.Cullable.Value = StateModes.Notch;
            Settings.Look.CullPercent.Value = 0;
        }
    }

    /// <summary>Loads the status icon atlas (syrairc's glyphs, composed by textures/build-atlas.ps1) as one ImGui texture.</summary>
    private void LoadAtlas()
    {
        var candidates = new[]
        {
            Path.Combine(DirectoryFullName, StatusAtlas.File),
            Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location) ?? "", StatusAtlas.File),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null) { _atlasProblem = $"{StatusAtlas.File} not found in {DirectoryFullName} or next to the plugin DLL: no status icons"; return; }
        try
        {
            if (!Graphics.InitImage(StatusAtlas.TextureName, path)) { _atlasProblem = $"Graphics.InitImage refused {path}: no status icons"; return; }
            _renderer.AtlasId = Graphics.GetTextureId(StatusAtlas.TextureName);
            _atlasProblem = _renderer.AtlasId == IntPtr.Zero ? $"Graphics.GetTextureId({StatusAtlas.TextureName}) returned 0 after loading {path}" : null;
        }
        catch (Exception ex) { _atlasProblem = $"loading {path}: {ex.Message}"; }
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

        while (_mainWork.TryDequeue(out var work)) work();
        _link.Refresh(GameController.PluginBridge, _now);
        while (_link.TryTakePreview(out var msg))
            StartPreview(msg?["seconds"]?.Value<double?>() ?? 8, (string?)msg?["focus"], msg?["stress"]?.Value<int?>() ?? 0);
        while (_previewRequests.TryDequeue(out var req)) StartPreview(req.seconds, req.focus, req.stress);
        if (_layersDirty) CompileLayers();

        _active = Evaluate();
        _renderer.Prepare(Settings, _resolutionScale);
        _renderer.SubPixel = Settings.Motion.SubPixel.Value;
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

    /// <summary>The lists changed (settings panel, bridge, [+ ignore]): recompiled on the next Tick.</summary>
    internal void LayersChanged() => _layersDirty = true;

    private void CompileLayers()
    {
        _layersDirty = false;
        CompiledLayers c;
        lock (LayersLock) c = CompiledLayers.Build(Settings);
        _renderer.Rules = c;
        _layersVersion++;
        _nextClassify = 0;
    }

    internal CompiledLayers Rules => _renderer.Rules;

    /// <summary>Whether bars are drawn at all right now; <see cref="_suppressed"/> says why not.</summary>
    private bool Evaluate()
    {
        // The window first: the preview draws in town, in hideout and while disabled too.
        if (_now >= _nextWindow)
        {
            _nextWindow = _now + 0.5;
            var r = GameController.Window.GetWindowRectangleReal();
            _window = new Vector2(r.Width, r.Height);
            _resolutionScale = r.Height > 0 ? r.Height / 1440f : 1;
        }
        if (!Settings.Enable) { _suppressed = "disabled"; return false; }
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

    /// <summary>Kind, wanted, model height and the information layers for every tracked entity, at the classify rate.</summary>
    private void Classify()
    {
        var player = GameController.Player;
        var playerAddr = player?.Address ?? 0;
        var pg = player?.GridPos ?? Vector2.Zero;
        var maxD = Settings.Visibility.MaxDistance.Value;
        var maxD2 = (float)maxD * maxD;
        var minionBelow = Settings.Visibility.MinionBelowPercent.Value / 100f;
        var hideFull = Settings.Visibility.HideFullNormals.Value;
        var hideUntargetable = Settings.Visibility.HideUntargetable.Value;
        var publish = Settings.Bridge.PublishKills.Value;
        var states = Settings.States;
        var invulnOn = states.Invulnerable.Value != StateModes.Off;
        var invulnColor = BarRenderer.Pack(states.InvulnerableColor.Value);
        var cullOn = states.Cullable.Value != StateModes.Off;
        var hiddenDim = states.Hidden.Value == StateModes.Dim;
        var dotOn = Settings.Dot.Enabled.Value;
        var abbreviate = Settings.Text.AbbreviateOver.Value;
        var L = _renderer.Rules;
        var nameOverrides = false;
        foreach (var o in L.Overrides) nameOverrides |= string.Equals(o.Match, "Name", StringComparison.OrdinalIgnoreCase);
        Span<float> cull = stackalloc float[4];
        if (cullOn)
        {
            var mul = states.CullMultiplier.Value;
            cull[(int)BarKind.Unique] = Math.Min(1, states.CullUnique.Value / 100f * mul);
            cull[(int)BarKind.Rare] = Math.Min(1, states.CullRare.Value / 100f * mul);
            cull[(int)BarKind.Magic] = Math.Min(1, states.CullMagic.Value / 100f * mul);
            cull[(int)BarKind.Normal] = Math.Min(1, states.CullNormal.Value / 100f * mul);
        }
        _wanted = _withStatuses = _invulnerable = _withDot = 0;

        foreach (var b in _bars)
        {
            var e = b.Entity!;
            b.Wanted = false;
            if (!e.IsValid) { b.Why = Why.Invalid; continue; }
            if (!e.IsAlive)
            {
                b.Why = Why.Dead;
                if (b.Seen && !b.Dying) Die(b, publish);
                continue;
            }
            // Looked up again every pass, not held for good: a boss phase change (immune, adds, comes back) gives the same
            // entity new Life and Render components, and the old ones read 0/1 HP forever (a fake kill, the bar never back).
            b.LifeComp = e.GetComponent<Life>();
            b.Render = e.GetComponent<Render>();
            if (b.LifeComp == null) { b.Why = Why.NoLifeComponent; continue; }
            if (b.Render == null) { b.Why = Why.NoRenderComponent; continue; }
            var hidden = e.IsHidden;
            if (hidden && !hiddenDim) { b.Why = Why.Hidden; continue; }
            var life = b.LifeComp;
            if (b.Dying)
            {
                // IsAlive can lag CurHP by a frame: stay dead unless it really came back (a revive).
                if (life.CurHP <= 0) { b.Why = Why.Dead; continue; }
                b.Dying = false;
                b.Published = false;
                b.FirstHitAt = 0;
            }
            // Which pools to read per frame (classify rate): any with a max OR a current value, so a pool granted without a
            // max of its own still shows. The rest cost nothing per frame.
            var pools = _renderer.WantsPools;
            for (var p = Bar.Es; p <= Bar.Ward; p++)
            {
                var v = !pools ? default : p == Bar.Es ? life.EnergyShield : life.Ward;
                var track = pools && (v.Max > 0 || v.Current > 0);
                if (!track && b.Tracked[p]) b.ClearPool(p);
                b.Tracked[p] = track;
            }

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
            if (b.PathKey.Length == 0) b.PathKey = CompiledLayers.PathKey(e.Path);
            if (L.Ignored.Count > 0 && L.Ignored.Contains(b.PathKey)) { b.Why = Why.Ignored; continue; }
            if (!_renderer.Styles[(int)b.Kind].Show) { b.Why = Why.KindHidden; continue; }
            var d2 = b.Kind == BarKind.Self ? 0 : Vector2.DistanceSquared(e.GridPos, pg);
            b.Dist = MathF.Sqrt(d2);
            if (d2 > maxD2) { b.Why = Why.TooFar; continue; }
            // Hostile non-uniques only: allies are never targetable by you, and a boss mid-phase keeps its bar.
            if (hideUntargetable && b.Kind is BarKind.Rare or BarKind.Magic or BarKind.Normal && !e.IsTargetable) { b.Why = Why.Untargetable; continue; }
            // Life read here (classify rate) for the hidden-until-hurt rules, so those bars cost nothing per frame until
            // they qualify, and reappear as soon as they do.
            if (b.Kind == BarKind.Minion && minionBelow < 1 && life.MaxHP > 0 && life.CurHP >= life.MaxHP * minionBelow) { b.Why = Why.MinionNotHurt; continue; }
            if (hideFull && b.Kind == BarKind.Normal && b.FirstHitAt == 0 && life.CurHP >= life.MaxHP) { b.Why = Why.NormalNotHit; continue; }

            // ---- Layers: the look, in order kind -> override -> statuses -> state ----
            var look = _renderer.BaseLook(b.Kind);
            if (L.Overrides.Length > 0)
            {
                if (b.OverrideVersion != _layersVersion)
                {
                    b.OverrideIndex = L.MatchOverride(b.PathKey, nameOverrides ? e.RenderName ?? "" : "");
                    b.OverrideVersion = _layersVersion;
                }
                if (b.OverrideIndex >= 0) L.OverridePatches[b.OverrideIndex]?.ApplyTo(ref look);
                if (look.Hide) { b.Why = Why.OverrideHidden; continue; }
            }
            else b.OverrideIndex = -1;

            if (_renderer.Styles[(int)b.Kind].HasText && b.NameThreshold != abbreviate)
            {
                var raw = e.RenderName;
                if (string.IsNullOrEmpty(raw)) raw = b.PathKey[(b.PathKey.LastIndexOf('/') + 1)..];
                b.DisplayName = TextTemplate.Abbreviate(raw, abbreviate);
                b.NameThreshold = abbreviate;
                b.NameVersion++;
            }

            // Stats: invulnerable, can't be culled, damage over time (one frame-cached dictionary read).
            b.Invulnerable = b.CannotBeCulled = false;
            var dotPerMin = 0;
            var dotThroughEs = false;
            if (invulnOn || cullOn || dotOn)
            {
                b.StatsComp = e.GetComponent<Stats>();
                var sd = b.StatsComp?.StatDictionary;
                if (sd != null)
                {
                    b.Invulnerable = sd.TryGetValue(GameStat.CannotBeDamaged, out var cbd) && cbd != 0;
                    b.CannotBeCulled = sd.TryGetValue(GameStat.CannotBeCulled, out var cbc) && cbc != 0;
                    if (dotOn && sd.TryGetValue(GameStat.IsCurrentlyTakingDamageOverTime, out var dot) && dot != 0)
                    {
                        // PoE2 monsters report their DoT as damage to ES with the excess to life (read live 2026-10-12: the
                        // "to life" stat stays 0 while this one carries the whole rate).
                        sd.TryGetValue(GameStat.TotalDamageTakenPerMinuteToLife, out var toLife);
                        sd.TryGetValue(GameStat.TotalDamageTakenPerMinuteToEnergyShieldExcessToLife, out var viaEs);
                        dotPerMin = Math.Max(0, toLife) + Math.Max(0, viaEs);
                        dotThroughEs = viaEs > 0;
                    }
                }
            }

            // Buffs: status icons, status patches and the DoT's time left, read only when one of those needs them.
            var icons = _renderer.IconsFor(b.Kind) && (b.Kind != BarKind.Minion || Settings.Icons.Kinds.Value == IconKinds.All);
            b.StatusCount = 0;
            var horizon = 0f;
            if ((icons && L.StatusById.Count > 0) || L.AnyStatusPatch || dotPerMin > 0)
            {
                b.BuffsComp = e.GetComponent<Buffs>();
                var list = b.BuffsComp?.BuffsList;
                if (list != null) horizon = ReadBuffs(b, list, L, dotPerMin > 0, icons || L.AnyStatusPatch);
                for (var i = 0; i < b.StatusCount; i++) L.StatusPatches[b.Statuses![i].Rule]?.ApplyTo(ref look);
                if (!icons && b.StatusCount > 0) b.StatusCount = 0; // read for the patches only
                if (look.Hide) { b.Why = Why.StatusHidden; continue; }
                if (b.StatusCount > 0) _withStatuses++;
            }

            if (b.Invulnerable && invulnOn)
            {
                _invulnerable++;
                if (_renderer.InvulnerableHides) { b.Why = Why.Invulnerable; continue; }
                look.Life = invulnColor;
                look.Grey = look.Hatch = true;
            }
            if (hidden) look.Opacity *= 0.35f;
            b.CullAt = cullOn && !b.CannotBeCulled && b.Kind <= BarKind.Normal ? cull[(int)b.Kind] : 0;
            if (dotPerMin > 0)
            {
                b.DotRate = dotPerMin / 60f;
                b.DotUntil = _now + (horizon > 0 ? horizon : Settings.Dot.FallbackSeconds.Value);
                b.DotThroughEs = dotThroughEs;
                _withDot++;
            }
            else b.DotRate = 0;

            b.Look = look;
            b.TopZ = -b.Render.Bounds.Z;
            b.Wanted = true;
            b.Why = Why.Drawn;
            _wanted++;
        }
    }

    // PoE2 default durations (poe2db: ignite 4 s, bleed 5 s, poison 2 s), for DoT ailments whose buff has no timer.
    private const float IgniteSeconds = 4, BleedSeconds = 5, PoisonSeconds = 2;

    /// <summary>
    /// Matches the monster's buffs against the status list (one icon per status: stacks summed over its instances, the
    /// longest timer kept) and returns the longest time left on a damage-over-time ailment (0: none found).
    /// </summary>
    private float ReadBuffs(Bar b, List<Buff> list, CompiledLayers L, bool dot, bool statuses)
    {
        var horizon = 0f;
        var byId = L.StatusById;
        foreach (var buff in list)
        {
            if (buff == null) continue;
            var id = buff.Name;
            if (string.IsNullOrEmpty(id)) continue;
            var timer = buff.Timer;
            var hasTimer = float.IsFinite(timer) && timer > 0 && timer < 3600;
            if (dot)
            {
                var fallback = id.Contains("ignite", StringComparison.Ordinal) || id.Contains("burning", StringComparison.Ordinal) ? IgniteSeconds
                    : id.Contains("bleed", StringComparison.Ordinal) || id.Contains("corrupted_blood", StringComparison.Ordinal) ? BleedSeconds
                    : id.Contains("poison", StringComparison.Ordinal) ? PoisonSeconds
                    : -1f;
                if (fallback > 0) horizon = MathF.Max(horizon, hasTimer ? timer : fallback);
            }
            if (!statuses || !byId.TryGetValue(id, out var rule)) continue;
            int raw = Math.Max(buff.BuffStacks, buff.BuffCharges);
            var stacks = raw is > 1 and <= 999 ? raw : 1;
            var until = hasTimer ? _now + timer : 0;
            var hits = b.Statuses ??= new StatusHit[16];
            var at = -1;
            for (var i = 0; i < b.StatusCount; i++) if (hits[i].Rule == rule) { at = i; break; }
            if (at >= 0)
            {
                hits[at].Stacks += stacks;
                if (until > hits[at].Until) hits[at].Until = until;
            }
            else if (b.StatusCount < hits.Length) hits[b.StatusCount++] = new StatusHit { Rule = rule, Stacks = stacks, Until = until };
        }
        return horizon;
    }

    /// <summary>Per frame: position, cull, life, animation for wanted (and fading) bars.</summary>
    private void UpdateFrame()
    {
        var snap = GameController.IngameState.Camera.Snapshot;
        var z = Settings.Visibility.ZOffset.Value;
        // The game's own bar: the same model-top projection, then a fixed screen offset (bar centre 2.3 px left, bottom
        // 4.5 px up at 1080p). No memory anchor exists for it (it is drawn with the 3D scene), so this is the 1:1 match.
        var align = Settings.Visibility.AlignToGameBar.Value ? new Vector2(-2.3f, -4.5f) * (_window.Y / 1080f) : Vector2.Zero;
        var anim = Anim();
        var margin = 64f;
        var cullHide = _renderer.CullHide;
        var dpsTau = Settings.Text.DpsWindow.Value;

        foreach (var b in _bars)
        {
            if (!b.Wanted && (b.Alpha <= 0 || !b.Seen)) continue;
            var moved = false;
            if (!b.Dying && b.Render != null && b.Entity!.IsValid)
            {
                var p = b.Render.Pos;
                var w = p with { Z = p.Z + b.TopZ + z };
                moved = w != b.World;
                b.World = w;
            }
            var s = snap.WorldToScreen(b.World);
            if (s == Vector2.Zero || s.X < -margin || s.Y < -margin || s.X > _window.X + margin || s.Y > _window.Y + margin)
            {
                if (!b.Wanted) b.Alpha = 0;
                else b.Why = Why.OffScreen;
                continue;
            }
            b.Screen = s + align;

            if (!b.Dying && b.LifeComp != null)
            {
                // One struct read per pool: current and max from the same moment (separate CurHP/MaxHP reads could straddle
                // a buff and show a fake overfull). The full mark is the unreserved max (reserved life or ES can't be filled).
                var life = b.LifeComp;
                var hv = life.Health;
                if (!b.SetPool(Bar.Life, hv.Current, hv.Unreserved)) { BadPool(b, "life", hv.Current, hv.Max); b.Why = Why.NoPools; continue; }
                if (b.Tracked[Bar.Es])
                {
                    var ev = life.EnergyShield;
                    if (!b.SetPool(Bar.Es, ev.Current, ev.Unreserved)) { BadPool(b, "energy shield", ev.Current, ev.Max); b.ClearPool(Bar.Es); }
                }
                if (b.Tracked[Bar.Ward])
                {
                    var wv = life.Ward;
                    if (!b.SetPool(Bar.Ward, wv.Current, wv.Max)) { BadPool(b, "ward", wv.Current, wv.Max); b.ClearPool(Bar.Ward); }
                }
                if (b.MaxTotal <= 0) { b.Why = Why.NoPools; continue; }
                b.TrackHit(_now);
                if (b.MaxHp > 0 && b.Hp <= 0 && b.Seen) Die(b, Settings.Bridge.PublishKills.Value);
                b.UpdateDps(_now, dpsTau);
                DotLoss(b, _now);
                if (cullHide && b.CullAt > 0 && b.MaxHp > 0 && b.Hp <= b.MaxHp * b.CullAt && !b.Dying) { b.Why = Why.Cullable; continue; }
                if (b.Wanted) b.Why = Why.Drawn;
            }
            b.Animate(_now, _dt, anim);
            if (b.Alpha <= 0.004f) continue;
            if (_drawList.Count == _moved.Length) Array.Resize(ref _moved, _moved.Length * 2);
            _moved[_drawList.Count] = moved;
            _drawList.Add(b);
            _byKind[(int)b.Kind]++;
        }
        if (_drawList.Count > _peakDrawn) _peakDrawn = _drawList.Count;
        // Evenly spaced motion: draw between the last two game frames (Motion.cs).
        _motion.Apply(_drawList, snap.Matrix, _moved, _now, Settings.Motion.Interpolate.Value);
    }

    /// <summary>The life the running damage over time will still take: rate x time left, ES first when it goes through ES, capped at life.</summary>
    private static void DotLoss(Bar b, double now)
    {
        if (b.DotRate <= 0 || b.DotUntil <= now) { b.DotLossShown = 0; return; }
        var loss = b.DotRate * (float)(b.DotUntil - now) - (b.DotThroughEs ? b.Cur[Bar.Es] : 0);
        b.DotLossShown = Math.Clamp(loss, 0, b.Cur[Bar.Life]);
    }

    /// <summary>
    /// A pool read that can't be real (negative, or over 100M): the Life component's layout probably moved with a game patch.
    /// The pool is dropped for this bar and the state says which pool and what was read, instead of drawing garbage.
    /// </summary>
    private void BadPool(Bar b, string pool, int cur, int max)
    {
        _poolProblem = $"{pool} read {cur}/{max} on {b.Entity?.Path ?? "a preview bar"}: implausible, the pool is not drawn " +
                       "(Life component layout moved with a patch?)";
        _poolProblemAt = _now;
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
        b.DotRate = b.DotLossShown = 0;
        var (maxEs, maxWard) = (b.BaseMax[Bar.Es], b.BaseMax[Bar.Ward]);
        b.Zero();
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
            MaxEs = maxEs,
            MaxWard = maxWard,
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
        // The overlay is a topmost window: with another window in front of the game, the bars would land on that window.
        var inFront = GameController.IsForeGroundCache && GameInFront.Check(GameController.Window.Process.Id);
        if (inFront && ((_active && _drawList.Count > 0) || previewing))
        {
            _renderer.Begin();
            foreach (var b in _drawList) _renderer.Draw(b, _now);
            if (previewing)
            {
                foreach (var s in _stressSims) _renderer.Draw(s.Bar, _now);
                for (var i = 0; i < _previewSims.Length; i++) _renderer.Draw(_previewSims[i].Bar, _now, _previewLabels[i]);
            }
            var f0 = Stopwatch.GetTimestamp();
            _buildUs += (Stopwatch.GetElapsedTime(t0, f0).TotalMicroseconds - _buildUs) * 0.05;
            _renderer.Flush(ImGui.GetBackgroundDrawList());
            _flushUs += (Stopwatch.GetElapsedTime(f0).TotalMicroseconds - _flushUs) * 0.05;
        }
        else
        {
            // Nothing drawn: the split decays toward zero instead of showing the last busy frame's numbers.
            _renderer.Idle();
            _buildUs *= 0.95;
            _flushUs *= 0.95;
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

        // Debug only, outside the timings: why each nearby monster's bar is or isn't drawn.
        if (inFront && _active && Settings.Debug.WhyOverlay) WhyOverlay.Draw(this, _bars, Settings);
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        SettingsPanel.Draw(this, Settings);
    }

    internal void Ignore(string pathKey)
    {
        if (string.IsNullOrWhiteSpace(pathKey)) return;
        lock (LayersLock)
            if (!Settings.IgnoredPaths.Contains(pathKey, StringComparer.OrdinalIgnoreCase)) Settings.IgnoredPaths.Add(pathKey);
        LayersChanged();
    }

    internal string MaxDistanceText => Settings.Visibility.MaxDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- Preview: sample bars in the middle of the screen with fake hits and heals ----

    /// <summary>
    /// Sample pools per kind, so the preview shows every mix: life + ES (unique), mostly ES (rare), life + ward (magic),
    /// life only (normal, minion), and all three (you).
    /// </summary>
    private static readonly (int life, int es, int ward)[] PreviewPools =
        { (600, 400, 0), (150, 850, 0), (400, 0, 600), (1000, 0, 0), (1000, 0, 0), (700, 300, 0), (500, 300, 200) };

    private static readonly string[] PreviewNames =
        { "Xesht, We That Are One", "Gloomhowl Ravager", "Rotting Hulk", "Drowned", "Skeletal Warrior", "Exile", "You" };

    /// <summary>Statuses each sample kind shows (by id, from the current list; missing ids are skipped).</summary>
    private static readonly string[][] PreviewStatuses =
    {
        new[] { "ignited", "shocked", "curse_vulnerability", "exposure_elemental" },
        new[] { "poison", "chilled", "broken_armour_debuff" },
        new[] { "bleeding" },
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        new[] { "KaruiAdrenaline" },
    };

    /// <summary>Main thread only (Tick, the menu button): bridge calls queue through <see cref="_previewRequests"/>.</summary>
    private void StartPreview(double seconds, string? focus, int stress = 0)
    {
        var now = _clock.Elapsed.TotalSeconds;
        _previewUntil = now + Math.Clamp(seconds, 0, 60);
        _previewFocus = focus;
        // The focused kind first, then the rest in their natural order; then an invulnerable unique.
        var kinds = Enum.GetValues<BarKind>()
            .OrderBy(k => string.Equals(k.ToString(), focus, StringComparison.OrdinalIgnoreCase) ? -1 : (int)k).ToList();
        var sims = kinds.Select(k => Sim(k, now)).ToList();
        var labels = kinds.Select(k => BarRenderer.KindNames[(int)k]).ToList();
        var immune = Sim(BarKind.Unique, now);
        immune.Bar.Invulnerable = true;
        immune.Bar.DisplayName = "Immune phase";
        sims.Add(immune);
        labels.Add("Immune");
        _previewSims = sims.ToArray();
        _previewLabels = labels.ToArray();
        // Stress: rows that fit on screen, mostly normals like a real pack.
        _stressSims = new PoolSim[Math.Clamp(stress, 0, 2000)];
        for (var i = 0; i < _stressSims.Length; i++) _stressSims[i] = Sim((BarKind)(i % 4 == 0 ? i / 4 % 4 : 3), now);
        _nextPreviewLook = 0;
    }

    private PoolSim Sim(BarKind kind, double now)
    {
        var (life, es, ward) = PreviewPools[(int)kind];
        var bar = new Bar { Kind = kind, Wanted = true, DisplayName = PreviewNames[(int)kind], NameVersion = 1, Dist = 30 };
        return new PoolSim(bar, life, es, ward, now, _rng);
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
        var dpsTau = Settings.Text.DpsWindow.Value;
        var look = _now >= _nextPreviewLook;
        if (look) _nextPreviewLook = _now + 0.1;
        var y = _window.Y * 0.30f;
        for (var i = 0; i < _previewSims.Length; i++)
        {
            var sim = _previewSims[i];
            sim.Step(_now, _dt, _rng);
            var b = sim.Bar;
            b.Screen = new Vector2(_window.X * 0.5f, y + i * 48 * _resolutionScale);
            if (look) PreviewLook(b);
            b.UpdateDps(_now, dpsTau);
            DotLoss(b, _now);
            b.Animate(_now, _dt, anim);
        }

        // Stress: N bars on a grid, wrapping columns so every bar is on screen and counts honestly in PerBarNs.
        if (_stressSims.Length == 0) return;
        var cols = Math.Max(1, (int)((_window.X - 20) / 90));
        var rows = Math.Max(1, (int)((_window.Y - 60) / 22));
        for (var i = 0; i < _stressSims.Length; i++)
        {
            var sim = _stressSims[i];
            sim.Step(_now, _dt, _rng);
            var slot = i % (cols * rows); // past one screenful, bars overlap rather than leave the screen
            sim.Bar.Screen = new Vector2(55 + slot % cols * 90 + i / (cols * rows) * 6, 60 + slot / cols * 22);
            if (look) sim.Bar.Look = _renderer.BaseLook(sim.Bar.Kind);
            sim.Bar.Animate(_now, _dt, anim);
        }
    }

    /// <summary>A sample bar's look as classify would resolve it: fake statuses, a DoT on uniques and rares, the immune sample.</summary>
    private void PreviewLook(Bar b)
    {
        var L = _renderer.Rules;
        var look = _renderer.BaseLook(b.Kind);
        var states = Settings.States;
        b.StatusCount = 0;
        if (b.Invulnerable)
        {
            look.Life = BarRenderer.Pack(states.InvulnerableColor.Value);
            look.Grey = look.Hatch = true;
        }
        else
        {
            foreach (var id in PreviewStatuses[(int)b.Kind])
            {
                if (!L.StatusById.TryGetValue(id, out var rule)) continue;
                var hits = b.Statuses ??= new StatusHit[16];
                hits[b.StatusCount++] = new StatusHit { Rule = rule, Stacks = id == "poison" ? 5 : 1, Until = _now + 2 + (rule % 5) };
                L.StatusPatches[rule]?.ApplyTo(ref look);
            }
            if (b.Kind <= BarKind.Rare && Settings.Dot.Enabled)
            {
                // A DoT refreshed every 3 s at 6% of max life per second.
                if (b.DotUntil <= _now) b.DotUntil = _now + 3;
                b.DotRate = b.BaseMax[Bar.Life] * 0.06f;
            }
            else b.DotRate = 0;
        }
        if (!_renderer.IconsFor(b.Kind)) b.StatusCount = 0;
        var cullOn = states.Cullable.Value != StateModes.Off;
        var mul = states.CullMultiplier.Value;
        b.CullAt = !cullOn || b.Invulnerable ? 0 : b.Kind switch
        {
            BarKind.Unique => states.CullUnique.Value / 100f * mul,
            BarKind.Rare => states.CullRare.Value / 100f * mul,
            BarKind.Magic => states.CullMagic.Value / 100f * mul,
            BarKind.Normal => states.CullNormal.Value / 100f * mul,
            _ => 0,
        };
        b.Look = look;
    }

    // ---- Bridge ----

    /// <summary>
    /// Runs <paramref name="work"/> on the main thread (next Tick) and waits for it, so bridge calls from any thread see a
    /// consistent bar list and settings. Inline when already on the main thread (run_csharp scripts).
    /// </summary>
    private string OnMain(Func<string> work, string method)
    {
        if (Environment.CurrentManagedThreadId == _mainThread) return work();
        string? result = null;
        using var done = new ManualResetEventSlim();
        _mainWork.Enqueue(() =>
        {
            try { result = work(); }
            catch (Exception ex) { result = JsonConvert.SerializeObject(new LayersReplyDto { Ok = false, Error = "threw", Message = $"{method}: {ex.Message}" }); }
            finally { done.Set(); }
        });
        if (done.Wait(3000)) return result!;
        return JsonConvert.SerializeObject(new LayersReplyDto
        {
            Ok = false, Error = "tick_timeout",
            Message = $"{method}: the plugin's Tick didn't run within 3 s (plugin disabled, or the HUD is paused or loading); nothing was changed",
        });
    }

    private static readonly JsonSerializerSettings Json = new() { NullValueHandling = NullValueHandling.Ignore };

    /// <summary>WhatsAHealthBar.GetLayers: the status, override and ignore lists, the icon names, and what didn't compile.</summary>
    private string GetLayersJson()
    {
        lock (LayersLock)
            return JsonConvert.SerializeObject(new LayersDto
            {
                Statuses = Settings.Statuses,
                Overrides = Settings.Overrides,
                IgnoredPaths = Settings.IgnoredPaths,
                Icons = StatusAtlas.Names,
                BadgeStyles = Badges.All,
                Problems = _renderer.Rules.Problems,
            }, Json);
    }

    /// <summary>WhatsAHealthBar.SetLayers(json): replaces each list given (null or missing: unchanged), applied on the next Tick.</summary>
    private string SetLayersFromBridge(string arg)
    {
        LayersDto? dto;
        try { dto = JsonConvert.DeserializeObject<LayersDto>(arg ?? ""); }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject(new LayersReplyDto { Ok = false, Error = "bad_argument", Message = $"SetLayers takes {{statuses?, overrides?, ignoredPaths?}} JSON: {ex.Message}" });
        }
        if (dto == null || (dto.Statuses == null && dto.Overrides == null && dto.IgnoredPaths == null))
            return JsonConvert.SerializeObject(new LayersReplyDto { Ok = false, Error = "bad_argument", Message = "SetLayers: give at least one of statuses, overrides, ignoredPaths" });
        return OnMain(() =>
        {
            lock (LayersLock)
            {
                if (dto.Statuses != null) Settings.Statuses = dto.Statuses.Where(s => s != null).ToList();
                if (dto.Overrides != null) Settings.Overrides = dto.Overrides.Where(o => o != null).ToList();
                if (dto.IgnoredPaths != null) Settings.IgnoredPaths = dto.IgnoredPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList();
            }
            CompileLayers();
            var c = _renderer.Rules;
            return JsonConvert.SerializeObject(new LayersReplyDto
            {
                Ok = true, Statuses = Settings.Statuses.Count, Overrides = Settings.Overrides.Count, IgnoredPaths = Settings.IgnoredPaths.Count,
                Problems = c.Problems,
            });
        }, SetLayersName);
    }

    /// <summary>The game's buff definitions (id, name), read once on the main thread.</summary>
    internal List<BuffDefDto> BuffCatalog()
    {
        if (_buffCatalog != null) return _buffCatalog;
        var list = new List<BuffDefDto>();
        var defs = GameController.Files.BuffDefinitions;
        defs.ReloadIfEmpty();
        foreach (var d in defs.EntriesList)
            if (d != null && !string.IsNullOrEmpty(d.Id)) list.Add(new BuffDefDto { Id = d.Id, Name = d.Name ?? "", Invisible = d.IsInvisible });
        if (list.Count > 0) _buffCatalog = list; // an empty read (not in game yet) is retried next call
        return list;
    }

    /// <summary>WhatsAHealthBar.SearchBuffs(query): up to 60 buff definitions whose id or name contains the query.</summary>
    private string SearchBuffsFromBridge(string query) => OnMain(() =>
    {
        var all = BuffCatalog();
        if (all.Count == 0) return JsonConvert.SerializeObject(new LayersReplyDto { Ok = false, Error = "no_buff_definitions", Message = "Files.BuffDefinitions is empty (not in game yet?)" });
        var listed = _renderer.Rules.StatusById;
        var q = (query ?? "").Trim();
        var hits = all.Where(d => q.Length == 0 || d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(60).Select(d => new BuffDefDto { Id = d.Id, Name = d.Name, Invisible = d.Invisible, Listed = listed.ContainsKey(d.Id) }).ToList();
        return JsonConvert.SerializeObject(hits);
    }, SearchBuffsName);

    /// <summary>
    /// WhatsAHealthBar.WhyNotDrawn(arg): per tracked entity, whether its bar is drawn and the first reason it isn't.
    /// arg: empty (the 40 nearest), an entity id, or text matched against the path and name.
    /// </summary>
    private string WhyFromBridge(string arg) => OnMain(() =>
    {
        var reply = new WhyReplyDto { Suppressed = _suppressed, MaxDistance = Settings.Visibility.MaxDistance.Value, Tracked = _bars.Count };
        var q = (arg ?? "").Trim();
        IEnumerable<Bar> pick = _bars;
        if (uint.TryParse(q, out var id)) pick = _bars.Where(b => b.Id == id);
        else if (q.Length > 0) pick = _bars.Where(b => (b.Entity?.Path ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || b.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var b in pick.OrderBy(b => b.Why == Why.Drawn ? 0 : 1).ThenBy(b => b.Dist).Take(40)) reply.Entities.Add(WhyEntity(b));
        if (reply.Entities.Count == 0 && q.Length > 0) reply.Message = $"no tracked monster or player matches '{q}' (by id, path or name) among {_bars.Count}";
        return JsonConvert.SerializeObject(reply, Json);
    }, WhyName);

    internal WhyEntityDto WhyEntity(Bar b)
    {
        var L = _renderer.Rules;
        var e = b.Entity;
        var dto = new WhyEntityDto
        {
            Id = b.Id,
            Path = b.PathKey.Length > 0 ? b.PathKey : e?.Path,
            Name = b.DisplayName.Length > 0 ? b.DisplayName : null,
            Kind = b.Kind.ToString(),
            Drawn = b.Why == Why.Drawn && _drawList.Contains(b),
            Reason = WhyText.Code(b.Why),
            Detail = WhyText.Detail(b, Settings),
            Distance = MathF.Round(b.Dist),
            Invulnerable = b.Invulnerable,
            CullAtPercent = b.CullAt > 0 ? MathF.Round(b.CullAt * 1000) / 10 : null,
            Override = b.OverrideIndex >= 0 && b.OverrideIndex < L.Overrides.Length ? L.Overrides[b.OverrideIndex].Name : null,
            DotPerSecond = b.DotRate > 0 ? MathF.Round(b.DotRate) : null,
            Dps = b.Dps > 0 ? MathF.Round(b.Dps) : null,
        };
        for (var i = 0; i < b.StatusCount; i++)
            if (b.Statuses![i].Rule < L.Statuses.Length) dto.Statuses.Add(L.Statuses[b.Statuses[i].Rule].Id);
        return dto;
    }

    private string GetStateJson()
    {
        var previewing = _now < _previewUntil;
        var previewBars = previewing ? _previewSims.Length + _stressSims.Length : 0;
        var barsNow = _drawList.Count + previewBars;
        var rules = _renderer.Rules;
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
                WithStatuses = _withStatuses,
                Invulnerable = _invulnerable,
                WithDot = _withDot,
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
                Icons = _renderer.LastIcons,
                BuildUs = Math.Round(_buildUs, 1),
                FlushUs = Math.Round(_flushUs, 1),
                RenderPath = _renderer.Path,
                Motion = new HealthBarMotionDto
                {
                    Interpolate = Settings.Motion.Interpolate.Value,
                    SubPixel = Settings.Motion.SubPixel.Value,
                    LagMs = Math.Round(_motion.Lag * 1000, 1),
                    HudFramesPerGameFrame = Math.Round(_motion.FramesPerAdvance, 2),
                    JitterRawPx = Math.Round(_motion.JitterRaw, 2),
                    JitterShownPx = Math.Round(_motion.JitterShown, 2),
                },
            },
            Preview = new HealthBarPreviewDto
            {
                Active = previewing,
                Bars = previewBars,
                SecondsLeft = Math.Max(0, Math.Round(_previewUntil - _now, 1)),
                Focus = _previewFocus,
            },
            Bridge = new HealthBarBridgeDto { Linked = _link.Available, Why = _link.Why, PublishKills = Settings.Bridge.PublishKills.Value },
            Layers = new HealthBarLayersDto
            {
                Statuses = rules.StatusById.Count,
                Overrides = rules.Overrides.Length,
                IgnoredPaths = rules.Ignored.Count,
                AtlasProblem = _atlasProblem,
                Problems = rules.Problems.Count > 0 ? rules.Problems : null,
            },
            PoolProblem = _now - _poolProblemAt < 30 ? _poolProblem : null,
        };
        lock (_kills) state.Kills = _kills.ToList();
        return JsonConvert.SerializeObject(state);
    }
}
