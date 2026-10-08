# Changelog

## 0.1.1 — 2026-10-08

- The live camera pose is published every guest frame and the host composites the newest captured frame, so the
  host camera is no longer limited to the capture rate (it stuttered in Risk of Rain 2: 60 fps host, ~39 captures/s).
  `SyncCameraToCapture = true` restores the previous frame-locked behaviour.

## 0.1.0 - 2026-10-08

First release of the standalone kit, extracted from [UltraRing](https://github.com/Themetralla3000/UltraRing) 0.2.0
(tag `v0.2.0`). Experimental. The wire protocol is unchanged: the guest still works with Minecraft Ring's unmodified
Elden Ring host DLL.

- **Layout:** the code is split into `protocol/` (C header, byte-exact C# mirror, netstandard2.1), `host-sdk/` (`HostLink`
  and `HostFrames` for C# hosts), `guest/` (the ULTRAKILL BepInEx plugin), `fake-host/` (a WinForms host for development)
  and `tests/`.
- **Guest** (behaviour identical to UltraRing 0.2.0): camera and stand-in driving, terrain colliders rebuilt from host rays
  with look-ahead and a persistent per-zone cache, hittable enemy proxies for every host enemy, damage both ways with
  parry and whiplash, shared life and recalls, host interactions on the HUD (V), three-layer frame capture, input window
  glued over the host, F8 control switch, F9/F10 diagnostics.
- **Generic naming:** namespaces and assemblies are `UltrakillBridge.*`; BepInEx GUID `dev.ukbridge.guest`, name
  "ULTRAKILL Crossover Bridge", config `BepInEx/config/dev.ukbridge.guest.cfg`; wording in logs and config descriptions
  says "host" instead of "Elden Ring". Config defaults are unchanged.
- **Bridge folder:** `UKBRIDGE_DIR` is accepted next to the original `ERMC_DIR` (`UKBRIDGE_DIR` wins when both are set).
  The window-mode override variable is now `UKBRIDGE_WINDOW_MODE`.
- **Scripts:** `Build.ps1` (downloads BepInEx, builds, stages `dist/guest`, `-Package` for a release zip),
  `Install-Guest.ps1` / `Uninstall-Guest.ps1`, `Launch-Guest.ps1`, `Run-FakeHost.ps1`, `Stop-Guest.ps1`.
- **Docs:** README for modders, a step-by-step host-writing guide with example mappings (Elden Ring, Risk of Rain 2), the
  protocol reference, how it works, the guest reference and ULTRAKILL internals.
- **Tests:** header layout checks, host/guest round trips, bridge folder resolution, terrain-cache tests.
