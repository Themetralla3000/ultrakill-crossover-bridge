using BepInEx.Configuration;
using UnityEngine;

namespace UltrakillBridge.Guest
{
    /// <summary>All user-tunable settings (BepInEx/config/dev.ukbridge.guest.cfg).</summary>
    internal static class BridgeConfig
    {
        // General
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> MetresPerUnit;
        public static ConfigEntry<bool> StartInSandbox;
        public static ConfigEntry<int> TargetFrameRate;
        public static ConfigEntry<bool> AllowCheats;

        // Combat
        public static ConfigEntry<float> HostHpPerUkHp;
        public static ConfigEntry<float> HostDamageScale;
        public static ConfigEntry<bool> SolidEnemies;

        // Rendering / window
        public static ConfigEntry<bool> Composite;
        public static ConfigEntry<bool> InputOverlay;
        public static ConfigEntry<KeyCode> SwitchKey;
        public static ConfigEntry<KeyCode> InteractKey;
        public static ConfigEntry<string> WindowMode;
        public static ConfigEntry<bool> OwnedByHost;
        public static ConfigEntry<float> CaptureScale;
        public static ConfigEntry<bool> CaptureFlipRows;
        public static ConfigEntry<bool> SyncCameraToCapture;
        public static ConfigEntry<string> WorldAlpha, HandAlpha, GuiAlpha;
        public static ConfigEntry<bool> HideMainRender;

        // Terrain
        public static ConfigEntry<float> TerrainRadius;
        public static ConfigEntry<float> TerrainCell;
        public static ConfigEntry<float> TerrainStepHeight;
        public static ConfigEntry<bool> TerrainCache;

        // Loadout
        public static ConfigEntry<string> LoadoutMode;
        public static ConfigEntry<string> ProgressionStart, ProgressionPool;
        public static ConfigEntry<int> UnlocksPerBoss;

        // Debug
        public static ConfigEntry<bool> DebugOverlay;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true, "Run the bridge. When false ULTRAKILL behaves normally.");
            MetresPerUnit = cfg.Bind("General", "MetresPerUnit", 0.5f,
                "Host metres per ULTRAKILL unit. V1 is 3.5 units tall; 0.5 makes it 1.75 m, close to a typical human-sized host character.");
            StartInSandbox = cfg.Bind("General", "StartInSandbox", true,
                "Boot straight into the sandbox (uk_construct) used as the bridge's empty shell.");
            TargetFrameRate = cfg.Bind("General", "TargetFrameRate", 120,
                "ULTRAKILL frame cap while bridged (vSync is turned off). Match it to the host's frame rate.");

            AllowCheats = cfg.Bind("General", "AllowCheats", false,
                "Let ULTRAKILL's cheats work while bridged. Off (default): the sandbox's auto-enabled cheats are switched off, the cheat menu and the CHEATS ENABLED banner are hidden and every cheat key bind (V = noclip, B = flight...) does nothing, so they cannot clash with the bridge's keys.");

            HostHpPerUkHp = cfg.Bind("Combat", "HostHpPerUkHp", 60f,
                "How many host HP one point of ULTRAKILL enemy health is worth (a 221 HP soldier ~ 3.7 UK HP, like a Stray).");
            HostDamageScale = cfg.Bind("Combat", "HostDamageScale", 1f,
                "Damage V1 takes when the stand-in loses a share of its max HP: share * 100 * scale.");
            SolidEnemies = cfg.Bind("Combat", "SolidEnemies", false,
                "Host enemies block V1 and can be stood on (proxy hitboxes on layer 11 instead of 10).");

            Composite = cfg.Bind("Rendering", "Composite", true,
                "Send V1's viewmodel, effects and HUD to the host to be drawn into its frame.");
            InputOverlay = cfg.Bind("Rendering", "InputOverlay", true,
                "Glue ULTRAKILL's window, nearly transparent, on top of the host window so it receives keyboard and mouse.");
            SwitchKey = cfg.Bind("Rendering", "SwitchKey", KeyCode.F8, "Hand control to the host game and back.");
            InteractKey = cfg.Bind("Rendering", "InteractKey", KeyCode.V,
                "Use what the host offers (open doors, pull levers, pick up items, use checkpoints). E/Q/R/F/G are ULTRAKILL's.");
            OwnedByHost = cfg.Bind("Rendering", "OwnedByHost", false,
                "Make the host window the owner of ULTRAKILL's input window (Minecraft Ring's approach). Windows then attaches the two processes' input queues: with hosts that pump messages once per frame (Unity games such as Risk of Rain 2) input lags by up to seconds. Off: kept above the host by re-focusing.");
            WindowMode = cfg.Bind("Rendering", "WindowMode", "Layered",
                "How the input window hides itself above the host: Layered (constant opacity 1/255), Region (full-size window clipped to one pixel; use if Layered shows ULTRAKILL opaque) or Tiny (a 1x1 window). Env UKBRIDGE_WINDOW_MODE overrides.");

            CaptureScale = cfg.Bind("Rendering", "CaptureScale", 1f,
                "Capture resolution as a fraction of the host window (lower = faster, blurrier V1 layer).");
            SyncCameraToCapture = cfg.Bind("Rendering", "SyncCameraToCapture", false,
                "Only move the host camera when a captured frame lands (exact alignment of V1's effects, but the camera moves at the capture rate and can stutter). Off: smooth camera, newest frame composited.");
            CaptureFlipRows = cfg.Bind("Rendering", "CaptureFlipRows", false,
                "Flip captured frames vertically. Turn on if V1's arm and HUD appear upside down in the host.");
            WorldAlpha = cfg.Bind("Rendering", "WorldAlpha", "Matte",
                "Alpha for the effects layer: Matte (render over black and over white: exact alpha for opaque and translucent objects; costs one extra world render + readback), MaxRgb (alpha from the brightest channel; dark objects turn translucent), Opaque or None.");
            HandAlpha = cfg.Bind("Rendering", "HandAlpha", "Opaque", "Alpha repair for the viewmodel layer: None, Opaque or MaxRgb (Matte is world-only and counts as MaxRgb).");
            GuiAlpha = cfg.Bind("Rendering", "GuiAlpha", "MaxRgb", "Alpha repair for the HUD layer: None, Opaque or MaxRgb (Matte is world-only and counts as MaxRgb).");
            HideMainRender = cfg.Bind("Rendering", "HideMainRender", false,
                "Stop ULTRAKILL's own camera from drawing the (hidden) world, to save GPU time. Experimental.");

