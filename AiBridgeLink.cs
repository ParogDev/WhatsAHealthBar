using System;
using System.Collections.Concurrent;
using ExileCore2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAHealthBar;

/// <summary>
/// This plugin's side of the AI Bridge's pub/sub, through PluginBridge only (no compile-linking). It publishes rare events
/// (healthbars.kill) and subscribes to healthbars.preview, which the control center or an agent publishes to show sample bars
/// in game. The bridge may load after this plugin, reload or be absent: its methods are looked up again every 2 s, a new
/// bridge load gets the subscription again, and it is renewed every 30 s anyway. Messages arrive on the publisher's thread
/// and are queued for Tick. Each plugin load subscribes under its own name, so an old load's OnClose can't drop the new one.
/// </summary>
public sealed class AiBridgeLink
{
    public const string PublishName = "AIBridge.Publish", SubscribeName = "AIBridge.Subscribe";
    public const string PreviewKind = "healthbars.preview", KillKind = "healthbars.kill";

    private readonly string _subscriber = "WhatsAHealthBar#" + Guid.NewGuid().ToString("N")[..8];
    private Func<string, string, string>? _publish;
    private Func<string, string, Action<string>, string>? _subscribe;
    private Delegate? _deadPublish;
    private double _nextLookup, _nextSubscribe;
    private readonly ConcurrentQueue<string> _inbox = new();
    private static readonly JsonSerializerSettings Json = new() { NullValueHandling = NullValueHandling.Ignore };

    /// <summary>Why the bridge can't be used right now (null when it can).</summary>
    public string? Why { get; private set; } = "the AI Bridge plugin is not loaded yet";

    public bool Available => _publish != null;

    public void Refresh(PluginBridge bridge, double now)
    {
        if (now < _nextLookup) return;
        _nextLookup = now + 2;
        Func<string, string, string>? publish;
        Func<string, string, Action<string>, string>? subscribe;
        try
        {
            publish = bridge.GetMethod<Func<string, string, string>>(PublishName);
            subscribe = bridge.GetMethod<Func<string, string, Action<string>, string>>(SubscribeName);
        }
        catch (Exception ex) { _publish = null; _subscribe = null; Why = $"PluginBridge lookup of {PublishName}: {ex.Message}"; return; }
        if (publish == null || subscribe == null)
        {
            _publish = null; _subscribe = null;
            Why = $"PluginBridge has no {(publish == null ? PublishName : SubscribeName)}: Whats An AI Bridge is not loaded, or a build before pub/sub";
            return;
        }
        if (ReferenceEquals(publish, _deadPublish)) { _publish = null; _subscribe = null; Why = "Whats An AI Bridge was unloaded (its methods answer bridge_reloaded)"; return; }
        var newLoad = !ReferenceEquals(subscribe, _subscribe);
        _publish = publish; _subscribe = subscribe; Why = null;
        if (!newLoad && now < _nextSubscribe) return;
        _nextSubscribe = now + 30;
        try
        {
            var r = JObject.Parse(subscribe(_subscriber, PreviewKind, json => _inbox.Enqueue(json)));
            if (r["ok"]?.Value<bool>() != true) Why = $"{SubscribeName}: {r["message"] ?? r["error"]}";
        }
        catch (Exception ex) { Why = $"{SubscribeName}: {ex.Message}"; }
    }

    /// <summary>Publishes one event; false (Why says why) when the bridge is absent or refused it.</summary>
    public bool Publish(string kind, object data)
    {
        var publish = _publish;
        if (publish == null) return false;
        try
        {
            var r = JObject.Parse(publish(kind, JsonConvert.SerializeObject(data, Json)));
            if (r["ok"]?.Value<bool>() == true) return true;
            Why = $"{PublishName}({kind}): {r["message"] ?? r["error"] ?? "no answer"}";
            if ((string?)r["error"] == "bridge_reloaded") { _deadPublish = publish; _publish = null; _subscribe = null; _nextLookup = 0; }
        }
        catch (Exception ex) { Why = $"{PublishName}({kind}): {ex.Message}"; }
        return false;
    }

    /// <summary>The data object of each preview message received since the last call (Tick, main thread).</summary>
    public bool TryTakePreview(out JObject? data)
    {
        data = null;
        if (!_inbox.TryDequeue(out var json)) return false;
        try { data = JObject.Parse(json)["data"] as JObject; } catch { }
        return true;
    }

    public void Close()
    {
        try { _subscribe?.Invoke(_subscriber, PreviewKind, null!); } catch { }
    }
}
