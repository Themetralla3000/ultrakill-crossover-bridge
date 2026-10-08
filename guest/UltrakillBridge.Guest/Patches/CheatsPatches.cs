using System;
using System.Collections;
using HarmonyLib;
using ULTRAKILL.Cheats;
using UnityEngine;

namespace UltrakillBridge.Guest.Patches
{
    /// <summary>
    /// Keeps ULTRAKILL's cheats off while bridged (config General/AllowCheats = false, the default). The sandbox turns
    /// them on three ways: the "Cheats Enabler" scene object (CheatsEnabler.Start -> ActivateCheats), the sandbox map's
    /// MapInfoBase.sandboxTools (CheatsManager.KeepCheatsEnabled -> CheatsController.Start sets cheatsEnabled) and a
    /// saved "keep enabled" pref. With cheats on, the cheat key binds (V = noclip, B = flight, ...) fire from
    /// CheatBinds' input actions, which collide with the bridge's keys. Every bind is gated on
    /// CheatsController.cheatsEnabled (CheatsManager.HandleCheatBind), so keeping that flag false makes them inert.
    /// Nothing the bridge needs depends on cheats (it does not use the spawner arm or any sandbox tool).
    /// </summary>
    internal static class CheatsGuard
    {
        private static CheatsManager _swept;

        public static bool Blocked =>
            BridgeConfig.Enabled.Value && !BridgeConfig.AllowCheats.Value
            && BridgeSession.Instance != null && BridgeSession.Instance.InBridgeScene;

        /// <summary>Switches cheats off and hides their UI. Idempotent and cheap; safe to call every frame.</summary>
        public static void Enforce(CheatsController c)
        {
            try
            {
                if (c == null) return;
                CheatsManager mgr = MonoSingleton<CheatsManager>.Instance;
                if (mgr != null && mgr != _swept && TrySweep(mgr)) _swept = mgr;

                if (c.cheatsEnabled)
                {
                    c.cheatsEnabled = false;
                    if (mgr != null && mgr.IsMenuOpen()) mgr.HideMenu();
                    var assist = MonoSingleton<AssistController>.Instance;
                    if (assist != null) assist.cheatsEnabled = false;
                    Plugin.Log.LogInfo("Cheats disabled (General/AllowCheats = false).");
                }
                // The CHEATS ENABLED banner and the info list are kept active by cheatsEnabled only.
                SetActive(c, "cheatsEnabledPanel", false);
                SetActive(c, "cheatsInfoPanel", false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("CheatsGuard: " + e.Message);
            }
        }

        /// <summary>Turns off every cheat that is active (a saved one, or one enabled before the guard ran).</summary>
        private static bool TrySweep(CheatsManager mgr)
        {
            var all = Traverse.Create(mgr).Field("allRegisteredCheats").GetValue() as IDictionary;
            if (all == null || all.Count == 0) return false;   // CheatsManager.Start has not registered them yet
            foreach (object list in all.Values)
            {
                foreach (object o in (IEnumerable)list)
                {
                    var cheat = o as ICheat;
                    if (cheat == null || !cheat.IsActive) continue;
                    try { mgr.SetCheatActive(cheat, false, false); }   // Disable(); no pref is written
                    catch (Exception e) { Plugin.Log.LogWarning("Could not disable cheat " + cheat.Identifier + ": " + e.Message); }
                }
            }
            return true;
        }

        private static void SetActive(CheatsController c, string field, bool value)
        {
            var go = Traverse.Create(c).Field(field).GetValue<GameObject>();
            if (go != null && go.activeSelf != value) go.SetActive(value);
        }
    }

    /// <summary>The sandbox's "Cheats Enabler" object activates cheats on Start.</summary>
    [HarmonyPatch(typeof(CheatsEnabler), "Start")]
    internal static class NoCheatsEnabler
    {
        private static bool Prefix() => !CheatsGuard.Blocked;
    }

    [HarmonyPatch(typeof(CheatsController), nameof(CheatsController.ActivateCheats))]
    internal static class NoActivateCheats
    {
        private static bool Prefix() => !CheatsGuard.Blocked;
    }

    /// <summary>CheatsController.Start enables cheats itself on sandbox maps (KeepCheatsEnabled).</summary>
    [HarmonyPatch(typeof(CheatsController), "Start")]
    internal static class NoCheatsOnStart
    {
        private static void Postfix(CheatsController __instance)
        {
            if (CheatsGuard.Blocked) CheatsGuard.Enforce(__instance);
        }
    }

    /// <summary>
    /// Update drives the Konami-code prompt, the cheat menu hotkeys (Home, `) and the status panels: skipped entirely
    /// while blocked, after forcing everything off.
    /// </summary>
    [HarmonyPatch(typeof(CheatsController), nameof(CheatsController.Update))]
    internal static class NoCheatsUpdate
    {
        private static bool Prefix(CheatsController __instance)
        {
            if (!CheatsGuard.Blocked) return true;
            CheatsGuard.Enforce(__instance);
            return false;
        }
    }
}
