# Protocol reference

The wire protocol between a **host** (the game that owns the world) and the **guest** (ULTRAKILL with this repository's BepInEx plugin, which owns the player). It is a file-backed shared-memory protocol designed by [Minecraft Ring](https://github.com/siddoff/Minecraft-Ring) / [minecraft-crossover-bridge](https://github.com/justbustin/minecraft-crossover-bridge) (MIT). This repository does **not** change it: [`protocol/c/bridge_protocol.h`](../protocol/c/bridge_protocol.h) is their header, and [`protocol/csharp/`](../protocol/csharp/UltrakillBridge.Protocol) is a byte-exact C# transcription checked by the tests against the header's `static_assert`s.

How to read this document:

- Everything is little-endian. Lengths are metres, angles degrees unless stated. "Host" is the game being driven, "guest" is ULTRAKILL.
- The protocol was written for Minecraft as the guest and Elden Ring as the host, so some names are historical: structs are `Ermc*`, the player character the host represents is the "hunter" (`hunterPos`, `hunterYawDeg`), guest fields are `mc*`, and the file magic is "MHMC" (inherited from a Monster Hunter: World bridge). They are wire names; do not rename them.
- Behaviours described in terms of "Elden Ring" or "the reference host" come from Minecraft Ring's Elden Ring host DLL (the only complete host). A new host is free to implement them differently where the table in [section 0.2](#02-what-is-host-defined-and-what-is-fixed) says so. [writing-a-host.md](writing-a-host.md) is the step-by-step guide; this file is the lookup table.
- Statements marked **(UNVERIFIED)** are not backed by the reference sources or by a test run.
- Appendix B at the end lists every region offset on one page.

## 0. Headline findings (read first)

### 0.1 Facts that surprise people

1. **The native terrain-contacts pipeline (`OFF_CONTACTS` 0x320000, `OFF_COLLISION_CONTROL` 0x350000) is dead code in the reference host and guest.** Terrain really works through the **ray mailbox** (`OFF_RAYS`). A guest must not depend on contacts (section 5).
2. **Damage conversion in the reference host is relative to target max HP**: `erHp = ceil(amount * maxHp / clamp(20*sqrt(maxHp/100), 10, 300))`, minimum 1. A guest sends a "fraction of max HP" scaled by the same divisor to get exact control (section 6.2). The host-to-guest direction is raw HP lost plus the stand-in's max HP, so the guest also works with fractions.
3. **The compositor compares guest depth to host depth in metres** after linearising guest depth with `mcNear`/`mcFar` from the slot header, assuming **OpenGL window depth in [0,1]** (not Unity reversed-Z). The guest must publish OpenGL-style depth and scale `mcNear/mcFar` into host metres (section 9.5).
4. **Memory path pixel order is BGRA bytes (B,G,R,A)**, bottom-up rows, premultiplied alpha. The GPU path (not used by this guest) is RGBA with `swapRB=1`, decided by slot flag bit3.
5. **The reference compositor does NOT apply the 1000 ms control timeout**: a guest that dies leaving `COMPOSITE` set keeps its last frame on screen forever. The guest must clear the flags on exit (`scripts/Stop-Guest.ps1` does it for you after a crash). A better host should time `COMPOSITE` out like it does for the camera.
6. **`totalStatusDamage` (hunter events 0x2C) is never written**; `view[]`/`proj[]`/`STATE_MATRICES_VALID` are never filled; `depthIndex`, `CAPTURE_DEPTH`, `hostPresentPage` are unused. Do not rely on them (section 10.7).
7. ULTRAKILL (Unity) and the reference host are both left-handed, Y-up, +Z forward. **No Z mirror is needed.** The host's yaw convention is Minecraft's; the conversion is in section 3.4.
8. A host must not touch the world clock/weather unless the guest writes `OFF_ENVIRONMENT` with flags set. This guest never writes it (section 8).

### 0.2 What is host-defined and what is fixed

| Topic | Fixed by the protocol | Left to the host |
| --- | --- | --- |
| File names, sizes, offsets, seqlock rules, magic/version | yes | - |
| Units (metres) and handedness (left-handed, Y-up) | yes (`unitsPerMeter` is published as 1.0) | - |
| Stable frame per `stageId` | the concept | how it is built (the reference host stitches Elden Ring's open-world tiles) |
| Camera override | `camPos/camTarget/camUp/fovYDeg`; aspect stays the host's | how the host applies it |
| Stand-in ("hunter") pinned to `hunterPos` | the flags | whether it is invisible, invulnerable, immobile (the reference host does all three) |
| Ray answers | hit position, hit flag | the filter (static geometry only), normal synthesis, budget (~2 ms/frame in the reference host) |
| Entity table | layout; `flags` bit0 = dead | which entities, radius (80 m), the box shape (world-aligned capsule boxes) |
| Damage ring | layout; `amount` is a float | the conversion to host HP (section 6.2) |
| Hunter events | layout | when a hit is detected (per host tick) |
| Life counters | meaning of `hostLife`, `mcDeaths`, `hostDeaths` | how death/respawn is detected |
| Actions / prompt | `mcActionReq`/`hostActionAck`/`hostActionResult`, `hostPrompt` | what an action does |
| Compositing | slot layout, layers, depth encoding | the shader (relighting, fog) |

## 1. Transport

### 1.1 Files and directories

| Item | Value |
| --- | --- |
| Bridge directory | `UKBRIDGE_DIR` env var if set, else `ERMC_DIR` (the original name, the only one the Elden Ring DLL reads), else `%TEMP%\ermc`. The launcher scripts set both to the same folder. Both sides must resolve the same folder. |
| Control file | `<dir>\bridge.shm`, **8 MiB = 0x800000** (`ERMC_SHM_SIZE`) |
| Frame file | `<dir>\frames.shm`, **0x17BB1300 = 398,136,064 bytes** (0x1000 header area + 3 slots of 0x7E90100) |

Other files in the folder (host log, `terrain-cache/`, ...) are not part of the protocol.

### 1.2 Who creates and maps each file

**bridge.shm** (either side may start first):

- Open or create the file, extend it to 8 MiB, map it read/write. The guest uses kernel32 `CreateFileMapping`/`MapViewOfFile` directly (Mono's `MemoryMappedFile` is avoided so that both processes share the same physical pages).
- Initialisation: if `magic != 0x434D484D` or `version != 1`, zero `[0, 0x100000)` (everything below `OFF_RAYS`), write `version=1`, `size=8 MiB`, then (after a barrier) `magic`. Whoever sees a valid magic skips this step. **Zeroing only covers [0, 0x100000)**: the rays, entities, damage ring, passages, platforms, contacts and collision-control regions are not cleared, so stale data from a previous session can survive (for example a stale damage ring `write`/`read`; see 5.1 and 6.2).
- The host sets `hostPid` and `hostStartMs`; the guest sets `mcPid` and `mcStartMs` at every attach.

**frames.shm** (the guest creates it; the host only opens an existing file):

- Guest: create the directory, create or extend the file to the **full 0x17BB1300 bytes**, map it, then **invalidate**: release-store `magic = 0`, set `frameCounter = max(frameCounter, latestFrameId@0x10)`, zero the 0x100-byte header of each of the 3 slots, write `version = 3`, finally release-store `magic = 0x524D484D`.
- Host: opens lazily (the reference host tries from its Present hook, at most every 2 s) with `OPEN_EXISTING`, requires the size to be at least the full size, maps the whole file and requires `magic == 0x524D484D && version == 3`. It maps once and never re-validates the magic.
- Therefore the guest must create the file at full size **before** the host first tries and before it sets `COMPOSITE`. A smaller file is rejected.

### 1.3 Single guest

There is no ownership field: the protocol assumes exactly one guest process. Two guests would race on the control block and the ray mailbox. `scripts/Launch-Guest.ps1` refuses to start a second one by comparing `mcPid`/`mcStartMs` with the running process.

---

## 2. bridge.shm layout, field ownership, heartbeats, liveness

### 2.1 Region map (all absolute offsets in bridge.shm)

| Offset | Region | Writer | Notes |
| --- | --- | --- | --- |
| 0x000000 | `ErmcHeader` (0xC0 bytes) | both (per field) | |
| 0x000100 | `ErmcGameState` (0x114) | host | seqlock, section 2.3 |
| 0x000800 | `ErmcControl` (0x64) | guest | seqlock, section 4 |
| 0x000A00 | `ErmcHunterEvents` (0x30) | host | seqlock, section 6.4 |
| 0x000B00 | `ErmcEnvironment` (0x18) | guest | seqlock, section 8 |
| 0x001000 | `ErmcCmdBlock` (0x1000) | dev tools | debug read/write/scan mailbox; not needed |
| 0x002000 | cmd response (up to 0xFE000) | host | |
| 0x100000 | Ray mailbox | guest requests / host answers | section 5.1 |
| 0x200000 | `ErmcEntityTable` | host | 0x10 + 256*0x80; section 6.1 |
| 0x280000 | `ErmcDamageQueue` | guest | 0x10 + 256*0x20; section 6.2 |
| 0x300000 | `ErmcPassageTable` | host | 0x10 + 64*0x20 |
| 0x310000 | `ErmcPlatformTable` | host | 0x10 + 169*0x20 |
| 0x320000 | `ErmcTerrainContacts` | (host, unimplemented) | 0x28 + 4096*0x20; section 5.2 |
| 0x350000 | `ErmcCollisionControl` (0x30) | (guest, unimplemented) | section 5.2 |
| 0x360000 | `ErmcHostEvents` (0x60) | host (optional) | seqlock, section 8.1; extension, `protocol/c/bridge_protocol_ext.h` |

Struct packing: everything after `#pragma pack(push, 4)` packs to 4; the earlier structs (contacts, collision control, platform) are plain 4-byte-field structs, so their offsets are the same.

### 2.2 `ErmcHeader` fields (0xC0 bytes)

| Off | Field | Type | Writer | Semantics |
| --- | --- | --- | --- | --- |
| 0x00 | magic | u32 | whoever initialises first | `0x434D484D` ("MHMC", kept from the MH:World bridge) |
| 0x04 | version | u32 | init | 1 |
| 0x08 | size | u32 | init | 8 MiB |
| 0x0C | reserved0 | u32 | - | unused |
| 0x10 | hostHeartbeat | u64 | host | = `on_frame` counter; +1 per Elden Ring game frame (from the tick task, group 117) or per ~16 ms from the pacer thread until the tick task claims the source |
| 0x18 | mcHeartbeat | u64 | guest | +1 per guest render frame. **Host reads it only for the environment 2000 ms rule** |
| 0x20 | hostPid | u32 | host | |
| 0x24 | mcPid | u32 | guest | |
| 0x28 | hostStartMs | u64 | host | unix ms |
| 0x30 | mcStartMs | u64 | guest | unix ms |
| 0x38 | coreReloadReq | u32 | dev tool | hot-reload request |
| 0x3C | coreReloadAck | u32 | host loader | |
| 0x40 | coreGeneration | u32 | host loader | |
| 0x44 | coreStatus | i32 | host loader | 1 running, 0 not loaded, <0 error |
| 0x48 | mcSwitchReq | u32 | **host** | +1 when F8 is pressed in Elden Ring (ER->guest "take control back") |
| 0x4C | hostFocusReq | u32 | **guest** | +1 = "bring the ER window to the front" |
| 0x50 | hostTaskPage | u64 | host | RWX page address, survives reloads |
| 0x58 | hostLife | u32 | host | +1 each time the stand-in becomes usable at a place the guest did not choose (after load, respawn, game warp); guest must recall before driving |
| 0x5C | mcDeaths | u32 | guest | +1 each time the guest's player dies |
| 0x60 | hostDeaths | u32 | host | +1 each time the standing-in stand-in died in ER |
| 0x64 | debugFlags | u32 | dev tool | bit0 compositor test pattern, bit1 host depth is standard-Z not reversed, bit2 spawn test soldier, bit3 remove it, bit4 press Esc. Leave 0. |
| 0x68 | hostPresentPage | u64 | - | declared, never referenced on this build |
| 0x70 | mcActionReq | u32 | guest | +1 = perform the action ER offers now |
| 0x74 | hostActionAck | u32 | host | = mcActionReq once handled |
| 0x78 | hostActionResult | i32 | host | 1 done, 0 nothing/blocked/grayed, -1 unsupported build, -2 needs ER input (ladders) |
| 0x7C | hostPromptSeq | u32 | host | +1 before and +1 after each prompt rewrite (odd = writing) |
| 0x80 | hostPrompt[64] | char | host | UTF-8 NUL-terminated prompt text |

Seqlock convention: writer bumps `seq` to odd, writes payload, bumps to next even; readers retry if `seq` odd or changed. **A writer must never leave `seq == 0` after publishing**: the host treats `seq==0` as "never published" (`control_snapshot` returns `s1 != 0`; environment skips `!seq`; guest `readState` returns `s1 != 0`). The host uses plain stores + compiler barriers (TSO assumption); C# must use `Volatile.Read/Write` or `Interlocked` + `Thread.MemoryBarrier` (x86 also, but the JIT may reorder).

### 2.3 `ErmcGameState` (host -> guest), offset 0x100, size 0x114

Published once per host game frame by `on_frame` using a local copy then `memcpy` of bytes [4, 0x114) between two `seq` bumps.

| Off | Field | Type | Meaning |
| --- | --- | --- | --- |
| 0x00 | seq | u32 | seqlock |
| 0x04 | flags | u32 | see below |
| 0x08 | frame | u64 | host frame counter (same as heartbeat) |
| 0x10 | camPos[3] | f32 | ER camera eye, **stable frame** |
| 0x1C | camTarget[3] | f32 | `pos + camera forward` (1 unit ahead), stable frame |
| 0x28 | camUp[3] | f32 | camera up row |
| 0x34 | fovYDeg | f32 | vertical FOV in degrees |
| 0x38 | nearZ | f32 | camera near |
| 0x3C | farZ | f32 | camera far |
| 0x40 | aspect | f32 | camera aspect |
| 0x44 | unitsPerMeter | f32 | **always 1.0** (Elden Ring: metres) |
| 0x48 | playerPos[3] | f32 | stand-in **feet** (physics position), stable frame; while standing in, the reach-assist offset is subtracted so it reports "where the guest player is" |
| 0x54 | playerQuat[4] | f32 | (x,y,z,w) physics rotation (rotation about +Y; local forward = -Z) |
| 0x64 | winX,winY,winW,winH | i32 | ER client area in screen coordinates |
| 0x74 | bbW,bbH | u32 | "back buffer size" = client area size |
| 0x7C | stageId | u32 | the **zone id** (not a "stage"); 0 when unknown / not alive; header comment about 504/101 is stale (MH:World leftover) |
| 0x80 | view[16] | f32 | never filled (zeros) |
| 0xC0 | proj[16] | f32 | never filled (zeros) |
| 0x100 | supportEpoch | u32 | changes when support contact is lost/reacquired |
| 0x104 | supportTravelY | f32 | cumulative vertical travel at the tracked support point (m) |
| 0x108 | supportPos[3] | f32 | tracked floor position, stable frame |

State flags:

| Bit | Name | Set when |
| --- | --- | --- |
| 0 | CAMERA_VALID | camera object found (and life ALIVE) |
| 1 | PLAYER_VALID | main player found **and life == ALIVE and frame valid** |
| 2 | WINDOW_VALID | window client rect fetched |
| 3 | CAM_OVERRIDDEN | host applied a guest camera since the previous publish |
| 4 | MATRICES_VALID | **never set** |
| 5 | WINDOW_FOCUSED | ER window is foreground |
| 6 | COMPOSITING | a composite was drawn within the last 500 ms |
| 7 | PLAYER_DEAD | life == DEAD |
| 8 | HOST_BUSY | life == DEAD, or not ALIVE within 60 s of the last ALIVE/DEAD tick (loading screen, settling) |
| 9 | SUPPORT_VALID | tracked moving/flat support (needs MOVE_HUNTER) |

When life != ALIVE the early return at leaves cam*, player*, stageId, support* at **zero**; only window fields, `unitsPerMeter=1.0` and flags remain.

### 2.4 Liveness / timeout master table

Host-side rules about the guest:

| Rule | Value | Effect |
| --- | --- | --- |
| Control `seq` unchanged | **1000 ms** | `control_active` false: no camera override, stand-in released, platform tracking off. Last good snapshot is reused on torn reads (never drops for a torn read) |
| Control never published (`seq==0`) | - | no control at all |
| Compositor reads control | **no timeout** | stale `COMPOSITE` flag keeps the last frame drawn |
| `mcHeartbeat` unchanged | **2000 ms** | environment override released (clock/weather returned to ER) |
| `mcDeaths` change honoured only if | stand-in stood in within the last **2000 ms**, life ALIVE, not already dead | kill stand-in (`Kill(chr,0)`) |
| Ray mailbox processing budget | ~**2 ms** per game frame, clock checked every 16 rays | batch finished across frames via `processed` |
| Platform cell scan | every **40 ms** while standing in | |
| Support tracking | previous sample must be **<= 250 ms** old; ray window `0.1 + 24*min(dt,250ms)/1000` m; travel jump limit `0.05 + 24*dt` m | else support lost, `supportEpoch++` |
| Moving-cell memory | 1500 ms (`old.moved`), 250 ms (re-trace) | |
| Reach scan (doors) | worker, 1000 ms period, <= 50 ms per scan, 800-ish nodes; passages republished every 30 ticks | |
| World strike rate limit | 150 ms | |
| Poke per-target rate | 250 ms | |
| Pending kill retry | 2000 ms | |
| Poke row repatch retry | 5000 ms | |
| Action press latch | cleared after 500 ms if not consumed | |
| Life settling | after load/respawn: >= 90 ticks and 45 still ticks; game warp (>10 m jump): 30/15; zone change or hot reload: 10/0; "still" = moved < 0.15 m, same zone, same Havok offset | |
| NoDead clamp | HP==1 for 3 consecutive ticks -> let it die | |
| HOST_BUSY tail | 60 s after last ALIVE/DEAD | |
| `frames.shm` map retry | 2000 ms | |
| Fence wait before reuse of a ring slot | 50 ms (then skips the composite) | |
| Depth buffer candidate | must have been used within 2000 ms | |
| Compositor hook discovery | worker retries every 1 s, up to 180 tries | |
| Loader wait for window | 10 min; +1.5 s settle | |
| Core shutdown waits | 2 s for worker threads, up to ~2 s for in-flight hooks | |

Guest-side rules (reference implementation; keep equivalents):

| Rule | Value |
| --- | --- |
| Host alive iff `hostHeartbeat` changed within | **2000 ms**; first sample after attach never counts as live (stale counter) |
| Reopen bridge.shm attempt | 1000 ms |
| Recall settled | recall served **and >= 400 ms ago** (`recallSettled(400)`) before driving camera/hunter, before overlay shows, before platform logic |
| Ray batch stall log | 3000 ms (only logs; mailbox ownership is **kept** until the host answers) |
| Overlay "in world" grace | 3000 ms after last PLAYER_VALID; deactivate only after 1500 ms of not wanting |
| Action request pending | 2000 ms |
| Terrain column refresh | 2000 ms (`REFRESH_MS`) |
| Status damage drain | every 20 server ticks |
| Native contacts validity | 2000 ms (dead path) |
| Frame capture gating | at most one capture per host frame value |

---

## 3. Coordinate frames

### 3.1 Handedness and units

- Host world = Elden Ring physics space: **metres, left-handed (D3D), +Y up, +Z forward** (the host builds the camera matrix with `right = up x forward`).
- `unitsPerMeter = 1.0`. All lengths in the protocol are metres.
- Camera matrix stored by the game: rows right, up, forward, position.
- ULTRAKILL/Unity: also left-handed, +Y up, +Z forward. A same-handedness guest only needs a translation (and unit scale) between guest world and host stable frame. (Minecraft's right-handed world needed `flipZ`.) **(UNVERIFIED for ULTRAKILL: world unit size. Do not assume 1 unit = 1 m; measure the player height/walk speed and pick a scale. All scale-dependent host constants are listed in 10.4.)**

### 3.2 The "stable frame" and `stageId`/zone

Elden Ring's Havok space re-centres as the world shifts, so every position that crosses the protocol is in a **stable frame per zone**:

- Open world (area 60 or 61, `blockId >> 24`): global = `blockRelative + 256 * (gridX, 0, gridZ)` with `gridX = (blk>>16)&0xFF`, `gridZ = (blk>>8)&0xFF`; zone = `area << 24` (0x3C000000 / 0x3D000000).
- Everything else (legacy dungeons, caves): block-relative coordinates; zone = full block id (u32).
- `blockId` is read at player+0x6D0, position at +0x6C0. Block ids `0` and `0xFFFFFFFF` mean loading screen / no block: `block_valid` false, frame invalid, life NONE.
- `Havok = stable + offset`. `offset = HavokPos - blockPos(-bias)` is re-derived only by whole multiples of 8 m (Havok re-centre) once a zone has started. A new zone (loading screen, new character) starts fresh; **a seamless block change keeps the old zone id** and shifts coordinates by `g_bias` so the guest does not see a zone jump.
- The guest must treat `stageId` (state 0x7C) as an opaque key: when it changes (and is not 0/-1) the guest must re-anchor (Minecraft: new region = 32768 blocks away per zone). All guest->host positions are interpreted in the **current** zone's stable frame (`to_havok` adds the current offset); the guest has no way to name a zone in control, so on a zone change it must stop sending (flags = 0) until `stageId` matches its anchor (Minecraft: `CameraSync.ready` requires `map.zone == STATE.stageId`). Ray results, entity table, passages and platforms are likewise only valid for the zone in force (`ErmcPassageTable.zone`, `ErmcPlatformTable.zone`).

Guest-chosen anchor (Minecraft): first valid state per zone pins `playerPos` (host stable) to a fixed guest point; `toMc = (x-ax)*s + originX, (y-ay)*s + 100, +/-(z-az)*s + 0.5`. For Unity the equivalent is a pure offset; anchors persisted per world - optional.

### 3.3 Camera fields (control block)

`camPos` = eye position; `camTarget` = look-at point (host only uses `normalize(camTarget - camPos)` as forward, so any distance works); `camUp` = up vector (roll honoured) all in stable frame metres; `fovYDeg` = **vertical** FOV, applied only if `5 < fov < 170` else the game's FOV is left. Host computes `fwd = norm(target-pos)`, `right = norm(cross(up, fwd))`, `up = cross(fwd, right)` and writes the 4x4 (rows right/up/fwd/pos) into the game's camera at `+0x10`, FOV (rad) at `+0x50`. **Aspect, near, far stay the game's**: the guest frame must be rendered at ER's window aspect (`winW/winH` from state) or it will be stretched (the compositor stretches the guest image over the full back buffer).

The Minecraft guest sends `target = eye + lookVector` and `up = camera.getUpVector`, then maps all three through `toHost` / `dirToHost`.

### 3.4 `hunterPos` and `hunterYawDeg`

- `hunterPos` = the guest player's **feet** (bottom of the body) in stable frame. Evidence: the host writes it directly into the stand-in's physics origin, entity `pos` from the same origin is documented "origin (feet)" and boxes are built upward from it, platform/support logic compares `hunterPos.y` with floor ray hits (: feet-floor must lie in [-0.45, +0.4] m when GROUNDED, up to +4 m otherwise). The Minecraft guest sends the interpolated feet.
- The host may move the stand-in off `hunterPos` ("reach assist"): when the guest faces an animated map object (door, lever, chest, lift) within 2 m the stand-in is placed 0.45 m short of the surface, squared to it. `state.playerPos` is reported with that offset removed.
- `hunterYawDeg`: **Minecraft yaw in degrees**. Exact host formula:

  ```
  y        = hunterYawDeg * pi/180
  forward  = (-sin y, -cos y)            // (x, z) in the host stable frame; MC yaw 0 faces host -Z
  theta    = atan2(-forward.x, -forward.z) = y     // identity: theta == the yaw in radians
  q        = (0, sin(theta/2), 0, cos(theta/2))     // written to the physics quat and the interpolated quat
  ``` verifies that rotating the character's local forward `(0,0,-1)` by that quaternion yields `(-sin y, -cos y)`.
  For a **Unity-handed guest whose frame equals the host frame up to translation**: if the guest's horizontal facing is `f=(fx,fz)` (unit, host axes), send `hunterYawDeg = deg(atan2(-fx, -fz))`. Equivalently, with Unity `eulerAngles.y = psi` (forward `(sin psi, cos psi)`), send `hunterYawDeg = psi + 180`. If the guest frame is rotated relative to the host frame by `r` about +Y (anchor rotation) add that rotation to the facing before converting.
  Guest-side counterpart for recall: Minecraft turns `playerQuat` into a yaw with `a = 2*atan2(qy, qw)`, `forward = (-sin a, -cos a)`.
- The pose is applied to physics (`kPhysPos` 0x70, `kPhysPosLast` 0x80 as `vec4 w=1`, `kPhysProxyUpdate` 0x91=1).

### 3.5 `poseLag`, `mcFrame`, `poseId` bookkeeping

- Guest assigns a monotonically increasing frame id per captured frame (Minecraft seeds it with `currentTimeMillis*1024` and `max(...,latestFrameId@frames.shm+0x10)` so ids survive guest restarts).
- It writes pixels into a slot with `frameId = poseId = id`, and **only afterwards** publishes the control block with `mcFrame = id`.
- Each game frame the host camera task calls `compositor_note_applied_pose(c.mcFrame)` (only when a camera override was actually applied) pushing the id into an 8-entry ring.
- At Present: `poseId = history[(pos-1-lag) & 7]` where `lag = ctrl.poseLag`, clamped to `<= pos-1` and `<= 6`; `0` lag = newest applied pose; no history -> 0. The guest default is `poseLag = 1`. Meaning (header): "game frames between applying a pose and presenting it".
- `pick_slot(poseId)`: scans the 3 slots; skips odd `seq`, zero/oversized width/height; returns the slot with `poseId == wanted` immediately; otherwise the slot with the greatest `poseId` **less than** wanted; null if none. Stats logged as "exact / older / missing" every 300 presents.
- If no slot qualifies and the compositor already holds a frame, it re-draws the previous textures. A slot is only uploaded when `slot->frameId > g_lastUploaded` ("fresh").

---

## 4. Control flags and the driving sequence

### 4.1 `ErmcControl` (guest -> host), offset 0x800, size 0x64

| Off | Field | Type | Host use |
| --- | --- | --- | --- |
| 0x00 | seq | u32 | liveness: must change at least every 1000 ms |
| 0x04 | flags | u32 | below |
| 0x08 | mcFrame | u64 | pose id for compositing (== slot `poseId`) |
| 0x10 | camPos[3] | f32 | |
| 0x1C | camTarget[3] | f32 | |
| 0x28 | camUp[3] | f32 | |
| 0x34 | fovYDeg | f32 | |
| 0x38 | hunterPos[3] | f32 | stand-in feet |
| 0x44 | poseLag | u32 | compositor |
| 0x48 | depthIndex | u32 | **unused** |
| 0x4C | lightGain | f32 | relight: `k = clamp(lightMin + lum*lightGain, 0, 1.15)`; 0 = default 2.6 |
| 0x50 | lightMin | f32 | 0 = default 0.18 |
| 0x54 | fogStrength | f32 | 0 = default 0.55 |
| 0x58 | hunterYawDeg | f32 | when MOVE_HUNTER |
| 0x5C | supportEpoch | u32 | echo of `state.supportEpoch` used by this pose |
| 0x60 | supportTravelY | f32 | platform travel already included in `hunterPos` (m) |

The Minecraft guest does not write 0x4C-0x57 (zeros -> defaults).

Flags:

| Bit | Name | Host behaviour |
| --- | --- | --- |
| 0 | OVERRIDE_CAMERA | camera task (task group 107, after the game resolved its camera) writes the guest camera if life==ALIVE && control fresh && frame valid |
| 1 | MOVE_HUNTER | stand-in active: stand-in pinned to `hunterPos`, NoMove, gravity off, NoDead, HP refilled every tick; also enables support/platform tracking |
| 2 | HIDE_HUNTER | clears render flag bit3 of the character; restored on release |
| 3 | CAPTURE_DEPTH | reserved |
| 4 | COMPOSITE | host draws guest frames from frames.shm into the ER frame (requires `frames_open`) |
| 5 | NO_DEPTH_TEST | debug: no occlusion |
| 6 | DEBUG_DEPTH | debug: show host depth bands (10 m) |
| 7 | NO_RELIGHT | debug: no ER relighting of guest pixels |
| 8 | GROUNDED | guest stands on terrain; enables platform carrying of `hunterPos.y` when `supportEpoch` matches; also tightens the support feet-gap to 0.4 m instead of 4.0 m |
| 9 | FLYING | creative/spectator flight: do not track a platform |

Minecraft sets: `OVERRIDE_CAMERA | (standIn&&player ? MOVE_HUNTER|HIDE_HUNTER) | (passthrough ? COMPOSITE) | debug bits`, `GROUNDED` if `onGround && !flying`, `FLYING` if `flying`.

### 4.2 Start-driving sequence (as implemented by the guest)

This is the order the ULTRAKILL guest follows, taken from Minecraft Ring's Minecraft guest. A new host should expect exactly this behaviour.

1. Map `bridge.shm`; every frame poll `hostHeartbeat` (alive = changed within 2 s; the first sample never counts), take a seqlock snapshot of `ErmcGameState`, and bump `mcHeartbeat`. Also poll `mcSwitchReq`.
2. Compare `hostLife` with the last seen value. **Any change, including the first read after attach, requests a recall.**
3. Anchor the zone: ignore `stageId` 0 and 0xFFFFFFFF; when `PLAYER_VALID` is set and there is no mapping for this `stageId`, create one with `anchor = state.playerPos` (the guest world's origin sits there) and `unitsPerMeter`.
4. Serve the recall when it is pending and `PLAYER_VALID && mapping != null`: teleport the guest player to `playerPos` (feet) facing the yaw derived from `playerQuat`, remember the time, and hold the player in place on a small temporary floor until the first ray batches have produced ground under it (the guest gives up waiting after 4 s). Recalls are requested at attach, on a `hostLife` change, after returning from F8 and after re-anchoring.
5. Drive only when all of these hold, else release control: host alive, `PLAYER_VALID` and not `HOST_BUSY`/`PLAYER_DEAD`, the mapping's zone equals `state.stageId`, no recall pending, **at least 400 ms since the recall**, not in host mode (F8), and the guest player is ready.
6. Driving: fill the control block (camera, `hunterPos`, `hunterYawDeg`, flags) and write it. With compositing, the pose is saved with the capture and written only after the pixels are in the slot (9.5 step 3).
7. Release: any "not ready" condition writes **one** control block with `flags = 0` and a new `mcFrame`, which also stops compositing on the host (the `COMPOSITE` flag drops). Do this on exit and on any unexpected exception too.
8. While the host reports `HOST_BUSY` (death, loading, settling) the guest draws nothing and does not drive.

### 4.3 F8 handling

- **Guest F8 (guest -> host)**: the guest enters "host mode": writes `flags = 0` once, releases the mouse, hides or shrinks its input window, and does `hostFocusReq++`. On a `hostFocusReq` change the host should restore and foreground its window, and must not count the same key press as a return press.
- **Host F8 (host -> guest)**: the host polls F8 every tick (even unfocused); when its window is foreground and F8 goes down (edge) it does `mcSwitchReq++`. The guest sees the change (the first sample is a baseline), leaves host mode, shows its window and requests a recall. It drives again after the recall settles (400 ms).
- While the control flags are 0 the host must release the stand-in (restore movement, gravity, damage, visibility), so the host's own input works.

---

## 5. Terrain

### 5.1 Ray mailbox (`OFF_RAYS` 0x100000) - the working terrain mechanism

Layout: `ErmcRayHeader` (0x20 bytes) then `ErmcRay rays[8192]` (24 B: `f32 start[3]; f32 end[3]`) at +0x20, then `ErmcRayHit hits[8192]` (32 B) at `+0x20 + 8192*24 = +0x30020`.

| Off | Field | Writer | |
| --- | --- | --- | --- |
| 0x00 | reqSeq | guest | +1 to submit |
| 0x04 | respSeq | host | set to reqSeq when the whole batch is done |
| 0x08 | count | guest | <= 8192 (clamped host-side) |
| 0x0C | flags | guest | `ERMC_RAYS_CAMERA_FILTER` bit0 (**not implemented in the host mailbox path**), `CUSTOM_FILTER` bit1, `HIT_SELF` bit2 (debug command path only) |
| 0x10 | processed | host | progress (resumable across game frames) |
| 0x14 | filterA | guest | used as the collision filter when CUSTOM_FILTER |
| 0x18,0x1C | filterB/C | guest | **ignored by the host** |

Hit (`ErmcRayHit`, 32 B): `pos[3]` (stable frame) at +0, `normal[3]` at +12, `hit u32` at +24, `attr u32` at +28.

Protocol and host behaviour:

- Guest writes rays, `count`, `flags` (+filterA) **then** bumps `reqSeq` with release semantics. One batch in flight: guest must not touch the region until `respSeq == reqSeq`; the reference guest refuses (`-1`) if `reqSeq != respSeq`. **Important**: because the zeroing at init stops at 0x100000, a stale `reqSeq != respSeq` from an aborted previous session makes the host process that old batch first (and the reference (Minecraft) guest never submits until it completes; the host rereads `count`/`flags`/rays at processing time). A new guest should read both counters after attach and, if they differ, wait for `respSeq` to catch up (or not submit anything until it does).
- Host services the mailbox **only while life == ALIVE** (inside `if (alive)`), once per game tick (task group 117). Per tick it processes rays from `processed` until ~2 ms elapsed (clock sampled every 16 rays), writes `processed`, and when finished sets `respSeq = reqSeq` then `processed = 0`. So a batch needs >= 1 game frame round trip and may span several; throughput is time-budgeted, not count-budgeted (**exact rays/frame unmeasured; the Minecraft guest uses 2048-ray batches**).
- Default filter when not CUSTOM: `kTerrainRayFilter = 0x5D` ("map geometry and props, but not characters"; verified live per the comment). The ray ignores the player character (`ignore = p.ins`).
- `raycast`: `start/end` in the stable frame -> Havok (`+offset`); calls `CSPhysWorld::CastRay(world, filter, origin(w=1), delta(w=0), hit, ignore)`. On hit: `hit = 1` (**always 1, not the raw return value as the header comment claims**), `pos = to_stable(hit)`, `normal` **synthesised, not measured**: if `-delta.y/|delta| > 0.7` (a downward ray) `normal = (0,1,0)`; otherwise `normal = -delta/|delta|` (facing the ray origin). `attr` is **always 0** (never filled). On miss everything is zero. No per-ray distance is returned: derive it from `pos`.
- Known engine behaviour (comment in the unused sampler): CastRay "can miss when starting inside solid geometry".

How the Minecraft guest uses it (reference for column terrain): per 1x1 column it submits `COARSE_RAYS = 2 + 3*4*2 = 26` rays: a downward ray from `y+2.2` to `y-40` at the column centre; a "high" downward ray from `y+40` to `y+2.2`; and for heights `{0.35, 1.0, 1.875}` above the sample feet, 4 wall segments (two axis-centre lines and two diagonals) cast in both directions. Consumption: `lowHit = hit != 0`; ground = `hit.pos.y`; the high ray only counts if `normal.y > 0.3` (always true for hits); horizontal hits count as obstacles when `|normal.y| < 0.7` and the wall is taller than the ground by 0.25 m. Because the host synthesises normals, a guest cannot detect slopes from the normal; derive slope from neighbouring hit heights.

### 5.2 `OFF_CONTACTS` / `OFF_COLLISION_CONTROL` (native contacts): documented, **unwired**

Status: both regions exist in the protocol and the sampler class `TerrainContactSampler` is fully written, but **no host code instantiates it or reads `OFF_COLLISION_CONTROL`**, and the guest neither writes collision control nor calls `NativeTerrainCollision.refresh` (see section 0.1). Treat as inert. If a different host build does implement it, the intended contract is:

`ErmcCollisionControl` (0x350000, 48 B): `seq u32 @0`, `flags u32 @4` (0 = off; bit1 = full previous simulation feet valid; the reference guest ORs `2` into any nonzero flags), `zone u32 @8`, `previousFeetX f32 @0xC`, `feet[3] @0x10`, `previousFeetY @0x1C`, `velocity[3] @0x20`, `previousFeetZ @0x2C`. Guest writes "actual simulation feet", independent of frame poses, units metres in the stable frame, velocity m/s.

`ErmcTerrainContacts` (0x320000): header 0x28 bytes (`seq @0, count @4, zone @8, valid @0xC` (bit0 complete, bit1 40-byte header with trajectory; sampler sets 3), `origin[3] @0x10`, `previousFeetY @0x1C`, `previousFeetX @0x20`, `previousFeetZ @0x24`), then `count <= 4096` contacts of 32 B: `min[3], max[3], kind u32, reserved` (the reference guest reads a 32-byte header if `flags&2==0` else 40). Kinds: `1 FLOOR`, `2 WALL`, `3 CEILING`, `4 CLEAR` (ray-verified air only).

Intended sampling: origin = feet; `STEP = 0.125 m`; `EDGE = 13` (1.625 m footprint), `HEIGHTS = 21` rows (2.625 m); guard rays 7x7x3 floor + 7x7x3 ceiling + 13x27x4 walls (1698) + 2 x 169 footprint floors = 2036 rays, `2*2036 <= 4096` contacts. Floor rays from `feet.y+0.625` (or `previousY+0.125` when descending <= 16 m) to `feet.y-24`; ceiling rays `feet.y+0.65 -> +2.75`; wall reach `clamp(12 + 4*travel, 12, 64)` m. Contact geometry: FLOOR box `xz = ray +/- 0.0625`, `y in [hit.y-0.125, hit.y]`; CEILING `y in [hit.y, hit.y+0.125]`; WALL cross-section 0.125 x 0.125 around the ray, extending along the axis from the hit to 0.0625 behind it on the blocked side only; CLEAR = the box between the ray start and its hit padded 0.0625 on the cross axes. All in the stable frame of `zone`.

**Guest implication**: do not wait for contacts. Build colliders from rays (5.1) or from your own sweeps of the mailbox.

### 5.3 Passages and platforms / support

- **Passages** (`OFF_PASSAGES`, host-written, 64 max; entry 0x20 B: `pos[3]` (corridor centre at floor level), `yaw` (through-direction `(sin yaw, 0, cos yaw)` in ER), `halfWidth` 0.75, `halfDepth` 1.8, `height` 3.0, `id`; header `seq, count, zone`). Found through animated map objects (doors) whose centre line at +1 m is clear while lines 1.4 m to each side hit a wall; rescanned every 30 ticks while standing in; published with `zone`; cleared (count 0) when not standing in. The Minecraft guest uses them to avoid placing wall blocks in 1 m door openings at an angle to its grid. **A guest with continuous collision (Unity colliders) can ignore them entirely.**
- **Platforms table** (`OFF_PLATFORMS`): moving-floor cells on a 0.5 m grid within +/-3.25 m of `hunterPos`, 13x13 = up to 169 cells, rewritten every 40 ms while standing in: `x,z,floor,previousFloor,clearLow,clearHigh,flags` (bit0 floor present, bit1 old floor verified empty, bit2 static landing). Only cells observed moving at the same world point qualify. **Ignorable** unless the guest wants to ride lifts.
- **Support tracking** (`state.supportEpoch/supportTravelY/supportPos`, flag SUPPORT_VALID): `update_support` casts 5 rays (centre + 4 corners at +/-0.3 m, +/-0.5 m vertical) each tick while standing in and !FLYING. If `GROUNDED` and `control.supportEpoch == state.supportEpoch`, the host moves the pinned stand-in by `state.supportTravelY - control.supportTravelY` in Y. **Minimal guest**: send `supportEpoch = 0` (the host's epoch is never 0) and `supportTravelY = 0` -> no adjustment ever happens; ignore the state fields. Optional: implement platform riding by applying `supportTravelY` deltas to the guest player and echoing epoch/travel (Minecraft: `MovingPlatformClient.copyControl`).
- Cost note: while MOVE_HUNTER is active the host casts ~5 support rays + up to 169 cell rays (more for moving cells) every 40 ms on the game thread in addition to the mailbox. Unavoidable unless MOVE_HUNTER is off.

---

## 6. Entities, combat, life

### 6.1 Entity table (`OFF_ENTITIES` 0x200000)

Header: `seq u32 @0`, `count u32 @4`, `frame u64 @8`, entries at +0x10, 0x80 B each, max 256. Published every game tick while life == ALIVE, emptied (`count=0`) otherwise. Seqlock: odd while writing.

| Off | Field | Production (source) |
| --- | --- | --- |
| 0x00 | id u64 | `FieldInsHandle` of the ChrIns (chr+0x08): stable while it lives; also what damage targets use |
| 0x08 | kind u32 | hostile (`CanTarget(player, chr)` real game call, or team type 6/7 fallback): `LARGE_MONSTER=1` if team==7 or capsule height > 3.0 m, else `SMALL_MONSTER=2`; non-hostile (NPCs etc.) `OTHER=3` |
| 0x0C | emId u32 | NpcParamId (chr+0x60) |
| 0x10 | pos[3] | `to_stable(physics pos)` = **feet** |
| 0x1C | quat[4] | physics rotation (x,y,z,w), copied (not used because flag bit1 is always set) |
| 0x2C | boxCenter[3] | `(pos.x, pos.y + h/2, pos.z)` |
| 0x38 | boxHalf[3] | `(r, h/2, r)` with the capsule radius `r` and height `h` read from the physics module (+0x2E4, +0x2E0); fallback `h=1.8, r=0.4` when out of (0.05, 40)/(0.05, 15) |
| 0x44 | hp f32 | `max(hp, 0)` (ints in the game) |
| 0x48 | maxHp f32 | |
| 0x4C | flags u32 | **always `2` (bit1: box world-aligned, ignore quat) | `1` if dead/dying (`hp<=0` or render flag bit7)** |
| 0x50 | name[48] | `"c%04d"` model id (e.g. `c4300`) |

Selection: candidates = game's "characters by distance" list (up to 1024) + the debug character set; skipped: the player, squared distance (chr+0x3FC) > 80 m, models 1000/100 (invisible helpers), `maxHp <= 0`, duplicate handles; cap 256. Dead characters stay in the list with `flags&1`. Boxes are **world-aligned vertical capsule boxes** (not oriented); the ordered-box path (`flags` bit1 == 0, `quat`) exists in the guest but the host never emits it.

Guest handling (reference): one invisible proxy per id with the AABB: `center = toMc(boxCenter)`, halves scaled by `1/unitsPerMeter`, entity feet = `center.y - hy`. Proxies of dead entities are removed. Enemies = kind 1 or 2.

### 6.2 Guest -> host damage ring (`OFF_DAMAGE` 0x280000)

`ErmcDamageQueue`: `write u32 @0` (producer), `read u32 @4` (host consumer), `reserved[2]`, `ring[256]` at +0x10 each `ErmcDamage` 0x20 B: `id u64 @0`, `amount f32 @8`, `hitPos[3] @0xC`, `flags u32 @0x18`, `reserved @0x1C`. Producer: write entry `ring[write % 256]`, then release-store `write+1`; refuse when `write - read >= 256`. Consumer (host): `r = read; if (w - r > 256) r = w - 256` (drops oldest), applies each, stores `read = r`. **The init zeroing does not cover this region (>= 0x100000)**, so `write/read` may be stale from an earlier session; they remain consistent with each other, so a new guest must **continue from the existing `write`** value (do not reset to 0).

Host processing (`service_damage`, only while ALIVE, before entities are republished so ids refer to last tick's table):

- `id == 0`: "strike the world": limited to once per 150 ms; `hitPos` is a point in the stable frame; if `flags & WORLD_RAY (1<<3)` the host first casts from the stand-in's eye `feetPos + 1.62 m` toward `hitPos` (filter 0, i.e. unfiltered) and uses the hit point instead; if `0.3 < len < 8` m from eye to target it spawns the player's "poke" bullet (Ruin Fragment param row 10176000, patched to dmgLevel 1, 12 poise) 0.25 m before the target along that direction; breaks crates/pots.
- `id != 0`: must match an entity published in the previous tick (`find_published`, re-validates vtable and handle), `0 < amount < 10000`, and `can_target(player, chr)` must be true (hostile) else dropped. Then `apply_hit`.
- `apply_hit`: skip if dead; `dmg = ceil(er_damage(amount, maxHp))`, min 1; if not NOT_BY_PLAYER, sets the target's `last_hit_by` to the player handle (kill credit/aggro); calls the game's `ModifyHp(data, -dmg,...)` (respects invincibility; if HP unchanged logs "invincible"); if alive after and by player (rate limited 250 ms per target) fires a poke bullet from behind the target for hit reaction and aggro; if HP <= 0 calls `Kill(chr, 0)` (death animation, drops, runes to the player), retried for 2 s if refused.

**Exact conversion**:

```
mcHealth = clamp(20 * sqrt(maxHp / 100), 10, 300)
erHp     = amount * maxHp / mcHealth      // then ceil, min 1 (apply_hit)
```
Examples: maxHp 100 -> mcHealth 20 -> 1 point = 5 HP; maxHp 221 (soldier) -> 29.73 -> 1 point = 7.43 HP; maxHp 6000 -> 154.9 -> 1 point = 38.7 HP; maxHp <= 25 -> mcHealth floor 10; maxHp >= 22500 -> 300.
**Recommended scheme**: the guest knows `maxHp` from the entity table, so send `amount = fractionOfMaxHp * mcHealth(maxHp)` (compute `mcHealth` with the same clamp) and the host yields `ceil(fraction * maxHp)` HP. Do **not** rely on Minecraft's hurt cooldown: the Minecraft proxy applies a 20-tick/half-cooldown rule before sending (`ErEntity.java:hurt`, the host does no throttling except the 250 ms poke).

Flags: `CRITICAL (1<<0)` only appears in the host log - no damage multiplier; `OUTWARD (1<<1)` **not read by the host source** (comment mentions a slinger shot, MH:World legacy); `NOT_BY_PLAYER (1<<2)` skips kill credit/aggro/poke (use for guest-world environmental or friendly-fire-ish damage); `WORLD_RAY (1<<3)` only with `id == 0`.

Hit-test is the guest's job: use `boxCenter/boxHalf` as a world-aligned AABB in host coordinates (convert to the guest frame). The Minecraft proxy fixes the AABB bottom-centre at the feet and applies `+/- boxHalf` (`ErEntity.java:makeBoundingBox`).

### 6.3 Host -> guest damage (`OFF_HUNTER` 0xA00, `ErmcHunterEvents`, 0x30 B)

| Off | Field | Production |
| --- | --- | --- |
| 0x00 | seq | odd while writing |
| 0x04 | hitCount u32 | +1 per detected HP-loss event |
| 0x08 | totalDamage f32 | running sum of ER HP lost |
| 0x0C | lastDamage f32 | HP lost in the last event |
| 0x10 | lastHitFrom[3] | position (stable frame, **feet**) of the attacker: the entity the game recorded in the stand-in's `last_hit_by` (chr+0x180, reset to -1 after each hit), else the nearest hostile of the last publish, else `hunterPos` |
| 0x1C | hunterMaxHp f32 | stand-in max HP; **only written when a hit is reported** (0 until the first hit) |
| 0x20 | lastHitFrame u64 | state frame at the hit |
| 0x28 | lastHitKind u32 | `ERMC_ENT_*` of the blamed attacker (default LARGE when unknown) |
| 0x2C | totalStatusDamage f32 | **never written (always 0)** |

Detection: each tick while standing in, `hp = data.hp`, `mx = data.maxHp`; if `lastHp > 0 && hp < lastHp` and not the 3-tick NoDead clamp, `dmg = lastHp - hp` (integer HP) is accumulated; then HP is rewritten to `mx` ("never faint while standing in") and `lastHp = mx`. So events are per game tick (multiple hits in one tick merge), and the stand-in's armor/defences have already been applied to the HP loss. Poison/rot etc. would show as many small events (each tick <1 HP changes appear only if the integer HP drops).

Guest consumption (reference): keep `lastHits`, `lastTotal`; first read only establishes the baseline; each `hits != lastHits` gives `hostDamage = totalDamage - lastTotal`; `share = hostDamage / hunterMaxHp`; damage mapped to Minecraft: `amount = min(20, (attackerKind==SMALL ? 3: 6) + share*30)`; attacker blamed = nearest enemy proxy to `lastHitFrom` within 32 m, knockback away from `lastHitFrom` with extra `min(1.1, share*3)`. **Recommended for this guest**: use `share = hostDamage/hunterMaxHp` as the fraction of the guest player's max HP to remove (units-free), reset baselines if `hitCount` decreases (host restart zeroes this region only if the file header was re-initialised/). Status damage: the guest handles `totalStatusDamage` but the host never produces it, so DoT shows only as ordinary small events.

### 6.4 Life and death counters

State machine: `NONE` (no character/loading) -> `SETTLING` (wait until the character stops moving) -> `ALIVE` (published, may stand in) -> `DEAD`.

- `hostLife` (+1): when SETTLING completes with `settleMin >= 30` ticks, i.e. after a load (90), a respawn (90) or a game warp (30), **not** after hot reload or a seamless zone/block change (10). Guest reaction: `requestRecall` = teleport the guest player to `state.playerPos` with `playerQuat` yaw and refrain from driving for 400 ms after (4.2 step 4-5). ER, not the guest, decides the spawn place (last Site of Grace).
- `mcDeaths` (+1 by the guest when its player dies, except deaths caused by `hostDeaths`): if ALIVE and the stand-in stood in within 2 s and is not dead, the host calls `ChrIns::Kill(chr, 0)` (the game's own debug kill: death animation, "YOU DIED", runes dropped, respawn at grace) and sets `g_expectDeath` so it does not echo back. The guest then respawns immediately (`doImmediateRespawn`), draws nothing while `HOST_BUSY`, and is moved by the next recall when `hostLife` ticks.
- `hostDeaths` (+1 by the host): the standing-in stand-in died in ER without the guest asking (e.g. below the map: NoDead produces "HP=1" every tick for 3 ticks, then the host lets it die). Guest reaction: kill the player with damage that bypasses creative/invulnerability (: baseline on first read, then `player.hurt(erbridge:host_death, Float.MAX_VALUE)` on change).
- `keepInventory` is forced on in the bridge world.
- If the host `life != ALIVE`: state's PLAYER_VALID clears; control must not be applied (the camera task requires ALIVE) and the stand-in is released (`stand_in(active=false)`).

---

## 7. Action / prompt (interact) protocol

Header fields 0x70-0xBF (section 2.2). Flow:

1. Guest: on its action key (Minecraft: `R`), if overlay active, host not busy and host alive: `mcActionReq += 1` (remember the new value as pending).
2. Host (`service_action`, in the ALIVE block each tick): first call only latches `g_actionSeen = mcActionReq` (requests made before the host's first service are ignored). On change: `perform_action`: requires the build's code signatures to match (`g_actionOk`), the game's `CSActionButtonMan` to have a selected prompt (`+0x20` non-null, text id `+0x2C >= 0`), `+0x29` canExec and not grayed (`+0x2B`); ladder prompts (ActionButtonParam 5000/5010) return -2; else sets the "Event Action tapped" latch byte (`+0x81 = 1`) so the game's own handler consumes it in the next event update; result 1. No input injection. The latch is cleared after 500 ms if not consumed.
3. Host writes `hostActionResult` then `hostActionAck = req` (release order).
4. Guest waits for `hostActionAck == pending` (give up after 2 s): `1` -> resample terrain around the player (door/lever moves collision), `0` "nothing to do here", `-2` "ladders need ER controls", `<0` unsupported.
5. Prompt text: while ALIVE **and standing in** the host publishes the current prompt's FMG string (ActionButtonText category 0x20, DLC 0x16D / 0x1D1) as UTF-8 (<= 63 bytes) in `hostPrompt` with `hostPromptSeq` incremented before and after; empty string = none; unknown text -> `"Action <id>"`. The reference guest reads it without the seqlock; a C# guest should re-read until `hostPromptSeq` is even and unchanged.

The prompt depends on the **stand-in's** position/facing, i.e. on `hunterPos`/`hunterYawDeg` fidelity (reach assist squares it to doors within 2 m of its look ray).

---

## 8. Environment block (time / weather), briefly

`OFF_ENVIRONMENT` 0xB00, guest-written, `ErmcEnvironment` (seq, flags, timeRevision, dayTicks, weatherRevision, weather; 24 B). `flags`: `ENV_TIME=1`, `ENV_WEATHER=2`; 0 releases. `dayTicks` in [0, 24000), **0 = 06:00, 6000 = noon**; host converts `seconds = ((dayTicks % 24000) * 18/5 + 21600) % 86400` and requests that clock time (; it also sets the game's time rate to 0 while owned). `weather`: 0 clear -> WeatherParam suffix 1, 1 rain -> 20, 2 thunder -> 30 (windy rain). Only honoured while ALIVE, fresh `mcHeartbeat` (< 2 s), no scripted native time/weather in effect. **this guest: leave the block zeroed (never publish) so ER keeps its own sky and clock.** If published, the guest must republish `seq` consistently (even, nonzero) and clear flags when exiting.

### 8.1 Host events block (optional extension, `OFF_HOST_EVENTS` 0x360000)

Defined in `protocol/c/bridge_protocol_ext.h` (a separate header; `bridge_protocol.h` is untouched). Written by the host, read by the guest. A host that does not know it (the Elden Ring DLL) never writes it, the region stays zero and the guest sees no magic. `HostLink` zeroes the 0x60 bytes when it initialises a fresh `bridge.shm`, so a stale block from a previous host never survives.

| Offset | Field | Meaning |
| --- | --- | --- |
| 0x00 | `magic` | `0x56454B55` ("UKEV"), written last on the first publish |
| 0x04 | `version` | 1 |
| 0x08 | `seq` | seqlock (odd = writing) |
| 0x0C | `flags` | reserved, 0 |
| 0x10 | `loadoutMode` | 0 = guest decides, 1 = all weapons, 2 = progression |
| 0x14 | `reserved0` | 0 |
| 0x18 | `runSeed` (u64) | identifies the run. A change means "new run": the guest reshuffles and restarts the progression. Must be stable within a run |
| 0x20 | `counters[16]` (u32) | `[0]` bossesDefeated (major/teleporter bosses), `[1]` stagesCleared, `[2]` eliteKills (optional), rest reserved (0). Monotonic within a run; reset to 0 together with a new `runSeed` |

Guest use: with `[Loadout] Mode = Host` it follows `loadoutMode`; in progression it owns `1 + bossesDefeated * UnlocksPerBoss` items out of a seeded random order (see guest-reference.md). `GuestLink.ReadHostEvents` returns false when the magic or version is absent. `HostLink.WriteHostEvents(mode, seed, counters)` writes only when something changed, so calling it every frame is cheap.

---

## 9. frames.shm in detail

### 9.1 File header (`ErmcFramesHeader` + GPU extension), offset 0 of frames.shm

| Off | Field | Writer | Notes |
| --- | --- | --- | --- |
| 0x00 | magic u32 `0x524D484D` ("MHMR") | guest (last) | host checks only at map time |
| 0x04 | version u32 = 3 | guest | |
| 0x08 | latestSlot u32 | guest | **not read by the host** (it scans all 3 slots) |
| 0x0C | reserved | | |
| 0x10 | latestFrameId u64 | guest (release) after each publish | host reads it with an interlocked read **only when compositing is off**, to retire everything up to that id: `g_lastUploaded = max(g_lastUploaded, latestFrameId)`; guest also reads it at startup to keep its counter monotonic. |
| 0x40 | GPU magic u32 `0x47504D43` | host (last, interlocked) | present iff shared textures exist; `gpu_release` writes 0 |
| 0x44 | width u32 | host | |
| 0x48 | height u32 | host | |
| 0x4C | generation u32 | host | `GetTickCount` at creation, part of the resource names |
| 0x50 | host pid u32 | host | |
| 0x54 | slot count u32 = 6 | host | |
| 0x60 + s*8 | ack u64 (s = 0..5) | host | highest frame id of GPU slot `s` the host has finished copying |
| 0xA0 + s*8 | published id u64 (s = 0..5) | guest | frame id the guest published in GPU slot `s` |

`memset(g_frames+0x40, 0, 0xA0)` clears 0x40..0xDF when the host (re)creates GPU textures. **A memory-path-only guest must never write 0x40..0xDF** and must treat them as host-owned.

### 9.2 Slots

3 slots of `ERMC_FRAME_SLOT_SIZE = 0x100 + 3840*2160*16 = 0x7E90100` bytes; slot `i` at `0x1000 + i*0x7E90100`; the Minecraft guest uses `slot = frameId % 3`, and the host indexes by position, not by that formula. Max frame size 3840x2160 (`ERMC_FRAME_MAX_W/H`).

Slot header (0x100 bytes reserved; host-used fields marked):

| Off | Field | Type | Host use |
| --- | --- | --- | --- |
| 0x00 | seq | u32 | guest sets odd while writing, then next even; host skips odd and revalidates after copying (`copy_slot` returns false if changed) |
| 0x04 | width | u32 | 1..3840; must equal the current texture size else the copy is skipped and (on a new size) textures are recreated |
| 0x08 | height | u32 | 1..2160 |
| 0x0C | flags | u32 | bit0 world valid, bit1 GUI valid (**both ignored by the host**), **bit2 hand layer present**, **bit3 GPU slot** (frame lives in shared textures) |
| 0x10 | frameId | u64 | freshness: uploaded only if `> g_lastUploaded`; also the fence value for GPU frames |
| 0x18 | poseId | u64 | == `ErmcControl.mcFrame` of the pose used for this frame; slot selection key |
| 0x20 | mcNear | f32 | guest projection near (see 9.5 for units) |
| 0x24 | mcFar | f32 | guest projection far |
| 0x28 | fovYDeg | f32 | **not read by the host** |
| 0x2C | aspect | f32 | **not read by the host** |
| 0x30 | gpuSlot | u32 | GPU path: which of the 6 shared texture sets holds the frame |
| 0x34 | gpuGeneration | u32 | must equal the host's `generation` or the frame is rejected |
| 0x38-0xFF | unused | | |

The Minecraft publish writes `flags = (hand ? 7 : 3) | (gpu ? 8 : 0)`.

### 9.3 Layers, formats, order

After the 0x100 header, layers are contiguous with `layerBytes = width*height*4` and **no row padding** (`copy_slot`):

| Layer | Offset in slot | Content | Format |
| --- | --- | --- | --- |
| 0 | 0x100 | world color | **BGRA8: bytes B,G,R,A** (memory path) |
| 1 | 0x100 + L | world depth | float32 per pixel, **OpenGL window depth [0,1]** |
| 2 | 0x100 + 2L | GUI/HUD | BGRA8 |
| 3 | 0x100 + 3L | hand + screen effects (optional, flags bit2) | BGRA8 |

- **Row order**: bottom-up (OpenGL): row 0 of the memory is the bottom scanline. The shader samples with `uvMc = (u, 1-v)`, so memory row `height-1` is the top of the screen.
- **Premultiplied alpha** for all colour layers: composite is `gui + scene*(1-gui.a)`, `scene = hand + world*(1-hand.a)`, blended onto the back buffer with `ONE, INV_SRC_ALPHA`. Background must be alpha 0.
- Textures created as `DXGI_FORMAT_B8G8R8A8_UNORM` (layers 0,2,3) and `R32_FLOAT` (layer 1). In the memory path the upload buffer is copied raw, so memory bytes must already be B,G,R,A. The reference guest gets this from `glReadPixels(GL_BGRA, GL_UNSIGNED_INT_8_8_8_8_REV)`.
- **`swapRB`** = `g_texGpu ? 1 : 0` where `g_texGpu` is the GPU flag of the **last uploaded frame**; applied to world, hand and GUI . GPU path: the guest blits RGBA8 GL textures into D3D `B8G8R8A8` memory so the host swaps. Memory path never swaps.
- Alpha handling in the shader: world pixels with `a > 0 && depth < 1.0` are depth-tested/relit/hazed; **world pixels with `a > 0` and `depth == 1.0` are drawn unlit and un-occluded**, as is anything in the hand and GUI layers (hand relit, never occluded; GUI unmodified).

### 9.4 How the host picks and uses slots

1. Present hook `composite` each frame: `mc = control_snapshot && (flags & COMPOSITE) && frames_open`. If not `mc`: retire everything and return.
2. `poseId = pose_for_present(ctrl.poseLag)` -> `slot = pick_slot(poseId)` (3.5).
3. `ensure_frame_resources(slot.width, slot.height)` recreates textures, upload ring (3x), and the GPU shared textures when the size changes (`wait_idle`, expensive: avoid resizing).
4. Waits up to 50 ms on the ring slot's fence; `fresh = slot->frameId > g_lastUploaded`.
5. Memory path (`flags & 8 == 0`): `copy_slot` into the upload heap (3 or 4 layers; the hand layer only if flags bit2 and the slot is big enough), `CopyTextureRegion` into the persistent textures; `g_lastUploaded = frameId`.
6. Depth: host copies its tracked scene depth buffer, reversed-Z, linearised with state `nearZ/farZ` (defaults 0.05 / 10000 if invalid). `useDepth` only if a host depth buffer is tracked and `NO_DEPTH_TEST` is clear. Relight is on when the scene copy exists and `NO_RELIGHT` is clear; fog start 40, end 400 (guest units == metres), strength 0.55 default.
7. The frame is drawn full-screen into the swapchain back buffer (supported formats `R8G8B8A8_UNORM`, `B8G8R8A8_UNORM`, `R10G10B10A2_UNORM`, else compositing is permanently off), only when the swapchain's queue is a direct queue.
8. "Freshness": a frame counts as new only when its `frameId` exceeds the last uploaded id. If the guest ever restarts with a lower counter, nothing uploads until ids exceed the old maximum - hence the time-based seed.

### 9.5 Making the memory path the one that is used; depth rules

To force the memory path the guest must:

1. Never set slot `flags` bit3; leave `0x30/0x34` zero; never open the `Local\ERMCGPU_*` objects. (The host still allocates 6x4 shared textures at the frame size each time resolution changes -> `gpu_init`; about 199 MB of VRAM at 1920x1080 and 797 MB at 3840x2160 by arithmetic: 24 textures x w*h*4 B. It is harmless but not free.)
2. Write each layer as raw bytes at `slot + 0x100 + layerBytes*i` in B,G,R,A (colour) / float32 (depth), bottom-up rows, premultiplied.
3. Publish with the slot `seq` odd->even, set `frameId/poseId/mcNear/mcFar/width/height/flags`, then `frames[0x08]=slot`, release-store `frames[0x10]=frameId`, then publish the control block with `mcFrame == poseId`.
4. Capture at most once per distinct host frame (`hostHeartbeat` changed since the last capture), because the host consumes one pose per game frame and the 3-slot ring would otherwise overwrite poses it still needs.
5. Keep the resolution stable and aspect == ER's `winW/winH`.

Depth encoding expected by `linMc(d)`:
`z = 2d-1; eyeDist = 2*n*f / (f + n - z*(f - n))` with `n = mcNear`, `f = mcFar` from the slot header: i.e. a standard OpenGL perspective depth. The result is compared with the host's linear depth in metres: occluded if `eyeDist > hostDist + 0.03 + 0.004*eyeDist`.
**Unity/ULTRAKILL consequences** (derived, **UNVERIFIED** against the game): Unity on D3D11/12 uses reversed-Z; either convert, or write the layer from a pass that outputs `d = f*(zEye - n) / (zEye*(f - n))` (this inverts the formula above; zEye = view-space distance along the camera forward axis; d is invariant when n and f are scaled together). To compare in host metres when one guest unit = `k` metres, publish `mcNear = n*k`, `mcFar = f*k` (and keep `d` computed from the guest's own n,f). Clear depth value must be `1.0` where nothing was drawn (`md < 1.0` test).

### 9.6 GPU shared-texture path (for later)

Host creation (from its frame-resource setup): for each of `kGpuSlots = 6` and each of 4 layers, a committed D3D12 texture (layer 1 `R32_FLOAT`, others `B8G8R8A8_UNORM`), flags `ALLOW_RENDER_TARGET | ALLOW_SIMULTANEOUS_ACCESS`, heap `SHARED`, shared by name `Local\ERMCGPU_<hostPid>_<generation>_<slot>_<layer>` (`%lu_%u_%d_%d`); a shared fence `Local\ERMCGPU_<hostPid>_<generation>_ready`. Then the frames.shm GPU header (9.1) is filled and the magic `0x47504D43` stored last. Guest import (reference guest: EXT_memory_object_win32 + EXT_semaphore_win32): requires magic, matching host pid, width/height equal to the frame, slot count 6, and a new generation; failures fall back to the memory path (`failedGeneration`).
Per frame (guest): choose capture set `cur = frameCounter % 6`; only if `available(cur)` (`used==0 || ack[cur] >= used[cur]`); `begin(cur)`: wait semaphore at value `used[cur]`; blit colour layers (RGBA bytes into the BGRA textures, hence `swapRB`), draw depth as float into layer 1; `finish(cur, id)`: signal the fence with value `frameId`, `used[cur] = id`; then fill the slot header with `flags |= 8`, `0x30 = cur`, `0x34 = generation`, and write `frames[0xA0 + cur*8] = frameId` (`published`).
Host: `gpu_frame_ready(slot)` requires `index<6`, `generation` match, and `fence.GetCompletedValue >= frameId`; copies the shared textures into the persistent ones, records `g_gpuAckFence/Frame[index]`; when the host's own fence passes that value (`gpu_acknowledge`), it stores the ack frame id at `frames[0x60 + index*8]`. It also acks (releases) stale published ids and unselected slot captures so one unconsumed slot cannot stall the ring. Requires monotonic `frameId`s (they are fence values). GPU path **should be postponed**; start memory-only.

---

## 10. Minimal guest checklist and host assumptions

### 10.1 Common prerequisites (lifecycle)

1. Resolve `ERMC_DIR` (env, else `%TEMP%\ermc`); create the dir; open/extend `bridge.shm` to 8 MiB; map R/W; if `magic/version` mismatch, zero `[0,0x100000)`, set version=1,size=8 MiB, then publish magic (release). Set `mcPid`, `mcStartMs`.
2. Poll `hostHeartbeat` (0x10): alive = changed within 2 s (ignore the first sample). Read `ErmcGameState` with the seqlock. Increment `mcHeartbeat` (0x18) every guest render frame (only matters for the environment rule, but keep it).
3. Read ray mailbox `reqSeq/respSeq`; if unequal wait for them to match before the first submit. Read damage ring `write/read` and continue from `write`.
4. Create `frames.shm` at full size (398,136,064 B) with version 3 and magic last, **before** setting `COMPOSITE`.
5. Zone handling: anchor each `stageId` (ignore 0/-1); on `stageId` change stop driving (flags=0) until re-anchored. React to `hostLife` changes (recall), `mcSwitchReq` changes (F8 from ER), `hostDeaths` changes (kill the player).
6. On guest shutdown/crash handler: write a control block with `flags = 0` (and bump `seq`) so the host stops compositing and releases the stand-in; the host does not time out `COMPOSITE`.

### 10.2 (A) Camera driving + hidden stand-in walking

- Gate: host alive, `PLAYER_VALID`, `stageId` anchored and equal, no HOST_BUSY/PLAYER_DEAD, `recallSettled` (>= 400 ms after teleporting the guest player to `state.playerPos`), and F8 handoff not active.
- Every guest render frame write a control block (seq odd -> payload -> next even; also required at >= 1 Hz): `flags = OVERRIDE_CAMERA | MOVE_HUNTER | HIDE_HUNTER | (COMPOSITE) | (GROUNDED xor FLYING as applicable)`, camera = eye/target/up/vertical FOV (degrees, 5..170) in host stable metres, `hunterPos` = feet, `hunterYawDeg` per 3.4, `poseLag = 1`, `mcFrame` = pose id, `supportEpoch = 0`, `supportTravelY = 0`, lightGain/lightMin/fogStrength = 0 (defaults).
- Render ULTRAKILL at ER's `winW x winH` aspect with the same FOV.
- Consider `HIDE_HUNTER` mandatory (host otherwise shows the stand-in).
- With COMPOSITE: write the control only after the corresponding frame is in the slot (9.5 step 3).
- F8: on guest hotkey write flags=0, `hostFocusReq++`, release input; on `mcSwitchReq` change resume (recall + 400 ms).

### 10.3 (B) Terrain collision (needs a guest-side design; host provides rays only)

- Submit batches through the ray mailbox (<= 8192 rays per batch, one in flight, results in stable-frame metres, `hit` 0/1, synthesised normals, attr 0) and build guest colliders from the hit points; sample around the player (Minecraft: radius 8 m near, 24 m far, ~26 rays per 1 m column, floor ray `+2.2 -> -40` m from feet).
- Resample after any `mcActionReq` result 1, after world strikes (`id==0` damage; wait ~0.9 s), and around opened passages (door changes arrive as new `OFF_PASSAGES` ids).
- Do not use `OFF_CONTACTS` / `OFF_COLLISION_CONTROL`. Platform/passage tables are optional.
- Rays are only answered while the host is ALIVE and are budgeted ~2 ms/game frame: expect latency of 1+ frames per batch.

### 10.4 (C) Combat both ways

- ER -> guest: enemies from the entity table (id, kind, world-aligned AABB `boxCenter +/- boxHalf`, hp/maxHp, flags bit0 dead); host damage via `OFF_HUNTER` counters (baseline on first read; `hitCount`, `totalDamage`, `hunterMaxHp`, `lastHitFrom`, `lastHitKind`); convert `share = hostDamage/hunterMaxHp` into guest HP; kill/respawn via `hostDeaths`/`mcDeaths`.
- Guest -> ER: compute hits against the AABBs yourself, push to the ring `{id, amount, hitPos, flags}` after writing the entry, then bump `write`; ring capacity 256 (check `write - read < 256`). Amount via the section 6.2 formula (send `fraction * clamp(20*sqrt(maxHp/100),10,300)`). `id == 0` + `WORLD_RAY` strikes world props.
- Player death -> `mcDeaths++` (only while standing in; the host kills the stand-in). Remember the host reacts at most once per counter change within 2 s of the last stand-in tick.

### 10.5 (D) Compositing through the memory path

See 9.5. Order per frame: render world (alpha 0 background, premultiplied) -> read back colour (BGRA bytes) + OpenGL-style depth (float32) + GUI layer (+ optional hand layer) bottom-up; write slot; set `latestSlot`, `latestFrameId`; then write control with `mcFrame == poseId`; set `COMPOSITE`. Never write 0x40..0xDF. Keep ids strictly increasing and > any previously published id (seed from `time*1024` and `max(.., latestFrameId)`).


### 10.6 Assumptions of the reference host to be aware of

The protocol was designed around Minecraft, so a few semantics carry Minecraft assumptions. They are harmless for ULTRAKILL once handled as follows; a new host may deviate where it is host-defined (0.2).

| # | Assumption | Risk / how the guest handles it |
| --- | --- | --- |
| 1 | `hunterYawDeg` is Minecraft yaw; facing = `(-sin, -cos)` in host axes | Convert as in 3.4; a wrong sign mirrors the stand-in's facing and breaks door prompts |
| 2 | Damage scale is "Minecraft damage points" (a 20-point mob) | The guest sends the fraction trick (6.2); amounts outside (0,10000) are dropped; every hit is at least 1 HP |
| 3 | Host-to-guest damage is raw host HP lost + `hunterMaxHp` | Use `share = hostDamage / hunterMaxHp`; `hunterMaxHp` is 0 until the first hit |
| 4 | Eye height 1.62 m for world-strike rays; strike reach 0.3-8 m | A guest whose eye height differs sends `hitPos` consistent with that eye |
| 5 | Body dimensions: support probe offsets +/-0.3 m, clearance up to `floor + 2.05..2.1` m, feet-floor gap `[-0.45, +0.4]` m when grounded (4.0 otherwise), door reach assist at chest height 1.0 / knee 0.4 | Only matter for support/platform/door assist; the stand-in's capsule is the host's own, not scaled by the guest's body size |
| 6 | Positions are feet; 1 guest block = 1 m | The guest has its own scale (`MetresPerUnit`, default 0.5: V1 = 3.5 units = 1.75 m) applied consistently to rays, camera, hitboxes, `supportTravelY`, `mcNear/mcFar` and hit points |
| 7 | Block-grid artifacts: 0.5 m platform cells, 1/16 block heights, 1 m door passage sizing | Ignored by continuous colliders |
| 8 | `mcNear/mcFar` are in metres; fog 40..400 m | The guest scales near/far into metres (9.5) |
| 9 | Depth is OpenGL [0,1]; world alpha>0 with depth==1 is drawn un-occluded | The guest provides GL-style depth; translucent depth-less effects are never occluded or relit |
| 10 | Memory-path pixel order BGRA vs GPU RGBA | Match flag bit3 (the guest never sets it) |
| 11 | Guest FOV must equal the camera FOV it sends; aspect equals the host back-buffer aspect | The guest renders at `winW:winH` |
| 12 | Stand-in is forced invulnerable, HP refilled each tick, immobile (host input ignored while driving) | The guest owns player health |
| 13 | Time/weather owner is the guest when `ErmcEnvironment` is written | The guest never writes the block |
| 14 | The reference host patches the game's FPS cap and a projectile parameter row | Informational |
| 15 | `playerPos` is reported minus the reach-assist offset, so a stationary guest near a door can see `playerPos != hunterPos` | Do not use `playerPos` as feedback while driving, except for recalls |
| 16 | The host expects exactly one guest process | Implement a guard (`Launch-Guest.ps1` does) |
| 17 | Entity ids are host object handles; only entities within 80 m, max 256; dead ones persist briefly | The guest removes proxies when `flags&1`, or `hp<=0 && maxHp>0`, or the id disappears |

### 10.7 Unwired / unreliable fields (do not depend on)

`ErmcContacts`, `ErmcCollisionControl`, `hunter.totalStatusDamage`, `state.view/proj`, `STATE_MATRICES_VALID`, `ErmcControl.depthIndex`, `CTRL_CAPTURE_DEPTH`, `ErmcRayHit.attr`, ray flag `CAMERA_FILTER`, ray `filterB/filterC`, damage flags `OUTWARD`/`CRITICAL` (effect-less), frames header `latestSlot`, slot header `fovYDeg`/`aspect`, `hostPresentPage`.

## Appendix A. Reference host per-frame order

For host authors who want to mirror the reference host: the camera override is applied right after the game resolves its own camera (so the guest camera wins). Then, in the game's tick, after physics: handle F8 and window focus; find the player; update the frame/zone and the life state machine; read control freshness; update support and platform cells; apply the stand-in (pin, HP refill, report HP loss); if `ALIVE`: service the ray mailbox (time budget), the damage ring, publish entities, service the action request; otherwise clear the entity table; publish the prompt; finally publish `ErmcGameState` and bump `hostHeartbeat`. Compositing happens in the Present hook, over the finished frame.

## Appendix B. Quick offset reference

bridge.shm: header 0x0, state 0x100, control 0x800, hunter events 0xA00, environment 0xB00, command mailbox 0x1000 (dev tools, ignored), command response 0x2000, rays 0x100000 (rays +0x20, hits +0x30020), entities 0x200000 (+0x10), damage 0x280000 (ring +0x10), passages 0x300000 (+0x10), platforms 0x310000 (+0x10), contacts 0x320000 (entries +0x28), collision control 0x350000, host events (optional) 0x360000.

frames.shm: header 0x0 (GPU extension 0x40-0xDF, host-owned), slot i at `0x1000 + i*0x7E90100`, layers at `slot + 0x100 + i*(w*h*4)`.
