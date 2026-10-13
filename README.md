# Whats A Health Bar

Crisp, animated health bars for the Path of Exile 2 HUD (ExileCore2), built to cost the same with 1 or 500 bars on screen.

## What you see
- **One bar per kind:** unique, rare, magic, normal, minions/allies, other players, and you. Each kind has its own size, color, segment notches and life %.
- **Hits:** the fill drops at once, a lighter **damage trail** holds briefly and then drains, and the bar's edge flashes.
- **Heals:** the bar fills smoothly with a glow.
- **Spawn and death:** bars fade in when a monster appears and out when it dies.
- **Low life:** rare and unique bars pulse.
- **Energy shield:** a thin strip across the top of the bar.
- **Preview in game:** draws sample bars mid-screen with fake hits and heals, names, status icons, a damage-over-time chunk and an immune sample, so you can tune the look in town.

## Information layers (ported from syrairc's ExileNameplates)
- **Names and data texts:** per kind, a template above, on and below the bar: `{name} {hp} {hpmax} {hp%} {life} {life%} {es} {es%} {ward} {ward%} {dist} {rarity} {dps} {ttk} {dotloss} {dotdps}`. Names show on rares and uniques by default; long names are abbreviated (`S. S. Captain`). A bar's string is rebuilt only when the values it shows change.
- **Status icons:** ailments, curses, exposure, armour break and buffs as icons next to the bar (above, below, left or right, in a grid). Each icon sits on a dark plate with a ring in its own color, stacks in a small pill, and a thin line along its foot drains with the time left (seconds as text optional). The list ships with PoE2 buff ids read from the game's own data (several also seen on monsters in combat) and is edited in the settings (search the game's buff list, pick a glyph from the grid) or through the bridge.
- **States:** invulnerable monsters (the `CannotBeDamaged` stat, e.g. a boss's immune phase) get a grey bar with light diagonal stripes; cullable monsters get a bright post at their culling threshold by rarity (normal 30 %, magic 20 %, rare 10 %, unique 5 %, times your multiplier), and the fill and outline take the cull color once under it; hidden monsters are hidden or dimmed.
- **Damage over time:** a dark chunk at the end of the life fill shows what the running ignite, bleed or poison will still take, with a light edge where the fill will end up.
- **DPS and time to kill:** a moving average of the damage each monster takes, as `{dps}` and `{ttk}`.
- **Overrides:** a look for specific monsters by metadata path (or name) regex: colors, opacity, grey, hatch, or hide.
- **Ignore list and the why overlay:** Debug > Why overlay shows over each monster why its bar is or isn't drawn, with a `[+ ignore]` button. Untargetable monsters are hidden by default (uniques keep their bar).

The icon glyphs, the ideas for these layers and the shape of the settings panel come from **syrairc's [ExileNameplates](https://github.com/syrairc/ExileNameplates_POE1)** (PoE1), used with his permission. `textures/status-atlas.png` is his atlas, downscaled into one texture by `textures/build-atlas.ps1` (his original sheets are in `textures/source`; the bare glyphs are brightened to white so a tint draws at its own color, and a solid block is added for the overlays that must draw over the icons within their one draw command).

## Settings
The HUD menu shows a custom panel in tabs: **General** (where bars show, placement, animation, bridge), **Bars** (the shared look, then one sub-tab per kind with a live sample bar drawn by the engine itself, and "copy to all kinds, keep colors"), **Text** (the three templates per kind with token chips and the string the sample shows), **Status icons** (icon blocks, a search over the game's buff list, the configured statuses with an icon grid picker, tint, badge, scale and a bar patch), **States** (invulnerable, cullable with the per-rarity thresholds, hidden, damage over time, with an immune sample), **Overrides** (ordered, regex validity shown inline), **Ignore** (chips) and **Debug** (the why overlay and the live numbers). Every control has a tooltip. The settings stay ordinary `[Menu]` nodes underneath, so the control center edits them by reflection and the HUD saves them as before.

## Why it is cheap
| | Stock HealthBars | Whats A Health Bar |
|---|---|---|
| Components | `GetComponent<Life>()` on every access | looked up at the classify rate (10 Hz), held between passes |
| Position | `Entity.Pos` + `GetComponent<Render>` per frame | `Render.Pos` read once per frame, height cached at classify rate |
| Projection | `Camera.WorldToScreen` per bar | one `Camera.Snapshot` per frame |
| Distance | `DistancePlayer` (200 ms cache per entity) | squared grid distance at the classify rate (10 Hz) |
| Out-of-range monsters | checked every frame | cost nothing between classify passes |
| Off-screen bars | culled only beyond distance 80 | always culled |
| Drawing | one texture `DrawImage` per bar part, plus strings each frame | every bar's quads written straight into one ImGui draw list's vertex buffer after a single `PrimReserve`: no per-bar native calls, one draw command, no strings (percent text from a table) |
| JIT | the HUD compiles plugins with optimizations disabled | `AssemblyInfo.cs` declares its own `Debuggable` attribute, so the JIT optimizes it (about 4x on the hot path) |

The plugin times itself (Tick, Render split into build and flush, ns per drawn bar) and reports it through the AI Bridge.
Measured on the PoE2 HUD with the in-game stress preview (`{"stress":500}`): **500 animated bars cost about 0.24 ms per frame** (Tick 85 µs, Render 159 µs, about 480 ns per bar, 2026-10-12 with the information layers; the same as without them, 88 / 157 µs). The layers add no per-frame component reads: buffs and stats are read at the classify rate, text is rebuilt only when it changes, and all icons are one extra draw command.

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
- **Lists** (statuses, overrides, ignored paths) are not menu nodes; they have a section under the settings and bridge methods:
  - `WhatsAHealthBar.GetLayers()` returns `{schema, statuses, overrides, ignoredPaths, icons, badgeStyles, problems}`.
  - `WhatsAHealthBar.SetLayers(json)` replaces each list it is given (`{statuses?, overrides?, ignoredPaths?}`) and returns what didn't compile (invalid regex, unknown icon, bad color).
  - `WhatsAHealthBar.SearchBuffs(query)` searches the game's buff definitions (`[{id, name, invisible, listed}]`).
  - `WhatsAHealthBar.WhyNotDrawn(arg)` answers per entity whether its bar is drawn and the first reason it isn't (`arg`: empty for the nearest 40, an entity id, or text in the path or name).

## Build
Target `net8.0-windows`. Set `exilecore2Package` to your ExileCore2 HUD folder and put the folder in `Plugins/Source`.
