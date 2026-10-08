# Guest reference

Everything about the ULTRAKILL side: how to install and launch it, configuration, hotkeys, capture layers, window modes, logs and troubleshooting. For writing the other side see [writing-a-host.md](writing-a-host.md).

## Requirements

| Component | Supported setup |
| --- | --- |
| OS | Windows 10/11 x64 (the bridge uses kernel32 file mappings and user32 windows) |
| ULTRAKILL | Current Steam build (Unity 2022.3, Mono). The guest boots it straight into the sandbox, which it uses as an empty shell. |
| BepInEx | 5.4.23.5 x64, downloaded by `scripts/Build.ps1` or shipped in the release zip |
| Build | .NET 8 SDK; ULTRAKILL's `ULTRAKILL_Data\Managed` DLLs are referenced from the game folder (MSBuild property `UltrakillDir`) |
| Host | Anything implementing [the protocol](protocol.md) |

You need your own copy of ULTRAKILL. No game files are distributed.

## Install, launch, stop

```powershell
.\scripts\Build.ps1 [-UltrakillDir <folder>] [-Package]   # BepInEx + plugin -> dist\guest (and a release zip)
.\scripts\Install-Guest.ps1 [-UltrakillDir <folder>]      # winhttp.dll + disabled doorstop_config.ini, with backup
.\scripts\Launch-Guest.ps1 [-BridgeDir <folder>]          # start ULTRAKILL as a guest
.\scripts\Stop-Guest.ps1 [-All]                           # close ULTRAKILL, release the host
.\scripts\Uninstall-Guest.ps1                             # remove the two files, restore the backups
```

