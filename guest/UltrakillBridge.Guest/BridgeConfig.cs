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
        public static ConfigEntry<bool> StatDamage;
        public static ConfigEntry<bool> HitLog;
        public static ConfigEntry<string> HealthModel;
        public static ConfigEntry<float> OverhealCap;
        public static ConfigEntry<bool> HardDamageVisual;

        // Stats (RoR2 stats drive V1; UltraRain docs/COMBAT-DESIGN.md F.3)
        public static ConfigEntry<bool> StatsEnabled;
        public static ConfigEntry<bool> MoveSpeedOn, SlideSpeedOn, DashScalesWithSpeed, JumpPowerOn, ExtraJumpsOn, AttackSpeedOn, AttackAnimations, RechargeOn, DashRechargeOn;
        public static ConfigEntry<float> MoveSpeedGain, MoveSpeedMin, MoveSpeedMax, SlideGain, JumpPowerGain, JumpPowerMax;
        public static ConfigEntry<float> AttackSpeedGain, AttackSpeedMin, AttackSpeedMax, RechargeGain, RechargeMin, RechargeMax, StatSmoothing;
        public static ConfigEntry<int> MaxExtraJumps;

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

        // Interaction (InteractKey itself lives in Rendering for config compatibility)
        public static ConfigEntry<string> ShowGuestPrompt;
        public static ConfigEntry<KeyCode> EquipmentKey, PingKey;
        public static ConfigEntry<bool> HoldRepeat;
        public static ConfigEntry<int> RepeatIntervalMs;
        public static ConfigEntry<bool> AutoHostInput;

        // Terrain
        public static ConfigEntry<float> TerrainRadius;
        public static ConfigEntry<float> TerrainCell;
        public static ConfigEntry<float> TerrainStepHeight;
        public static ConfigEntry<bool> VoidRescue;
        public static ConfigEntry<float> VoidRescueDepth;
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
            StatDamage = cfg.Bind("Combat", "StatDamage", true,
                "Use the stat damage wire when the host offers it (it advertises HostStatDamage): raw ULTRAKILL damage + weapon id + hit count + shot sequence, so the host can apply its own damage/crit/proc maths. Off (or a host without it, e.g. Elden Ring): damage is sent as a fraction of the enemy's max HP.");
            HealthModel = cfg.Bind("Combat", "HealthModel", "Host",
                "Whose health V1 has. Host: when the host advertises HostOwnsHealth (Risk of Rain 2 does) V1's bar mirrors the host character (100 * combined health / full health; shield and barrier show as overheal), V1 dies when the host character dies, blood and parry heals heal the host character, and dash / hurt i-frames and the punch parry make the host ignore those hits. V1: ULTRAKILL's own 100 HP, hits taken as a share of the host's max HP (the old behaviour; also what hosts without the capability get).");
            OverhealCap = cfg.Bind("Combat", "OverhealCap", 200f,
                "Largest value of V1's bar while the host owns the health: ULTRAKILL shows up to 200 (overheal). Shield + barrier above 100 % of the host's full health are shown up to this.");
            HardDamageVisual = cfg.Bind("Combat", "HardDamageVisual", false,
                "Keep ULTRAKILL's yellow hard-damage bar while the host owns the health. It is only a visual there (the host heals regardless), so it is off by default.");
            HitLog = cfg.Bind("Combat", "HitLog", false,
                "Measurement probe: append every hit on a host enemy to <bridge dir>/hitlog.csv (time, hitter, weapon, multipliers, head/limb, shot sequence...). Summarise with scripts/hitlog-summary.ps1. Does not change behaviour.");
            SolidEnemies = cfg.Bind("Combat", "SolidEnemies", false,
                "Host enemies block V1 and can be stood on (proxy hitboxes on layer 11 instead of 10).");

            StatsEnabled = cfg.Bind("Stats", "Enabled", true,
                "Apply the stats the host publishes (it advertises HostStats; Risk of Rain 2 does): movement speed, attack speed, extra jumps and recharge rates of its character drive V1, so Goat Hoof, Soldier's Syringe, Hopoo Feather, Alien Head... work. Everything is a ratio against the host character's base stats (level 1, no items = x1.0). Off, or a host without the capability: ULTRAKILL's own numbers, nothing is touched.");
            MoveSpeedOn = cfg.Bind("Stats", "MoveSpeed", true, "Host movement speed scales V1's walk and air speed (NewMovement.walkSpeed).");
            MoveSpeedGain = cfg.Bind("Stats", "MoveSpeedGain", 0.6f,
                "How much of the host's movement bonus V1 gets: multiplier = 1 + (ratio - 1) * gain. ULTRAKILL is already fast; 0.6 turns RoR2's +50 % into +30 %.");
            MoveSpeedMin = cfg.Bind("Stats", "MoveSpeedMin", 0.5f, "Lowest movement multiplier (chill / slows can slow V1 down to this).");
            MoveSpeedMax = cfg.Bind("Stats", "MoveSpeedMax", 2f, "Highest movement multiplier. Terrain sampling around V1 ([Terrain] RadiusMetres) limits how fast is safe.");
            SlideSpeedOn = cfg.Bind("Stats", "SlideSpeed", true, "The slide is V1's sprint: host movement speed times the sprint bonus (Energy Drink) scales it, with SlideGain.");
            SlideGain = cfg.Bind("Stats", "SlideGain", 0.4f, "Gain for the slide: multiplier = 1 + (moveRatio * sprintRatio - 1) * gain, clamped like the walk multiplier.");
            DashScalesWithSpeed = cfg.Bind("Stats", "DashScalesWithSpeed", false, "Also scale the dash's distance with the walk multiplier. Off (default): the dash always covers ULTRAKILL's distance, dodge i-frames included.");
            JumpPowerOn = cfg.Bind("Stats", "JumpPower", true, "Host jump power scales V1's jump force (NewMovement.jumpPower).");
            JumpPowerGain = cfg.Bind("Stats", "JumpPowerGain", 0.5f, "Gain for the jump force: multiplier = 1 + (ratio - 1) * gain, never below 1.");
            JumpPowerMax = cfg.Bind("Stats", "JumpPowerMax", 1.5f, "Highest jump force multiplier.");
            ExtraJumpsOn = cfg.Bind("Stats", "ExtraJumps", true,
                "Hopoo Feather and friends: every extra jump of the host character is one mid-air jump for V1 (ULTRAKILL has none; it uses the normal jump force and does not replace wall jumps). The counter resets on the ground.");
            MaxExtraJumps = cfg.Bind("Stats", "MaxExtraJumps", 3, "Most mid-air jumps V1 gets whatever the host publishes (0-10).");
            AttackSpeedOn = cfg.Bind("Stats", "AttackSpeed", true,
                "Host attack speed makes V1's weapons cycle faster: revolver shot timer, nailgun fire rate, rocket launcher cooldown, punch cooldown, and the shotgun / hammer animation speed (their shot timing is an animation event). Charged / alt-fire recharges are NOT attack speed, see Recharge.");
            AttackSpeedGain = cfg.Bind("Stats", "AttackSpeedGain", 0.75f, "multiplier = 1 + (ratio - 1) * gain.");
            AttackSpeedMin = cfg.Bind("Stats", "AttackSpeedMin", 0.75f, "Lowest attack speed multiplier (a slowing debuff cannot make V1 slower than this).");
            AttackSpeedMax = cfg.Bind("Stats", "AttackSpeedMax", 2.5f, "Highest attack speed multiplier.");
            AttackAnimations = cfg.Bind("Stats", "AttackSpeedAnimations", true, "Scale the shotgun and hammer animators with the attack speed (their ready-to-fire event comes from the animation). Off: those weapons keep their normal cadence.");
            RechargeOn = cfg.Bind("Stats", "Recharge", true,
                "Host cooldown reduction (Alien Head, Purity, Light Flux Pauldron...) speeds up the recharge of every alt fire and special charge (WeaponCharges.Charge: Piercer, coins, Marksman, grenade, saw, heat sinks, zapper, magnets, railcannon, cannonball, napalm, freeze time).");
            DashRechargeOn = cfg.Bind("Stats", "DashRecharge", true, "The host character's utility cooldown reduction speeds up V1's dash stamina regeneration.");
            RechargeGain = cfg.Bind("Stats", "RechargeGain", 0.6f, "multiplier = 1 + (rate - 1) * gain, where rate = 1 / the host's cooldown scale.");
            RechargeMin = cfg.Bind("Stats", "RechargeMin", 0.75f, "Lowest recharge multiplier.");
            RechargeMax = cfg.Bind("Stats", "RechargeMax", 2.5f, "Highest recharge multiplier (a stack of Alien Heads would otherwise refill the railcannon at once).");
            StatSmoothing = cfg.Bind("Stats", "SmoothingPerSecond", 3f, "How fast the applied multipliers follow the targets, in multiplier units per second (3: a +0.3 bonus takes 0.1 s). Hosts publish steps (buffs expire); this avoids visible jerks.");

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

            ShowGuestPrompt = cfg.Bind("Interaction", "ShowGuestPrompt", "Auto",
                "Draw the guest's own '[V] Open' label on ULTRAKILL's HUD. Auto: only when the host does not draw its own prompt (Risk of Rain 2 does, with its own highlight and cost); true / false force it.");
            EquipmentKey = cfg.Bind("Interaction", "EquipmentKey", KeyCode.T,
                "Use the host character's equipment (Risk of Rain 2: the active item). None disables. ULTRAKILL itself does not use T.");
            PingKey = cfg.Bind("Interaction", "PingKey", KeyCode.Mouse2,
                "Ping what the crosshair is on (Risk of Rain 2 ping). Mouse2 is the middle mouse button; ULTRAKILL does not use it. None disables.");
            HoldRepeat = cfg.Bind("Interaction", "HoldRepeat", true,
                "Holding the interact key repeats the interaction while the host still offers one (e.g. buying from a Shrine of Chance until it is spent).");
            RepeatIntervalMs = cfg.Bind("Interaction", "RepeatIntervalMs", 250,
                "Milliseconds between repeats while holding the interact key (Risk of Rain 2's own cadence is 250).");
            AutoHostInput = cfg.Bind("Interaction", "AutoHostInput", true,
                "When the host opens a menu that needs the mouse (item pickers, scrapper, Command, ...) hand input to the host window and take it back when it closes.");

            TerrainRadius = cfg.Bind("Terrain", "Radius", 24f, "Host terrain is sampled this far (metres) around V1.");
            TerrainCell = cfg.Bind("Terrain", "CellSize", 0.5f, "Horizontal sampling resolution in metres.");
            TerrainStepHeight = cfg.Bind("Terrain", "StepHeight", 0.6f,
                "Height difference (metres) between neighbouring samples that becomes a wall instead of a slope.");
            VoidRescue = cfg.Bind("Terrain", "VoidRescue", true,
                "Bring V1 back to the last ground it stood on when it falls far below it (a hole in the sampled terrain).");
            VoidRescueDepth = cfg.Bind("Terrain", "VoidRescueDepth", 40f,
                "Metres below the last ground before the void rescue triggers (after 1.5 s of falling that deep).");
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
            string sg = (ShowGuestPrompt.Value ?? "").Trim();
            if (!(sg.Equals("Auto", System.StringComparison.OrdinalIgnoreCase) || sg.Equals("true", System.StringComparison.OrdinalIgnoreCase)
                  || sg.Equals("false", System.StringComparison.OrdinalIgnoreCase)))
            {
                Warn(ShowGuestPrompt.Definition.Key, sg, "Auto");
                ShowGuestPrompt.Value = "Auto";
            }
            string hm = (HealthModel.Value ?? "").Trim();
            if (!(hm.Equals("Host", System.StringComparison.OrdinalIgnoreCase) || hm.Equals("V1", System.StringComparison.OrdinalIgnoreCase)))
            {
                Warn(HealthModel.Definition.Key, hm, "Host");
                HealthModel.Value = "Host";
            }
            Clamp(OverhealCap, 100f, 200f, 200f);
            Clamp(MoveSpeedGain, 0f, 2f, 0.6f); Clamp(MoveSpeedMin, 0.1f, 1f, 0.5f); Clamp(MoveSpeedMax, 1f, 4f, 2f); Clamp(SlideGain, 0f, 2f, 0.4f);
            Clamp(JumpPowerGain, 0f, 2f, 0.5f); Clamp(JumpPowerMax, 1f, 3f, 1.5f); Clamp(MaxExtraJumps, 0, 10, 3);
            Clamp(AttackSpeedGain, 0f, 2f, 0.75f); Clamp(AttackSpeedMin, 0.1f, 1f, 0.75f); Clamp(AttackSpeedMax, 1f, 5f, 2.5f);
            Clamp(RechargeGain, 0f, 2f, 0.6f); Clamp(RechargeMin, 0.1f, 1f, 0.75f); Clamp(RechargeMax, 1f, 5f, 2.5f); Clamp(StatSmoothing, 0.1f, 100f, 3f);
            Clamp(RepeatIntervalMs, 50, 2000, 250);
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
            foreach (var e in new[] { EquipmentKey, PingKey })
                if (e.Value != KeyCode.None && (e.Value == SwitchKey.Value || e.Value == InteractKey.Value || e.Value == KeyCode.Mouse0 || e.Value == KeyCode.Mouse1))
                {
                    Warn(e.Definition.Key + " (clashes with another key)", e.Value, KeyCode.None);
                    e.Value = KeyCode.None;
                }
            if (EquipmentKey.Value != KeyCode.None && EquipmentKey.Value == PingKey.Value)
            {
                Warn(PingKey.Definition.Key + " (same as EquipmentKey)", PingKey.Value, KeyCode.None);
                PingKey.Value = KeyCode.None;
            }
        }

        private static void Clamp(ConfigEntry<int> e, int min, int max, int fallback)
        {
            int v = e.Value;
            int fixedV = v < min || v > max ? fallback : v;
            if (fixedV == v) return;
            Warn(e.Definition.Key, v, fixedV);
            e.Value = fixedV;
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
