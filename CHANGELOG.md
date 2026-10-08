# Changelog

## Unreleased

- **Host-authoritative health (combat phase c).** New host capability `HostFlagOwnsHealth` (`ErmcHostEvents.flags` bit 3). The host
  publishes its character's `health`, `fullHealth`, `shield`, `fullShield`, `barrier`, `cursePenalty` and a `DEAD` bit in
  `ErmcHostCombat` (`HostLink.WriteHostHealth` / `ClearHostHealth`; flags `CombatHealthValid` / `CombatDead`). With
  `[Combat] HealthModel = Host` (new, default; `V1` = the old 100 HP model) the guest mirrors it onto V1's bar
  (`HealthWire.UkHp`: 100 x combined health / full health, shield + barrier as overheal up to `OverhealCap` 200), kills V1 when the
  host says dead, and turns host hits into hurt feedback only (no HP subtracted twice). Every ULTRAKILL heal (blood, parry) becomes a
  heal request (`NewMovement.GetHealth` prefix); damage ULTRAKILL deals to V1 itself is neutralised (`GetHurt` prefix/postfix).
  `ErmcGuestRequests` grows from 0x30 to 0x40 bytes (version unchanged, `extFlags` bit 0 says the tail is valid): `combatFlags`
  (dashing, hurt i-frames, parry window), cumulative `healMilli`, `punchSeq`. A hit the host rejected for the parry window
  is reported with `HunterKindParried` and the guest performs the parry (`ParrySystem.TryParry(forced)`). New config
  `[Combat] HealthModel`, `OverhealCap`, `HardDamageVisual`. Protocol library: `HealthWire`, `GuestLink.SetGuestCombatState/RequestHeal/NotePunchStart`.
  Fake host: owns a health pool with regen, shield, barrier, heals, dodge / parry rejection and lethal interception (`O`, `G`, `J`).
  Tests: layouts, mapping and heal maths, wire round trips. Hosts that never set the bit are unaffected.

- **Stat damage model and hit log (combat phases a and b).** Optional protocol extension for hosts with their own damage
  maths. New host capability `HostFlagStatDamage` (`ErmcHostEvents.flags` bit 2) and new optional host -> guest block
  `ErmcHostCombat` at `0x361000` (`UKCB`: the character's damage/crit stats and a `{k, proc}` table per weapon id).
  While the host advertises it the guest sends *stat entries* in the damage ring: `amount` = raw ULTRAKILL damage,
  `flags` STAT/WEAKPOINT/EXPLOSION/FRACTION (bits 4..7), `reserved` = weapon id (28 ids, `WeaponId`), hit count, shot
  sequence and hit kind (`StatWire`). The guest classifies hits from `eid.hitter`, `sourceWeapon` (type + variation),
  `tryForExplode` and `hitterWeapons`, counts shots with prefixes on the weapons' fire methods, aggregates hits per
  (weapon, head, shot, kind) and predicts the host's damage for the local enemy health. Without the bit (Elden Ring) the
  wire is byte-identical to before. Config `[Combat] StatDamage = true`. Host SDK: `HostLink.WriteHostCombat`,
  `WeaponTable` (defaults from the combat design, per-weapon rows to override), `ProcBudget`; protocol library:
  `StatWire`, `ShotRollCache`, `GuestLink.PushDamage(..., reserved)`, `GuestLink.ReadHostCombat`. Fake host: stat path
  with a fake body (damage 12, crit 10 %), per-weapon damage / hits / procs per second in the overlay, `T` toggles.
  Tests: layout, packing and round trip, proc budget maths, crit-per-shot grouping, hit classification.
- **Hit log probe.** `[Combat] HitLog = true` appends every proxy hit to `<bridge dir>/hitlog.csv`;
  `scripts/hitlog-summary.ps1` computes per weapon hits/s, damage per hit, hits per shot and UK DPS, to replace the
  design table's estimates.

- **Native-feeling interactions.** `ErmcHostEvents.flags` is now defined: `HostDrawsPrompt` (the guest hides its own
  `[V] Open` label, config `[Interaction] ShowGuestPrompt = Auto|true|false`) and `HostNeedsInput` (a host menu needs
  the mouse: the guest enters host mode like F8 and returns when the flag clears; `[Interaction] AutoHostInput`).
  New optional guest -> host block `ErmcGuestRequests` at `0x360100` (`UKRQ`): `useEquipment` and `ping` counters,
  held-key bits and the interact key name; keys `[Interaction] EquipmentKey = T`, `PingKey = Mouse2`. Holding the
  interact key repeats the action (`HoldRepeat`, `RepeatIntervalMs = 250`). `HostLink.WriteHostEvents(..., flags)`,
  `HostLink.ReadGuestRequests`, `GuestLink.RequestEquipment/RequestPing/SetHeldKeys/SetInteractKeyLabel`. No change for
  hosts that ignore them. Fake host: `P` native prompt, `I` menu open, equipment/ping counters. Layout and round-trip tests.

- **Weapon progression.** New optional protocol block `ErmcHostEvents` at `0x360000` (`bridge_protocol_ext.h`,
  `Protocol.OffHostEvents`): the host publishes a requested loadout mode (guest / all / progression), a run seed and
  counters (`bossesDefeated`, `stagesCleared`). No change for existing hosts (the Elden Ring DLL never writes it).
  `HostLink.WriteHostEvents`, `GuestLink.ReadHostEvents`. The guest's new `LoadoutManager` starts with one weapon and
  unlocks a seeded random weapon variant per boss defeated, through ULTRAKILL's forced loadout (the save is never
  touched) and announces unlocks on the HUD. Config `[Loadout] Mode/ProgressionStart/ProgressionPool/UnlocksPerBoss`;
  default `Mode = Host`, which is the old behaviour when the host sends nothing. Fake host: `B` boss defeated, `N` new
  run, `M` cycle the requested mode. Tests for the layout, the round trip and the deterministic unlock order.
- **Cheats are off while bridged.** The sandbox auto-enabled them (CHEATS ENABLED banner, V toggled noclip while the
  cheat menu was open). The Cheats Enabler, `CheatsController.Start/Update` and `ActivateCheats` are patched, active
  cheats are disabled and the cheat UI hidden. `[General] AllowCheats = true` opts back in.
- **Coins and other dark or saturated opaque projectiles render fully.** `WorldAlpha = MaxRgb` derived alpha from the
  brightest channel, so only the coin's bright glow survived. New default `WorldAlpha = Matte`: the world layer is
  rendered over black and over white and alpha is `1 - (white - black)` (exact for opaque, translucent and additive).
  Cost: one extra world render and readback (~8 MB at 1080p). `MaxRgb` restores the single pass. New
  `AlphaFix.Matte`, `GuestFrames.Matte` and `WriteLayerMatte` in the protocol library (unit tested). No wire change.

## 0.1.2 — 2026-10-08

- The input window is no longer owned by the host window by default (`OwnedByHost = false`). Cross-process ownership
  attaches the two processes' input queues; with a host that pumps messages once per frame (Unity games such as
  Risk of Rain 2) ULTRAKILL's mouse and keyboard lagged by up to seconds. The window is kept above the host by taking
  the focus back whenever the host comes to the front while V1 is played.

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