- **Build** puts a ready-to-use runtime in `dist\guest`: `winhttp.dll`, a `doorstop_config.ini` with `enabled = false`, `BepInEx\core`, `BepInEx\plugins\UltrakillBridge\` and a `BepInEx.cfg` with `HideManagerGameObject = true` (without it, objects the plugin creates before ULTRAKILL's first scene do not survive it). `-Package` also writes `dist\UltrakillBridge-Guest-<version>.zip`, the release artifact: extract it anywhere and pass `-GuestDir` to the scripts.
- **Install** copies only `winhttp.dll` and `doorstop_config.ini` next to `ULTRAKILL.exe` (existing ones are backed up to `runtime\backups\<timestamp>`). With `enabled = false` a normal Steam launch stays vanilla. The record is `runtime\guest-install.json`. `-SteamAppId` also writes `steam_appid.txt` (only needed if the launched game exits at once).
- **Launch** starts `ULTRAKILL.exe` with `--doorstop-enabled true --doorstop-target-assembly <guest dir>\BepInEx\core\BepInEx.Preloader.dll -screen-fullscreen 0 -popupwindow`, so BepInEx runs for that launch only and ULTRAKILL is windowed and borderless. It exports `UKBRIDGE_DIR` and `ERMC_DIR` (the same folder), refuses to start a second guest, and clears a stale control block left by a dead guest. Start the host yourself, with the same bridge folder, or use `scripts\Run-FakeHost.ps1`.
- **Stop** closes ULTRAKILL gracefully (killed after `-GraceSeconds`, default 5) and clears the control block so the host gets its camera and character back. `-All` also closes the fake host and the processes named in `-HostProcessName` (never forced).

Bridge folder resolution (guest, fake host, host SDK): `UKBRIDGE_DIR`, else `ERMC_DIR`, else `%TEMP%\ermc`. If both are set, `UKBRIDGE_DIR` wins. `ERMC_DIR` stays supported because Minecraft Ring's Elden Ring DLL reads only that name.

## Hotkeys

| Key | Action |
| --- | --- |
| ULTRAKILL's own binds | Movement, weapons, punch (F), whiplash (R), variants (E/Q), slots (1-6)... unchanged |
| **V** | Perform the host's interaction (doors, levers, pickups...). Configurable (`InteractKey`) |
| **F8** | Hand control to the host and back. The host's own F8 also returns control. Configurable (`SwitchKey`) |
| **F9** | Bridge diagnostics on screen |
| **F10** | Show the rebuilt terrain inside the host's frame (floors green, walls red, ceilings blue) |

## Configuration

`<guest dir>\BepInEx\config\dev.ukbridge.guest.cfg`, created on the first run (`dist\guest\BepInEx\config\` when launched with the scripts). Out-of-range values are clamped and logged.

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| General | `Enabled` | true | Run the bridge. When false ULTRAKILL behaves normally. |
| General | `MetresPerUnit` | 0.5 | Host metres per ULTRAKILL unit (V1 is 3.5 units tall: 1.75 m at 0.5). Range 0.05-5. |
| General | `AllowCheats` | false | Let ULTRAKILL's cheats work while bridged. Off: the sandbox's auto-enabled cheats are switched off, the cheat menu and the CHEATS ENABLED banner are hidden and no cheat key bind fires (see below). |
| General | `StartInSandbox` | true | Boot straight into the sandbox used as the empty shell. |
| General | `TargetFrameRate` | 120 | ULTRAKILL's frame cap while bridged (vSync is turned off). Match it to the host's. -1 = uncapped. |
| Combat | `HostHpPerUkHp` | 60 | Host HP per point of ULTRAKILL enemy health. Lower = enemies die faster. |
| Combat | `HostDamageScale` | 1 | Multiplier for the damage V1 takes from host hits: `share * 100 * scale`. |
| Combat | `SolidEnemies` | false | Host enemies block V1 and can be stood on (proxy hitboxes on layer 11 instead of 10). |
| Rendering | `Composite` | true | Send V1's viewmodel, effects and HUD to the host to be drawn into its frame. |
| Rendering | `InputOverlay` | true | Glue ULTRAKILL's window, nearly transparent, on top of the host window so it receives the keyboard and mouse. |
| Rendering | `SwitchKey` / `InteractKey` | F8 / V | The control-switch and interaction keys. If equal, the interact key is moved to a free one. |
| Rendering | `WindowMode` | Layered | `Layered`, `Region` or `Tiny` (see below). Environment variable `UKBRIDGE_WINDOW_MODE` overrides. |
| Rendering | `CaptureScale` | 1 | Capture resolution as a fraction of the host window (0.25-1). Lower = faster, blurrier V1 layers. |
| Rendering | `CaptureFlipRows` | false | Flip captured frames vertically. |
| Rendering | `SyncCameraToCapture` | false | Move the host camera only when a captured frame lands (exact alignment, but stutters when captures are slower than the host). Off = smooth camera, newest frame composited. |
| Rendering | `OwnedByHost` | false | Make the host window the owner of the input window (Minecraft Ring style). Attaches both input queues: causes seconds of input lag with Unity hosts. |
| Rendering | `WorldAlpha` | Matte | Alpha of the effects layer: `Matte` (difference matting, see below), `MaxRgb` (alpha from the brightest channel: dark opaque objects turn translucent), `Opaque` or `None`. |
| Rendering | `HandAlpha` / `GuiAlpha` | Opaque / MaxRgb | Alpha repair for the viewmodel and HUD layers: `None`, `Opaque` or `MaxRgb` (`Matte` counts as `MaxRgb` there). |
| Rendering | `HideMainRender` | false | Stop ULTRAKILL's own camera from drawing the (hidden) world, to save GPU time. Experimental. |
| Terrain | `Radius` | 24 | Host terrain is sampled this far (metres) around V1. Range 4-64. |
| Terrain | `CellSize` | 0.5 | Horizontal sampling resolution in metres. Range 0.25-4. |
| Terrain | `StepHeight` | 0.6 | Height difference (m) between neighbouring samples that becomes a wall instead of a slope. |
| Terrain | `PersistentCache` | true | Keep sampled terrain per zone in `<bridge dir>/terrain-cache/<zone>/`. |
| Debug | `Overlay` | false | Start with the F9 diagnostics shown. |

Choose `MetresPerUnit` so that V1 matches your game's characters: V1 is 3.5 units tall, so a 1.8 m character wants about 0.5, a 3.6 m one about 1.0. Movement speeds scale with it (V1 walks at roughly 8 m/s at 0.5, slides at 12, dashes at 25).

## Cheats

While bridged, `AllowCheats = false` (default) keeps the cheats off. The sandbox enabled them three ways, all closed: the "Cheats Enabler" object (`CheatsEnabler.Start` -> `ActivateCheats`, both patched out), `CheatsController.Start` on sandbox maps (`KeepCheatsEnabled`), and a saved keep-enabled preference. `CheatsController.Update` (Konami code, Home/` menu hotkeys, status panels) is skipped, anything already active is disabled through `CheatsManager.SetCheatActive`, and the cheat menu and the CHEATS ENABLED / info panels are hidden. All cheat key binds (V noclip, B flight, M, N, C, O, L, P, I, J, H) go through `CheatsManager.HandleCheatBind`, which requires `cheatsEnabled`, so they do nothing and V is free for the bridge's `InteractKey`. The bridge uses no cheat or sandbox tool. F8/F9/F10 are the bridge's; ULTRAKILL only reads F1/F9/F10 in debug builds or test scenes. Set `AllowCheats = true` to opt back in (the sandbox then behaves as before, including V = noclip; change `InteractKey` if you need V).

## Window modes

The input window sits above the host window. How it hides itself:

| Mode | Technique | Use when |
| --- | --- | --- |
| `Layered` | Full-size window with constant opacity 1/255 (what Minecraft Ring does with GLFW) | Default |
| `Region` | Full-size window clipped with `SetWindowRgn` to a single pixel, no layering | `Layered` shows ULTRAKILL opaque on your setup |
| `Tiny` | The window itself is 1x1 at the host client's bottom-right corner | Last resort; ULTRAKILL renders at 1x1 so captures use the host size explicitly |

