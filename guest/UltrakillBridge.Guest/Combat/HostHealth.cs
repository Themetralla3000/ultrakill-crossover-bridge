using System;
using HarmonyLib;
using UltrakillBridge.Link;
using UnityEngine;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>
    /// Host-authoritative health, guest side (UltraRain docs/COMBAT-DESIGN.md F.1, phase P4). Active when
    /// <c>[Combat] HealthModel = Host</c> (default), the host advertises <see cref="Protocol.HostFlagOwnsHealth"/> and its
    /// combat block carries valid health. Then:
    ///   - V1's HP is a mirror of the host character: <see cref="HealthWire.UkHp"/> (0..100 bar, shield + barrier as
    ///     overheal up to <c>[Combat] OverhealCap</c>) is written to NewMovement.hp every frame;
    ///   - V1 dies when the host says dead (the guest then bumps mcDeaths like for any death, the host applies its death policy);
    ///   - hits the host reports (hunter events) only play ULTRAKILL's hurt feedback (flash, i-frames, shake, style loss);
    ///     the HP they would remove is already in the mirrored value, so nothing is subtracted twice;
    ///   - every ULTRAKILL heal (blood, parry, soul orbs...) becomes a heal request to the host (NewMovement.GetHealth prefix);
    ///   - V1's dash i-frames, hurt i-frames and parry window are published so the host rejects those hits;
    ///   - damage ULTRAKILL itself deals to V1 (own explosions, hazards) cannot hurt it: it is neutralised in a GetHurt
    ///     prefix/postfix pair (the host body never takes it; documented limitation).
    /// With <c>HealthModel = V1</c> or a host without the capability nothing here does anything.
    /// </summary>
    internal static class HostHealth
    {
        /// <summary>The host owns the health this frame.</summary>
        public static bool Active { get; private set; }
        public static bool HostDead { get; private set; }
        public static int TargetHp { get; private set; } = 100;
        /// <summary>Set while the bridge itself calls GetHurt/GetHealth (the patches then leave the call alone).</summary>
        public static bool Internal;
        public static string Status => Active ? $"host-owned hp {TargetHp}{(HostDead ? " DEAD" : "")}" : "own hp";

        private static bool _deadLatched, _everLogged, _lastLogged;
        private static int _lastTarget = -1;

        public static bool WantHostModel =>
            string.Equals((BridgeConfig.HealthModel.Value ?? "Host").Trim(), "Host", StringComparison.OrdinalIgnoreCase);

        /// <summary>Reads the host's published health. Call once per frame, before the host damage events are applied.</summary>
        public static unsafe void Read(GuestLink link, uint hostFlags)
        {
            bool on = false;
            if (WantHostModel && (hostFlags & Protocol.HostFlagOwnsHealth) != 0 && link.ReadHostCombat(out ErmcHostCombat c)
                && (c.flags & Protocol.CombatHealthValid) != 0)
            {
                on = true;
                HostDead = (c.flags & Protocol.CombatDead) != 0;
                TargetHp = HealthWire.UkHp(c.health, c.fullHealth, c.shield, c.barrier, !HostDead, BridgeConfig.OverhealCap.Value);
            }
            Active = on;
            if (!on) { HostDead = false; _deadLatched = false; _lastTarget = -1; }
            if (!_everLogged || on != _lastLogged)
            {
                _everLogged = true;
                _lastLogged = on;
                Plugin.Log.LogInfo(on
                    ? "Health: host-authoritative (V1's bar mirrors the host character; heals go to the host)."
                    : "Health: V1's own 100 HP ([Combat] HealthModel = V1, or the host does not advertise HostOwnsHealth).");
            }
        }

        /// <summary>The host is gone or the bridge is idle: back to V1's own health model.</summary>
        public static void Reset()
        {
            Active = false; HostDead = false; _deadLatched = false; _lastTarget = -1;
        }

        /// <summary>Writes the mirrored HP into V1 and kills it when the host says dead. Call after the host damage feedback.</summary>
        public static void Mirror(NewMovement nm)
        {
            if (!Active || nm == null) return;
            if (HostDead)
            {
                if (!_deadLatched)
                {
                    _deadLatched = true;
                    if (!nm.dead)
                    {
                        Plugin.Log.LogInfo("The host character is dead; V1 dies.");
                        Kill(nm);
                    }
                }
                return;
            }
            _deadLatched = false;
            if (nm.dead) return;
            // A jump up that V1 did not ask for (Medkit, regeneration burst, level up...): ULTRAKILL's green heal flash.
            if (_lastTarget >= 0 && TargetHp - _lastTarget >= 5 && nm.hpFlash != null)
            {
                try { nm.hpFlash.Flash(1f); } catch { }
            }
            _lastTarget = TargetHp;
            if (nm.hp != TargetHp) nm.hp = TargetHp;
            if (!BridgeConfig.HardDamageVisual.Value && nm.antiHp > 0f) nm.ResetHardDamage();
        }

        /// <summary>A hit the host applied: ULTRAKILL's hurt feedback (flash, sound, shake, i-frames, style) for <paramref name="damage"/> UK HP; the HP itself is the mirror's.</summary>
        public static void Feedback(NewMovement nm, int damage)
        {
            if (nm == null || nm.dead || damage <= 0) return;
            Internal = true;
            try
            {
                nm.hp = Math.Max(nm.hp, damage * 4 + 1); // never let the feedback kill V1; the mirror restores the real value
                nm.GetHurt(damage, true, 1f, false, false, BridgeConfig.HardDamageVisual.Value ? 0.35f : 0f, ignoreInvincibility: true);
            }
            finally
            {
                Internal = false;
                if (!nm.dead && TargetHp > 0) nm.hp = TargetHp;
            }
        }

        private static void Kill(NewMovement nm)
        {
            Internal = true;
            try
            {
                nm.hp = 1;
                nm.GetHurt(99999, false, 0f, ignoreInvincibility: true);
            }
            finally { Internal = false; }
        }

        /// <summary>V1 healed locally (blood, parry...): ask the host to heal its character by the same ULTRAKILL HP.</summary>
        internal static void OnLocalHeal(int health)
        {
            if (health <= 0) return;
            var s = BridgeSession.Instance;
            if (s?.Link == null) return;
            s.Link.RequestHeal(health);
        }

        /// <summary>V1 started a punch.</summary>
        internal static void OnPunch()
        {
            if (!Active) return;
            BridgeSession.Instance?.Link?.NotePunchStart();
        }

        /// <summary>Publishes V1's i-frame and parry-window state for the host (every frame; 0 when not driving).</summary>
        public static void PublishGuestState(GuestLink link, NewMovement nm, bool driving)
        {
            uint f = 0;
            if (Active && driving && nm != null && !nm.dead)
            {
                if (nm.gameObject.layer == 15) f |= nm.hurtInvincibility > 0f ? Protocol.GuestHurtFrames : Protocol.GuestDashing;
                if (ParrySystem.WindowOpen) f |= Protocol.GuestParryWindow;
            }
            link.SetGuestCombatState(f);
        }
    }

    /// <summary>Every ULTRAKILL heal of V1 becomes a heal request while the host owns the health (the original still runs for the flash and sound).</summary>
    [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHealth))]
    internal static class GetHealthPatch
    {
        private static bool _loggedError;

        [HarmonyPrefix]
        private static void Prefix(NewMovement __instance, int health, bool fromExplosion)
        {
            try
            {
                if (!HostHealth.Active || HostHealth.Internal || __instance == null || __instance.dead) return;
                if (__instance.exploded && fromExplosion) return; // ULTRAKILL refuses this heal too
                HostHealth.OnLocalHeal(health);
            }
            catch (Exception e)
            {
                if (_loggedError) return;
                _loggedError = true;
                Plugin.Log.LogError("Heal forwarding failed (logged once): " + e);
            }
        }
    }

    /// <summary>
    /// Damage ULTRAKILL itself deals to V1 (own explosions, hazards) is not part of the host model: the HP buffer in the
    /// prefix keeps it from killing V1 and the postfix puts the mirrored HP back. Bridge-made calls pass through.
    /// </summary>
    [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt))]
    internal static class GetHurtPatch
    {
        [HarmonyPrefix]
        private static void Prefix(NewMovement __instance, int damage)
        {
            try
            {
                if (!HostHealth.Active || HostHealth.Internal || __instance == null || __instance.dead) return;
                if (__instance.hp <= damage * 4) __instance.hp = damage * 4 + 1;
            }
            catch { }
        }

        [HarmonyPostfix]
        private static void Postfix(NewMovement __instance)
        {
            try
            {
                if (!HostHealth.Active || HostHealth.Internal || __instance == null || __instance.dead) return;
                __instance.hp = HostHealth.TargetHp;
            }
            catch { }
        }
    }
}
