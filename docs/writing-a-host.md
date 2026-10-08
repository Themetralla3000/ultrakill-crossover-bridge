# Writing a host

This is the step-by-step guide for putting V1 into **your** game. You write the *host*: a plugin or mod inside the game that speaks the bridge protocol. The ULTRAKILL guest in this repository stays unchanged.

The protocol and the host-side design are [Minecraft Ring](https://github.com/siddoff/Minecraft-Ring)'s and [minecraft-crossover-bridge](https://github.com/justbustin/minecraft-crossover-bridge)'s. Minecraft Ring's Elden Ring DLL is the reference host; [protocol.md](protocol.md) is the exhaustive lookup table. This guide is the order to build things in and the conventions that cost people days when guessed wrong.

- [1. What your game has to provide](#1-what-your-game-has-to-provide)
- [2. Tools you have](#2-tools-you-have)
- [3. Rules that apply everywhere](#3-rules-that-apply-everywhere)
- [4. Milestones](#4-milestones)
- [5. Exact conventions](#5-exact-conventions)
- [6. Testing with the fake host](#6-testing-with-the-fake-host)
- [7. Example mappings](#7-example-mappings)
- [8. Pitfalls checklist](#8-pitfalls-checklist)

## 1. What your game has to provide

The guest never sees your game's objects. It sees numbers in shared memory. Your host must be able to, from inside the game process:

| Need | Used for |
| --- | --- |
| Run code every frame on the game's main thread | everything below |
| Read the player character's feet position and heading, and its health | state, hunter events, life |
| Override the camera (position, look direction, up, vertical FOV) | V1's eyes |
| Move the player character to a position and facing, keep it alive and hidden | the stand-in |
| Raycast against static world geometry | terrain (V1 walks on your world) |
| List enemies with positions, sizes and health, and damage them | combat |
| Draw a full-screen image on top of the final frame | V1's arm, gun, HUD and effects |
| Window rectangle and focus of the game window | the input window glued over it |

Optional: "what can I interact with" (doors, pickups) and F8 hand-over to the game's own controls.

You do **not** need to port any ULTRAKILL content or physics: V1 moves in ULTRAKILL, against invisible colliders the guest builds from your ray hits.

## 2. Tools you have

| Tool | Where | Use |
| --- | --- | --- |
| `bridge_protocol.h` | [`protocol/c/`](../protocol/c/bridge_protocol.h) | The authoritative layout for C/C++ (and a reference for any other language). Packed to 4 bytes, little-endian. |
| `UltrakillBridge.Protocol` | [`protocol/csharp/`](../protocol/csharp/UltrakillBridge.Protocol) | C# mirror of the structs and constants (`Ermc*`, `Protocol`), `MappedFile` (kernel32 mapping) and `BridgePaths`. netstandard2.1, so it loads in Unity/Mono. |
| `UltrakillBridge.HostSdk` | [`host-sdk/csharp/`](../host-sdk/csharp/UltrakillBridge.HostSdk/HostLink.cs) | `HostLink` and `HostFrames`: the host side of the protocol in C#. It does everything *except* the game-specific parts: opens and initialises `bridge.shm`, seqlocks, control staleness (1000 ms), ray mailbox with a time budget, entity table, damage ring, hunter events, prompt/action handshake, and the `frames.shm` reader with pose history and slot picking. |
| Fake host | [`fake-host/`](../fake-host/UltrakillBridge.FakeHost) | A complete small host (WinForms, software-rendered wireframe world with a pit, walls, stairs, a ramp, a door and five enemies). It is the executable specification: read `HostSim.cs` for the state machine. |
| Tests | [`tests/`](../tests/UltrakillBridge.Tests) | Layout checks against the header, `HostLink` <-> `GuestLink` round trips. |

Hosts in C# can reference `HostSdk` directly (copy the two small projects into your plugin solution, or reference the DLLs). Hosts in C/C++ include `bridge_protocol.h` and map the files themselves; `HostLink.cs` is a readable reference of the exact memory-ordering rules. Anything that can memory-map a file works.

## 3. Rules that apply everywhere

**Who writes what.** Every field has exactly one writer ([protocol.md section 2](protocol.md#2-bridgeshm-layout-field-ownership-heartbeats-liveness)). The host writes: `ErmcGameState`, `ErmcHunterEvents`, the entity table, ray results, the prompt, `hostHeartbeat`, `hostLife`, `hostDeaths`, `mcSwitchReq`, `hostActionAck/Result`. The guest writes: `ErmcControl`, the damage ring, the ray requests, `mcHeartbeat`, `mcDeaths`, `hostFocusReq`, `mcActionReq`, `frames.shm`.

**Seqlocks.** Blocks that are written while the other side may read use a sequence counter: bump to odd, write the payload, bump to the next even value. Readers retry if `seq` is odd or changed. **Never leave `seq == 0` after publishing**: both sides treat 0 as "never published". Use `Volatile`/atomics and barriers (C#: `HostLink` does it).

**Threads.** Do everything on the game's main thread, once per game frame, in the order of [protocol.md appendix A](protocol.md#appendix-a-reference-host-per-frame-order). Reading or writing the game's objects from another thread is how hosts crash.

**Liveness.** The guest decides the host is alive when `hostHeartbeat` changes within 2 s. You decide the guest is driving only while `ErmcControl.seq` keeps changing (it must change at least every 1000 ms); otherwise **release everything** (camera, stand-in, compositing).

**Release on every error path.** Guest crash, scene change, pause, a player death: put the game back (camera, character visibility, movement, health). The reference compositor does not time out `COMPOSITE`; yours should.

**One guest.** There is no ownership field; assume one ULTRAKILL process.

## 4. Milestones

Build in this order. Each milestone is testable with the real guest; do not move on before it works.

### M0. Connect, state and heartbeat

1. Resolve the bridge directory like the guest: `UKBRIDGE_DIR` env var, else `ERMC_DIR`, else `%TEMP%\ermc` (`BridgePaths.Dir`). Read the folder from a config file or from your launcher; the scripts export both variables.
2. Open or create `<dir>\bridge.shm` (8 MiB), initialise it if the magic is missing (`HostLink.Open`): zero `[0, 0x100000)`, `version = 1`, `size = 8 MiB`, then the magic last. Set `hostPid` (your game's process id: **the guest finds your window through it**) and `hostStartMs`. Set `coreStatus = 1` once ready (the launch scripts wait for it).
3. Every game frame: `hostHeartbeat++` and publish `ErmcGameState` through the seqlock (`HostLink.WriteState`), with at least `frame`, `unitsPerMeter = 1.0` and flags.
4. Write `count = 0` into the passage and platform table headers once (they are not cleared by init and the guest ignores them).

Test: start the host, start ULTRAKILL with `scripts/Launch-Guest.ps1`, press F9 in ULTRAKILL: the overlay says the host is alive and shows your flags and zone.

### M1. Stand-in and camera

1. **Life state machine** (`NONE -> SETTLING -> ALIVE -> DEAD`). Publish `PLAYER_VALID | CAMERA_VALID` only while ALIVE (loading screens, menus, cutscenes are not ALIVE). Set `HOST_BUSY` when dead, loading or paused so that the guest draws nothing. Details: [protocol.md 6.4](protocol.md#64-life-and-death-counters).
2. When the character becomes usable at a place the guest did not choose (after loading, respawn, teleport), **`hostLife++`**. The guest answers with a *recall*: it moves V1 to `playerPos` facing `playerQuat`. Do not bump it for ordinary walking.
3. Publish while ALIVE: `playerPos` (the character's **feet**), `playerQuat` (rotation about +Y; yaw formula in [5.5](#55-hunter-yaw)), `stageId` (see [5.3](#53-the-stable-frame-and-stageid)), plus the game camera as `camPos/camTarget/camUp/fovYDeg/nearZ/farZ/aspect`.
4. **Control.** Each frame call `HostLink.UpdateControl()` (staleness included). While `ControlActive`:
   - `OVERRIDE_CAMERA`: after the game resolved its own camera, write the guest camera: `forward = normalize(camTarget - camPos)`, right/up from `camUp` (roll is honoured), vertical FOV from `fovYDeg` if `5 < fov < 170`. **Aspect, near and far stay the game's.** Set `CAM_OVERRIDDEN` in your next state.
   - `MOVE_HUNTER`: pin the character to `hunterPos` (feet) and `hunterYawDeg`: no gravity, no collision, no AI-driven movement, no player input.
   - `HIDE_HUNTER`: make the character invisible (the guest draws the arm and gun).
   - Keep the character alive while pinned and report the damage it would have taken (M5).
5. When control is inactive or `flags == 0`, **undo all of it** and give the game's own input back (this is also what F8 uses).

Test: V1's view appears in your game; strafing and turning in ULTRAKILL moves your camera. (Without terrain V1 falls: the guest holds it on a small temporary floor for a few seconds after a recall.)

### M2. Window rectangle

Publish `winX/winY/winW/winH` (client area in screen coordinates), `bbW/bbH` (back-buffer size) and the flags `WINDOW_VALID` and `WINDOW_FOCUSED`. The guest sizes its input window and its render targets from them, so **render ULTRAKILL's image at your aspect**: if the guest frame has another aspect than your back buffer it is stretched. Handle DPI (publish physical pixels, make the host process DPI aware).

Test: the ULTRAKILL window appears over yours, borderless and almost invisible, and follows your window when you move or resize it.

### M3. Ray mailbox

Service `OFF_RAYS` once per game frame **only while ALIVE** (`HostLink.ServiceRays(cast)`; give it a delegate that raycasts in your engine). The guest sends batches of up to 8192 rays `(start, end)` in the stable frame and reads `hit` and `pos` for each; it builds colliders from the results.

- Cast against **static world geometry only**: no characters, no triggers, no props that move.
- On a hit: `hit = 1`, `pos` = hit point, `normal` synthesised (the SDK does it: downward ray gives `(0,1,0)`, else minus the ray direction), `attr = 0`. On a miss everything is zero. The guest derives slopes from neighbouring heights, not from normals.
- Budget about 2 ms per frame; a batch may span several frames (`processed` is resumable). Rays that start inside solid geometry may miss; that is normal.
- Stale `reqSeq != respSeq` from a dead session is answered first.

Test: V1 stands and walks on your ground. F9 in ULTRAKILL shows the terrain status, and F10 draws the rebuilt colliders (floors green, walls red, ceilings blue) into the frame you composite.

### M4. Entity table and damage ring

1. Each ALIVE frame publish the hostile characters near the player (reference: within 80 m, up to 256) with `HostLink.PublishEntities`: stable `id` (an object handle; ids must not alias across zones), `kind` (`1` large, `2` small, `3` other/non-hostile), `pos` (feet), a world-aligned box `boxCenter/boxHalf` (centre at `feet + height/2`, half extents `radius, height/2, radius`), `hp`, `maxHp`, name, and `flags` bit0 = dead, bit1 = box is world-aligned. Set `count = 0` when not ALIVE.
2. Consume the guest's damage ring (`HostLink.DrainDamage`). Entry: `id`, `amount` (float), `hitPos`, `flags`. Validate: `id` must be an entity you published last frame, `0 < amount < 10000`, target hostile and alive. Convert `amount` to your damage as in [5.6](#56-damage-amount-and-how-to-invert-it) and apply it **as a hit from the player** so kill credit, XP and aggro work.
   *Stat damage (optional, for hosts with their own damage model):* publish `HostLink.WriteHostCombat(...)` (the character's damage stat, crit, and the per-weapon `k`/`proc` table from `WeaponTable`), then set `Protocol.HostFlagStatDamage` in `WriteHostEvents`. The guest then sends `amount` = raw ULTRAKILL damage with the weapon id, hit count and shot sequence in `reserved` ([protocol.md 6.2.1](protocol.md#621-stat-damage-entries-optional-extension)). Apply `bodyDamage * scale * k[weapon] * amount` (x weak point multiplier), roll crit once per `(weapon, shot sequence)` with `ShotRollCache`, and use `ProcBudget.Entry(proc[weapon], hitCount, cap)` as the proc coefficient. Entries with the `FRACTION` flag (parry) keep the legacy conversion. A host that never sets the bit keeps the fraction encoding unchanged.

   *RoR2-style stats (optional, for hosts with movement / attack speed / jumps / cooldowns):* every frame `HostLink.WriteHostRatios(attackSpeed, moveSpeed, extraJumps, jumpPower, sprintSpeed, rechargeSecondary, rechargeSpecial, rechargeUtility)` with ratios against your character's base stats at its current level (1.0 = unchanged; build them with `StatsWire.Ratio`, `StatsWire.ExtraJumps`, `StatsWire.Recharge`, `StatsWire.SprintRatio`), then set `Protocol.HostFlagStats`. Never publish absolute values: the guest applies its own gain and caps, and a level 1 character without items must publish 1.0. Details: [protocol.md 8.5](protocol.md#85-stat-ratios-optional-extension-hostflag_stats). `tools`: the fake host publishes fake ratios (`Y`, `1`..`9`).

   *Host-authoritative health (optional, for hosts with real health, shield, healing and death rules):* every frame `HostLink.WriteHostHealth(health, fullHealth, shield, fullShield, barrier, curse, dead)`, then set `Protocol.HostFlagOwnsHealth`. V1's bar then mirrors your character (`HealthWire.UkHp`), blood and parry heals arrive as the cumulative `ErmcGuestRequests.healMilli` (heal `HealthWire.HealAmount(HealthWire.MilliDelta(now, last), fullHealth, scale)`), and `combatFlags` tells you when V1 dashes or is in its hurt i-frames (ignore incoming damage then) and when a punch is parrying (reject one close melee hit, report it with `Protocol.HunterKindParried` ORed into the kind). Do not let a lethal hit kill the character on your own: keep it at 1 HP, publish `dead`, ignore damage, and apply your death policy when the guest bumps `mcDeaths` (or on your own timeout). Details and the exact rules: [protocol.md 8.4](protocol.md#84-host-authoritative-health-optional-extension-hostflag_owns_health). `tools`: the fake host implements all of it (`O`, `G`, `J`).
3. `id == 0` with `WORLD_RAY` is a "strike the world" request (breakable props); implement or ignore it.

Test: enemy proxies appear in ULTRAKILL (F9 shows the counts); shooting them in ULTRAKILL hurts the real enemies, and they die with your game's death handling.

### M5. Hunter events and life counters

- **Hunter events** (host -> guest damage): when the pinned character loses HP, call `HostLink.ReportHunterHit(damage, attackerFeetPos, hunterMaxHp, frame, attackerKind)`. The guest converts `share = damage / hunterMaxHp` into V1 damage (and runs its parry check against it), so the units of `damage` only need to be consistent with `hunterMaxHp`. `hunterMaxHp` must be non-zero from the first event on. Then **refill the character's HP** so it never dies while stood in.
- **Deaths**: `mcDeaths` increments when V1 dies: kill (or respawn) the character the way your game does it, and do not echo it back. `hostDeaths++` when the character dies without the guest asking (killed by the world, fell out of the map): the guest kills V1.
- `hostLife++` on respawn, as in M1.

Test: stand in front of an enemy: V1's health drops; let V1 die: your character dies; die in your game: V1 dies.

### M6. Compositing through frames.shm

The guest renders V1's world effects, viewmodel and HUD into transparent layers and writes them to `frames.shm`; you draw them over your finished frame.

1. When `COMPOSITE` is set in the control block, open `frames.shm` (`HostFrames.TryOpen`; it opens the existing file only and retries every 2 s).
2. Each frame: `NoteAppliedPose(control.mcFrame)` when you applied a camera, then `PickSlot(PoseForPresent(control.poseLag))`, then `CopySlot` into your textures (re-validates `seq`). Slot choice by pose id is what keeps the arm in sync with the camera you actually rendered, so keep it.
3. Draw full screen, premultiplied alpha, over the final back buffer: `scene = hand + world * (1 - hand.a)`, `out = gui + scene * (1 - gui.a)`. Layers are BGRA bytes, **bottom-up rows**, no padding; depth is float32 in OpenGL window depth.
4. Set `COMPOSITING` in the state while drawing; stop (and clear it) when `COMPOSITE` drops, the guest dies, or the game is paused.

Start without depth: a screen-space overlay with two or three images is enough to see V1's arm and HUD. Add depth occlusion later by comparing the linearised guest depth to your scene depth ([5.8](#58-compositing-layers-and-depth)); until then V1's world effects show through walls.

### M7. Actions and prompt

If your game has "press E to open" style interactions:

- Each frame while ALIVE publish the current prompt text (UTF-8, at most 63 bytes, empty string = none) with `HostLink.SetPrompt`.
- When `PollActionRequest(out req)` fires, perform the interaction your character is looking at and answer with `AckAction(req, result)`: `1` done, `0` nothing to do or blocked, `-1` unsupported, `-2` needs the game's own controls (ladders, menus). The guest re-samples the terrain after a `1`, so doors and levers that move collision work.
- The prompt depends on the *character's* position and facing, i.e. on the fidelity of `hunterPos` and `hunterYawDeg`.

#### Making interactions feel native (optional)

- **Draw your own prompt.** If your game already shows a context prompt with a highlight and cost, publish `Protocol.HostFlagDrawsPrompt` in `WriteHostEvents(..., flags)`. The guest then hides its `[V] Open` label (`[Interaction] ShowGuestPrompt = Auto`) and still uses the text you publish with `SetPrompt` to know that a target exists (hold-to-repeat). Read `ReadGuestRequests` + `InteractKeyName` to show the guest's key in your glyph.
- **Hold to repeat.** The guest repeats `mcActionReq` while its interact key is held and a prompt exists (every 250 ms by default); just answer each request.
- **Equipment and ping.** Poll `HostLink.ReadGuestRequests`; baseline the counters on the first read, then every change of `useEquipment` / `ping` is one use of the player's active item / one ping, aimed along the guest camera (`ErmcControl`). Respect cooldowns and authority exactly like your own input path.
- **Menus.** While a UI that needs the mouse is open, set `Protocol.HostFlagNeedsInput`: the guest hands input to your window and takes it back when you clear the flag (debounce it).

### M8. F8

- Guest F8: the guest writes `flags = 0` and `hostFocusReq++`. On a change, bring your window to the front and give the game's controls back.
- Your F8: poll the key (even when unfocused); when your window is foreground and F8 goes down, `mcSwitchReq++` (`HostLink.BumpSwitchRequest`). The guest recalls and takes over again after 400 ms.

### M9. Weapon progression (optional)

If your game has a run structure, publish run info and the guest can hand out weapons as the player progresses ("start with the revolver, every boss defeated unlocks a random new weapon"):

- Call `HostLink.WriteHostEvents(loadoutMode, runSeed, counters)` once per frame (it only touches memory on change). `loadoutMode`: `Protocol.LoadoutProgression` (2), `LoadoutAll` (1) or `LoadoutGuest` (0, no opinion).
- `runSeed` must be stable for the whole run and different between runs (the unlock order is derived from it, so the same run always unlocks the same weapons). Change it, and zero the counters, when a new run starts.
- `counters[Protocol.CtrBossesDefeated]` = number of major bosses defeated this run (the only counter the guest uses today), `CtrStagesCleared` = stages cleared.
- The guest never writes the player's ULTRAKILL save: it uses the game's forced-loadout mechanism. Hosts that do not publish the block keep the player's own loadout.

## 5. Exact conventions

### 5.1 Units and handedness

- Lengths are **metres**, `unitsPerMeter` is published as `1.0`. The guest scales its own world (`MetresPerUnit`, default 0.5 metres per ULTRAKILL unit, so V1 is 1.75 m tall; change it in the guest config if your characters are much bigger or smaller).
- The host frame is **left-handed, Y up, +Z forward** (Unity, Unreal after a swizzle, D3D engines). If your engine is right-handed (OpenGL-style, Minecraft, Godot, Source), mirror one axis (usually Z) at the boundary, **consistently for every position, direction, ray, box and camera**, and flip the yaw sign.

### 5.2 Feet positions

`hunterPos`, `playerPos`, entity `pos` and `lastHitFrom` are **feet** (the bottom of the body). Boxes are built upward from them.

### 5.3 The stable frame and stageId

Every position that crosses the protocol lives in one coordinate frame that **does not move** while the player plays a "zone". `stageId` names the zone:

- It must be non-zero (and not `0xFFFFFFFF`) while the state is valid, and it must **change whenever the frame changes**: a scene or level load, a new run, a teleport that re-bases the world.
- Engines with floating origins or streamed worlds must convert to a stable frame (for example world position plus the tile offset) before publishing, and convert back when applying guest data (`hunterPos`, camera, ray starts).
- The guest anchors the first valid state per zone, treats `stageId` as opaque, and stops driving until it has re-anchored after a change. Entities, ray results and platforms are valid only for the current zone. After a zone change, give the guest a moment: publish an empty entity table until the new zone's entities are real.

### 5.4 Camera

`camTarget` only has to be any point along the look direction. `camUp` carries roll. FOV is **vertical** degrees. The guest renders at your aspect (M2). Apply the override after your game resolved its own camera each frame so the guest wins, and give the camera back smoothly when control ends.

### 5.5 Hunter yaw

`hunterYawDeg` follows the Minecraft yaw convention (the protocol was written for it): the character faces `(-sin y, -cos y)` in host `(x, z)`. The reference host writes the physics quaternion `q = (0, sin(y/2), 0, cos(y/2))` with `y = hunterYawDeg * pi/180`.

For a Unity-handed host with the same axes: if the character's horizontal facing is the unit vector `f = (fx, fz)`, then `hunterYawDeg = deg(atan2(-fx, -fz))`; equivalently, from a Unity heading `psi = eulerAngles.y` (forward `(sin psi, cos psi)`), `psi = hunterYawDeg - 180`. The guest sends `hunterYawDeg = psi + 180` and reads your `playerQuat` back with `a = 2 * atan2(qy, qw)`, `forward = (-sin a, -cos a)`. If your frame is rotated about +Y relative to the stable frame, add that rotation before converting.

A wrong sign here mirrors the stand-in's facing, which breaks interaction prompts and enemy aggro direction.

### 5.6 Damage amount and how to invert it

The guest sends `amount = fraction * mcHealth(maxHp)` where

```
mcHealth(maxHp) = clamp(20 * sqrt(maxHp / 100), 10, 300)
```

and `fraction` is the share of the target's **max HP** the ULTRAKILL hit is worth (the guest config `HostHpPerUkHp` decides how many host HP one ULTRAKILL health point is worth, 60 by default). The reference host computes `hp = ceil(amount * maxHp / mcHealth(maxHp))`, minimum 1, which is exactly `ceil(fraction * maxHp)`.

**To invert** (you want fractions): `fraction = amount / mcHealth(maxHp)` using the `maxHp` you published for that entity, then remove `fraction * maxHp` of your own health unit. Apply `Math.Max(1, ceil(...))` if your game has integer HP. Guard: drop `amount <= 0` or `>= 10000`, and `id`s not in last frame's table. The `flags` field is `0` for normal hits; `NOT_BY_PLAYER` (bit2) means "environmental, no kill credit"; `CRITICAL` and `OUTWARD` have no meaning.

Arithmetic examples: `maxHp = 100` gives `mcHealth = 20` (one point = 5 HP); `maxHp = 6000` gives about 154.9 (one point = 38.7 HP).

Host-to-guest damage is simpler: report the HP your stand-in lost and its max HP; the guest uses the ratio.

### 5.7 Ray normals and hits

`normal` is **synthesised, not measured**: `(0,1,0)` for downward rays, else the negative ray direction. `hit` is always 1 on a hit. `attr` is always 0. Return no distance; the guest derives it from `pos`.

### 5.8 Compositing layers and depth

Slot layers, in memory order after the 0x100-byte header: `world` BGRA, `depth` float32, `gui` BGRA, `hand` BGRA (optional, flag bit2), all `width * height` pixels, **bottom-up** (row 0 is the bottom scanline) and **premultiplied**. Background alpha is 0.

Depth is **OpenGL window depth** `d` in [0,1], 1.0 where nothing was drawn. Linearise with the slot's `mcNear/mcFar` (published in host metres): `z = 2d - 1; eyeDist = 2*n*f / (f + n - z*(f - n))`. Treat the guest pixel as occluded if `eyeDist > hostDistance + 0.03 + 0.004*eyeDist`. World pixels with alpha > 0 and `d == 1.0` are drawn unoccluded (translucent effects). The hand and GUI layers are never occluded.

### 5.9 Timeouts

| Rule | Value |
| --- | --- |
| Control `seq` unchanged | 1000 ms -> release camera and stand-in |
| `COMPOSITE` | the reference host has no timeout; yours should release it with the control |
| Guest alive | `mcHeartbeat` changes within 2000 ms |
| `mcDeaths` honoured if | the character was stood in within 2000 ms, ALIVE, not dead |
| Recall settle (guest side) | the guest waits 400 ms after teleporting V1 before driving |
| `frames.shm` open retry | 2000 ms |
| Ray budget | about 2 ms per frame |

## 6. Testing with the fake host

The fake host is the other half of the protocol: it behaves like a host so you can develop the guest, and it behaves like the reference to show you what yours should do.

```powershell
dotnet build UltrakillBridge.sln -c Release
.\scripts\Run-FakeHost.ps1 -WithGuest     # fake host + ULTRAKILL as guest
```

To test **your host against the real guest**: start your game with the bridge folder set (both `UKBRIDGE_DIR` and `ERMC_DIR`, or `scripts/Launch-Guest.ps1 -BridgeDir <your folder>` for the guest side), then `.\scripts\Launch-Guest.ps1 -BridgeDir <folder>`.

Weapon progression in the fake host: it requests `Progression` by default; press `B` (or the panel button) to count a boss defeated, `N` for a new run (new seed, counters to 0), `M` to cycle the requested mode (guest / all / progression).

Stat damage in the fake host: it advertises the capability by default (`T` or the panel button toggles it) with a fake character (damage 12, crit 10 % x2), applies `damage * k * amount`, rolls crit per shot, and lists damage, hits and procs per second per weapon over the last 5 s in its overlay. Turn on `[Combat] HitLog` in the guest to record the same hits to `hitlog.csv`.

Diagnostics in ULTRAKILL:

| Key | Shows |
| --- | --- |
| F9 | Host alive, state flags, zone, driving, recall, terrain, enemies, capture and window status |
| F10 | The rebuilt terrain colliders (floors green, walls red, ceilings blue) inside your game if you composite |

Logs: `dist\guest\BepInEx\LogOutput.log` (guest) and your own host log. The guest logs every recall, zone anchor, release and failure once.

If you want to verify the layout of your own implementation, `tests/UltrakillBridge.Tests` shows how to check sizes and offsets against `bridge_protocol.h`, and its round-trip tests drive `HostLink` and `GuestLink` against each other in memory.

## 7. Example mappings

### 7.1 Elden Ring (Minecraft Ring's DLL, the reference host)

| Protocol piece | Elden Ring implementation |
| --- | --- |
| Stable frame / `stageId` | The open world is stitched from tiles: global = tile-relative + 256 m * (gridX, gridZ); zone = area id `<< 24`. Legacy dungeons use block-relative coordinates and the full block id as zone. A seamless block change keeps the zone id. |
| Life state machine | Loading screen = NONE; SETTLING waits until the character stops moving (90 ticks after load or respawn); `hostLife++` on ALIVE; DEAD when the character dies. |
| Stand-in | The player's own character: physics position written every tick, NoMove, gravity off, NoDead, HP refilled, render flag cleared. Reach assist squares it to doors within 2 m. |
| Camera | Writes the game's camera matrix after the game's own camera step; FOV; keeps the game's aspect. |
| Rays | The game's `CastRay` with a "map geometry and props, not characters" filter, ~2 ms per tick. |
| Entities | The game's character list within 80 m: capsule radius and height from the physics module, name `cNNNN`. |
| Damage | `ModifyHp` plus a kill call (death animation, drops, runes), a "poke" bullet for hit reaction and aggro. |
| Prompt / action | The game's action-button manager: the selected prompt's text, and an "event action tapped" latch for the action. |
| Compositor | D3D12 Present hook that draws the layers with depth and relighting. |

The DLL is built unchanged from Minecraft Ring's sources (pinned commit in `THIRD_PARTY_NOTICES.md`); UltraRing (the project this kit was extracted from) drives it.

### 7.2 Risk of Rain 2 (design summary, not shipped here)

A BepInEx 5 plugin using `HostLink` from the SDK, with no Harmony patches needed for a first version. The mapping, in the order of the milestones:

| Protocol piece | Risk of Rain 2 implementation |
| --- | --- |
| Units / frame | Unity: left-handed, Y up, +Z forward, 1 unit = 1 m, no floating origin. Stable frame = Unity world space; no mirror. |
| `stageId` | `((sceneIndex + 2) << 16) | (loadCounter & 0xFFFF)`, bumped in `Stage.onStageStartGlobal`; a re-entered stage is a new zone. |
| Life | NONE with no run or menu scenes; SETTLING for pods, falling in, a foreign camera override, a teleport and 1 s after a new body; ALIVE when the body is alive and still; DEAD when the body dies. `Time.timeScale == 0` sets `HOST_BUSY`. |
| Stand-in | The local player's own `CharacterBody`, pinned each `FixedUpdate` through the KCC motor with collision solving off, made a fake actor (non-colliding), hidden with the invisibility counter, kept alive by restoring health in `GlobalEventManager.onServerDamageDealt` (which fires before the death check). The damage actually lost (after armor) feeds the hunter events. |
| Camera | A custom `ICameraStateProvider` installed with `CameraRigController.SetOverrideCam`; returning `IsUserControlAllowed = false` stops RoR2 feeding input into the body. Releasing the override hands the camera back smoothly. |
| Rays | `Physics.Raycast` on the world layer mask only, triggers ignored, ~2 ms budget via `HostLink.ServiceRays`. |
| Entities | `CharacterBody.readOnlyInstancesList` with hurtbox bounds; ids include the stage-load counter so they cannot alias across stages. |
| Damage | `ErHp` conversion as in 5.6 (optionally compensating armor), then a `DamageInfo` through the same sequence as `BulletAttack`, `HealthComponent.TakeDamage` and `GlobalEventManager.OnHitEnemy`, so items proc and kills give gold and XP. |
| Prompt / action | The local player's `InteractionDriver.currentInteractable`: `IInteractable.GetContextString` for the prompt, `Interactor.AttemptInteraction` for the action. |
| F8 | `GetAsyncKeyState` for the host key; `hostFocusReq` brings the window forward and releases the override. |
| Compositor | A `ScreenSpaceOverlay` canvas with 2-3 `RawImage` layers fed from `HostFrames`, premultiplied through RoR2's `Hopoo Games/UI/Custom Blend` shader; depth occlusion is a later milestone. |

Main risks identified there: overkill damage killing the stand-in before the health restore, other systems taking the camera override (pods, cutscenes), the per-frame texture upload cost, DPI/window-rect mismatches. The full design lives in the UltraRing repository's research notes.

## 8. Pitfalls checklist

- Leaving `seq == 0` after a publish, or not bumping it, so the other side thinks nothing was published.
- Forgetting `hostPid`: the guest finds your window through it.
- A zone that changes silently (no `stageId` change) or a `stageId` that changes every frame.
- Mixing handedness for some vectors but not others.
- Publishing the character's centre or eye as "feet".
- Camera applied before the game's own camera step (the game overwrites it).
- Letting the pinned character fall, collide, take fall damage or die.
- Ray results from before a zone change, entity ids that alias after a level load.
- Applying guest damage to non-hostile or dead targets, or to ids not in last frame's table.
- Not releasing control when the guest stops writing (1000 ms), leaving `COMPOSITE` on after the guest died.
- Drawing the guest frame at the wrong aspect, top-down instead of bottom-up, or non-premultiplied.
- Doing protocol work off the main thread.
- Forgetting that stale data above offset 0x100000 survives a restart.