The host window must be windowed or borderless (not exclusive fullscreen), or it will minimise when ULTRAKILL takes focus. The host window is found through `hostPid` in `bridge.shm`.

## Capture layers

The guest sends three layers per frame, premultiplied BGRA bottom-up, plus OpenGL-style depth for the world layer (details in [how-it-works.md](how-it-works.md#capture-layers-what-the-host-composites) and [protocol.md section 9](protocol.md#9-framesshm-in-detail)):

| Layer | Content | Alpha repair default |
| --- | --- | --- |
| world | Projectiles, coins, explosions, gibs, particles, plus depth | Matte |
| hand | Arm and weapons (viewmodel) | Opaque |
| gui | Gun/style/finish canvases and the screen-space HUD | MaxRgb |

### World alpha: difference matting

ULTRAKILL's shaders do not write a trustworthy alpha, and `MaxRgb` (alpha = brightest channel) makes a dark or dim but opaque object translucent: the coin's gold mesh faded to nothing while its additive glow (bright, so "correct") stayed. `Matte` renders the world layer twice with the same camera, once over opaque black and once over opaque white. Per channel, black render = `c*a` (the premultiplied colour) and white render = `c*a + (1-a)`, so `a = 1 - (white - black)`; the smallest channel difference is used and the colour is clamped to alpha. Opaque, translucent and additive pixels all come out right and the background (identical in both) is alpha 0. The pass does not depend on the shader's alpha at all.

Cost: one extra world render (the world camera sees only projectiles and effects, no level), one extra GPU readback (about 8 MB at 1080p) and a slightly heavier CPU combine (one integer pass over the layer, in the same loop that copies it into the slot). Set `WorldAlpha = MaxRgb` to go back to a single pass. Matting is world-only; the HUD and viewmodel layers keep their repair modes.

`CaptureFlipRows = true` flips them if V1's arm appears upside down in a host whose texture rows are the other way round.

## Logs and files

| File | Contents |
| --- | --- |
| `<guest dir>\BepInEx\LogOutput.log` | Guest log: attach, zone anchors, recalls, drive/release transitions, window mode, each failure once |
| `<guest dir>\BepInEx\config\dev.ukbridge.guest.cfg` | Configuration |
| `<bridge dir>\bridge.shm`, `frames.shm` | The shared files |
| `<bridge dir>\terrain-cache\<zone>\*.bin` | Persistent terrain cache, one folder per zone (delete to clear) |
| `%USERPROFILE%\AppData\LocalLow\New Blood Interactive\ULTRAKILL\Player.log` | Unity's own log |

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Nothing happens, F9 says "host not running" | The host did not publish `hostHeartbeat`, or the two sides use different bridge folders. Print `UKBRIDGE_DIR`/`ERMC_DIR` on both sides. |
| `Launch-Guest.ps1`: "winhttp.dll is not in the ULTRAKILL folder" | Run `scripts\Install-Guest.ps1`. |
| ULTRAKILL exits at once or restarts through Steam | Install with `-SteamAppId`, and keep Steam running. |
| Host minimises or flickers when V1 takes over | The host must be in borderless/windowed mode. |
| Can't get back into the game after Alt+Tab | Click the middle of the screen. |
| ULTRAKILL is visible on top of the host | Set `WindowMode = Region`. |
| V1's arm/HUD is upside down | Set `CaptureFlipRows = true`. |
| V1's arm/HUD is stretched | The host's back-buffer size and aspect (`bbW/bbH`, `winW/winH`) do not match what it displays. |
| V1 falls through a spot or walks through a wall | Press F10 to see the rebuilt terrain there; check that your ray service ignores characters and triggers. |
| ULTRAKILL crashed and the host is frozen on the last frame / the character does not move | Run `scripts\Stop-Guest.ps1` (the reference compositor never times out `COMPOSITE`). |
| "ULTRAKILL is already running without the bridge" | Close it; BepInEx can only be enabled at startup. |
| Objects created by the plugin disappear after the first scene | `HideManagerGameObject` must be `true` in `BepInEx.cfg` (the build script sets it). |

## Known limitations

- V1's world effects (projectiles, explosions, gore) are drawn on top of the host without depth occlusion unless the host implements the depth comparison, so they can show through walls. The arm and HUD are unaffected.
- The camera follows the captured frames, so it lags V1 by a frame or two.
- Terrain is sampled, not exact: pillars thinner than about 0.3 m can be missed, and areas with several stacked floors keep one floor and one ceiling per 0.5 m column.
- Host enemies cannot be knocked back, pulled or launched by ULTRAKILL; host projectiles do not exist in ULTRAKILL, so parries are melee-timed punches.
- Ladders, menus, maps and similar need the host's controls (F8).
- Single-player only; one guest per host.
- Only the memory path of `frames.shm` is implemented (the GPU shared-texture path is not).
