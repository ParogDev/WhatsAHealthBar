# Whats A Health Bar

Crisp, animated health bars for the Path of Exile 2 HUD (ExileCore2), built to cost the same with 1 or 500 bars on screen.

## What you see
- **One bar per kind:** unique, rare, magic, normal, minions/allies, other players, and you. Each kind has its own size, color, segment notches and life %.
- **Hits:** the fill drops at once, a lighter **damage trail** holds briefly and then drains, and the bar's edge flashes.
- **Heals:** the bar fills smoothly with a glow.
- **Spawn and death:** bars fade in when a monster appears and out when it dies.
- **Low life:** rare and unique bars pulse.
- **Energy shield:** a thin strip across the top of the bar.
- **Extras:** an optional culling-strike notch.
- **Preview in game:** draws sample bars mid-screen with fake hits and heals, so you can tune the look in town.

## Why it is cheap
| | Stock HealthBars | Whats A Health Bar |
|---|---|---|
| Components | `GetComponent<Life>()` on every access | looked up once per entity, held |
| Position | `Entity.Pos` + `GetComponent<Render>` per frame | `Render.Pos` read once per frame, height cached at classify rate |
| Projection | `Camera.WorldToScreen` per bar | one `Camera.Snapshot` per frame |
| Distance | `DistancePlayer` (200 ms cache per entity) | squared grid distance at the classify rate (10 Hz) |
| Out-of-range monsters | checked every frame | cost nothing between classify passes |
| Off-screen bars | culled only beyond distance 80 | always culled |
| Drawing | one texture `DrawImage` per bar part, plus strings each frame | every bar's quads written straight into one ImGui draw list's vertex buffer after a single `PrimReserve`: no per-bar native calls, one draw command, no strings (percent text from a table) |
| JIT | the HUD compiles plugins with optimizations disabled | `AssemblyInfo.cs` declares its own `Debuggable` attribute, so the JIT optimizes it (about 4x on the hot path) |

The plugin times itself (Tick, Render split into build and flush, ns per drawn bar) and reports it through the AI Bridge.
Measured on the PoE2 HUD with the in-game stress preview (`{"stress":500}`): **500 animated bars cost about 0.11 ms per frame** (Tick 14 µs, Render 101 µs, about 230 ns per bar).

## Pools: life, energy shield, ward
With **Stacked** (the default), the bar is as wide as life + ES + ward. Each pool fills its own share in its own color, in the order damage removes them: life on the left, then ES, then ward on the right. The bar drains right to left, the way the monster does.
- A notch marks the end of the life share.
- A mostly-ES monster reads as a mostly-ES bar with a short life stub. A warded one shows its ward cap. Mixed pools show all three.
- The trail, the flash and the percent use the combined total.
- **Life only** and **ES strip** (ES as a thin strip over the life bar) are the other modes.

## Bridge and control center
- `WhatsAHealthBar.GetState` (PluginBridge) returns the state as JSON: counts, the plugin's own timings, preview status and recent kills. The MCP tool `healthbars_state` reads it, and the control center's **Health Bars** page shows it live.
- **Pub/sub:**
  - It subscribes to `healthbars.preview` (`{seconds, focus}`), which the control center publishes to preview your edits in game.
  - It publishes `healthbars.kill` when a unique (or, optionally, also a rare) dies, with its name and time to kill.
- **Settings:** every setting has a label and tooltip, so the control center can edit all of them live.

## Build
Target `net8.0-windows`. Set `exilecore2Package` to your ExileCore2 HUD folder and put the folder in `Plugins/Source`.
