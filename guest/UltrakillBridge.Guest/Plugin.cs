using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UltrakillBridge.Link;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UltrakillBridge.Guest
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.ukbridge.guest";
        public const string Name = "ULTRAKILL Crossover Bridge";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            BridgeConfig.Bind(Config);
            if (!BridgeConfig.Enabled.Value)
            {
                Log.LogInfo("ULTRAKILL Crossover Bridge disabled in config.");
                return;
            }
            // ULTRAKILL ships with runInBackground off: the whole player loop would stop whenever the host window
            // has focus (F8) or is clicked.
            Application.runInBackground = true;
            ApplyRenderSettings();
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            // The chainloader runs before ULTRAKILL's first scene; objects made this early do not survive it.
            // A static scene hook does, so the session is (re)created from there.
            _link = new GuestLink();
            SceneManager.sceneLoaded += OnSceneLoadedStatic;
            Log.LogInfo($"ULTRAKILL Crossover Bridge {Version}: bridge dir {BridgePaths.Dir}");
        }

        private static GuestLink _link;

        private static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode)
        {
            if (BridgeSession.Instance != null) return; // alive (Unity null check); it handles scenes itself
            var go = new GameObject("UKBridge Session");
            go.AddComponent<BridgeMarker>();
            Object.DontDestroyOnLoad(go);
            var session = go.AddComponent<BridgeSession>();
            session.Init(_link);
            Log.LogInfo($"Bridge session created on scene '{scene.name}'.");
            session.HandleSceneLoaded(scene, mode);
        }

        private static void ApplyRenderSettings()
        {
            Render.FrameCapture.Scale = Mathf.Clamp(BridgeConfig.CaptureScale.Value, 0.25f, 1f);
            Render.FrameCapture.FlipRows = BridgeConfig.CaptureFlipRows.Value;
            Render.FrameCapture.SyncCameraToCapture = BridgeConfig.SyncCameraToCapture.Value;
            Render.FrameCapture.WorldAlpha = ParseAlpha(BridgeConfig.WorldAlpha, AlphaFix.MaxRgb);
            Render.FrameCapture.HandAlpha = ParseAlpha(BridgeConfig.HandAlpha, AlphaFix.Opaque);
            Render.FrameCapture.GuiAlpha = ParseAlpha(BridgeConfig.GuiAlpha, AlphaFix.MaxRgb);
            Render.CaptureRig.HideMainRender = BridgeConfig.HideMainRender.Value;
        }

        private static AlphaFix ParseAlpha(BepInEx.Configuration.ConfigEntry<string> entry, AlphaFix fallback)
        {
            if (System.Enum.TryParse(entry.Value, true, out AlphaFix value)) return value;
            Log.LogWarning($"Unknown {entry.Definition.Key} '{entry.Value}', using {fallback}.");
            return fallback;
        }
    }
}