            TerrainRadius = cfg.Bind("Terrain", "Radius", 24f, "Host terrain is sampled this far (metres) around V1.");
            TerrainCell = cfg.Bind("Terrain", "CellSize", 0.5f, "Horizontal sampling resolution in metres.");
            TerrainStepHeight = cfg.Bind("Terrain", "StepHeight", 0.6f,
                "Height difference (metres) between neighbouring samples that becomes a wall instead of a slope.");
            TerrainCache = cfg.Bind("Terrain", "PersistentCache", true,
                "Keep every sampled terrain cell on disk (<bridge dir>/terrain-cache/<zone>/) so revisited areas have collision instantly; cached cells are re-validated in the background.");

            LoadoutMode = cfg.Bind("Loadout", "Mode", "Host",
                "Which weapons V1 has. Host: do what the host asks (e.g. Risk of Rain 2 asks for Progression), Save when it asks nothing. Save: your normal ULTRAKILL save. All: every weapon and arm. Progression: start with ProgressionStart and unlock one more item (from ProgressionPool, in a random order fixed by the run seed) for each major boss the host reports defeated. Never writes to your ULTRAKILL save (uses the game's forced-loadout mechanism).");
            ProgressionStart = cfg.Bind("Loadout", "ProgressionStart", "rev0,arm0",
                "Comma separated items owned at the start of a progression run. Ids: rev0..2 (Piercer, Sharpshooter, Marksman revolver), sho0..2 (Core Eject, Pump Charge, Sawed-On), nai0..2 (Attractor, Overheat, Sawblade Launcher), rai0..2 (Electric, Screwdriver, Malicious), rock0..2 (Freezeframe, S.R.S., Napalm), arm0..2 (Feedbacker, Knuckleblaster, Whiplash); append 'alt' to rev/sho/nai ids for the alt variant (e.g. sho1alt).");
            ProgressionPool = cfg.Bind("Loadout", "ProgressionPool", "all",
                "Comma separated items that can be unlocked, same ids as ProgressionStart, or 'all'.");
            UnlocksPerBoss = cfg.Bind("Loadout", "UnlocksPerBoss", 1, "Items unlocked per boss defeated (1-10).");

            DebugOverlay =cfg.Bind("Debug", "Overlay", false, "Show bridge diagnostics on screen (toggle with F9).");
            Validate();
        }

        /// <summary>Clamps values that would break the bridge (NaN, zero, negative) and logs a warning for each fix.</summary>
        private static void Validate()
        {
            string lm = (LoadoutMode.Value ?? "").Trim();
            if (!(lm.Equals("Host", System.StringComparison.OrdinalIgnoreCase) || lm.Equals("Save", System.StringComparison.OrdinalIgnoreCase)
                  || lm.Equals("All", System.StringComparison.OrdinalIgnoreCase) || lm.Equals("Progression", System.StringComparison.OrdinalIgnoreCase)))
            {
                Warn(LoadoutMode.Definition.Key, lm, "Host");
                LoadoutMode.Value = "Host";
            }
            if (UnlocksPerBoss.Value < 1 || UnlocksPerBoss.Value > 10) { Warn(UnlocksPerBoss.Definition.Key, UnlocksPerBoss.Value, 1); UnlocksPerBoss.Value = 1; }
            Clamp(MetresPerUnit, 0.05f, 5f, 0.5f);
            Clamp(HostHpPerUkHp, 0.01f, 100000f, 60f);
            Clamp(HostDamageScale, 0f, 20f, 1f);
            Clamp(CaptureScale, 0.25f, 1f, 1f);
            Clamp(TerrainRadius, 4f, 64f, 24f);
            Clamp(TerrainCell, 0.25f, 4f, 0.5f);
            Clamp(TerrainStepHeight, 0.05f, 5f, 0.6f);
            int fps = TargetFrameRate.Value;
            if (fps != -1 && (fps < 20 || fps > 1000))
            {
                int fixedFps = fps <= 0 ? -1 : Mathf.Clamp(fps, 20, 1000);
                Warn(TargetFrameRate.Definition.Key, fps, fixedFps);
                TargetFrameRate.Value = fixedFps;
            }
            if (InteractKey.Value == SwitchKey.Value)
            {
                var alt = SwitchKey.Value == KeyCode.V ? KeyCode.B : KeyCode.V;
                Warn(InteractKey.Definition.Key + " (same as SwitchKey)", InteractKey.Value, alt);
                InteractKey.Value = alt;
            }
        }

        private static void Clamp(ConfigEntry<float> e, float min, float max, float fallback)
        {
            float v = e.Value;
            float fixedV = float.IsNaN(v) || float.IsInfinity(v) ? fallback : Mathf.Clamp(v, min, max);
            if (fixedV == v) return;
            Warn(e.Definition.Key, v, fixedV);
            e.Value = fixedV;
        }

        private static void Warn(string key, object from, object to) =>
            Plugin.Log?.LogWarning($"Config {key} = {from} is out of range; using {to}.");
    }
}
