using System;
using HarmonyLib;
using UltrakillBridge.Link;
using UnityEngine;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>
    /// The ULTRAKILL hooks of the host stats (UltraRain docs/COMBAT-DESIGN.md F.3). Every hook reads the smoothed multipliers
    /// of <see cref="HostStats"/> and does nothing at exactly 1.0 (except handing back a value it wrote earlier), so with the
    /// feature off the game runs untouched. Each patch is installed on its own and only logs when the method is missing
    /// (game update); a failing hook logs once and disables itself.
    ///
    ///   walk / air / slide / dash   NewMovement.FixedUpdate prefix: writes walkSpeed = base * multiplier for the whole step
    ///                               (Move() uses it for walking and air control, Dodge() for the slide and the dash)
    ///   jump force                  same prefix on NewMovement.jumpPower
    ///   extra jumps                 NewMovement.HandleInputs prefix + postfix: a jump press that the game ignored in mid-air calls Jump()
    ///   revolver                    Revolver.Update prefix: extra shootCharge progress (the shot timer, 200 per second)
    ///   nailgun                     Nailgun.FixedUpdate prefix: extra fireCooldown drain (100 per second)
    ///   rocket launcher             RocketLauncher.Update prefix: extra cooldown drain
    ///   punch                       FistControl.Update prefix: extra fistCooldown drain
    ///   shotgun / hammer            Update postfix: Animator.speed (their ready-to-fire event is an animation event)
    ///   alt fire recharges          WeaponCharges.Charge prefix: amount * multiplier (+ postfix for the railcannon's own rate)
    ///   dash stamina                NewMovement.Update postfix: extra boostCharge regeneration
    /// </summary>
    internal static class StatPatches
    {
        private static readonly ScaledValue Walk = new ScaledValue(), JumpForce = new ScaledValue();
        private static int _airJumpsUsed;
        private static bool _airJumpLogged;
        private static readonly System.Collections.Generic.HashSet<string> Failed = new System.Collections.Generic.HashSet<string>();

        private static readonly AccessTools.FieldRef<NewMovement, bool> JumpCooldown = Ref<NewMovement, bool>("jumpCooldown");
        private static readonly AccessTools.FieldRef<Revolver, bool> ShootReady = Ref<Revolver, bool>("shootReady");
        private static readonly AccessTools.FieldRef<Nailgun, float> NailCooldown = Ref<Nailgun, float>("fireCooldown");
        private static readonly AccessTools.FieldRef<RocketLauncher, float> RocketCooldown = Ref<RocketLauncher, float>("cooldown");
        private static readonly AccessTools.FieldRef<Shotgun, Animator> ShotgunAnim = Ref<Shotgun, Animator>("anim");
        private static readonly AccessTools.FieldRef<ShotgunHammer, Animator> HammerAnim = Ref<ShotgunHammer, Animator>("anim");

        private static AccessTools.FieldRef<T, F> Ref<T, F>(string name)
        {
            try { return AccessTools.FieldRefAccess<T, F>(name); }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Stats: {typeof(T).Name}.{name} not found ({e.Message}); the hook that needs it is off.");
                return null;
            }
        }

        public static void Install(Harmony harmony)
        {
            Hook(harmony, typeof(NewMovement), "FixedUpdate", nameof(MovementPrefix), null);
            Hook(harmony, typeof(NewMovement), "HandleInputs", nameof(HandleInputsPrefix), nameof(HandleInputsPostfix));
            Hook(harmony, typeof(NewMovement), "Update", null, nameof(MovementUpdatePostfix));
            Hook(harmony, typeof(Revolver), "Update", nameof(RevolverPrefix), null);
            Hook(harmony, typeof(Nailgun), "FixedUpdate", nameof(NailgunPrefix), null);
            Hook(harmony, typeof(RocketLauncher), "Update", nameof(RocketPrefix), null);
            Hook(harmony, typeof(FistControl), "Update", nameof(FistPrefix), null);
            Hook(harmony, typeof(Shotgun), "Update", null, nameof(ShotgunPostfix));
            Hook(harmony, typeof(ShotgunHammer), "Update", null, nameof(HammerPostfix));
            Hook(harmony, typeof(WeaponCharges), "Charge", nameof(ChargePrefix), nameof(ChargePostfix));
        }

        private static void Hook(Harmony harmony, Type type, string method, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                var pre = prefix == null ? null : AccessTools.Method(typeof(StatPatches), prefix);
                var post = postfix == null ? null : AccessTools.Method(typeof(StatPatches), postfix);
                if (target == null) { Plugin.Log.LogWarning($"Stats: {type.Name}.{method} not found (that hook is off)."); return; }
                harmony.Patch(target, prefix: pre == null ? null : new HarmonyMethod(pre), postfix: post == null ? null : new HarmonyMethod(post));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Stats: could not patch {type.Name}.{method}: {e.Message}");
            }
        }

        /// <summary>Logs a hook failure once per hook; the hook keeps running (it throws again, silently) but never breaks the game.</summary>
        private static void Fail(string hook, Exception e)
        {
            if (Failed.Add(hook)) Plugin.Log.LogError($"Stats hook {hook} failed (logged once): {e}");
        }

        // ---- movement ------------------------------------------------------------------------------------------

        private static void MovementPrefix(NewMovement __instance)
        {
            try
            {
                var nm = __instance;
                float mul = 1f;
                if (HostStats.Move != 1f || HostStats.Slide != 1f || Walk.Modified)
                {
                    mul = nm.boost
                        ? (nm.sliding ? HostStats.Slide : (BridgeConfig.DashScalesWithSpeed.Value ? HostStats.Move : 1f))
                        : HostStats.Move;
                    nm.walkSpeed = Walk.Apply(nm.walkSpeed, mul);
                }
                if (HostStats.Jump != 1f || JumpForce.Modified)
                    nm.jumpPower = JumpForce.Apply(nm.jumpPower, HostStats.Jump);
            }
            catch (Exception e) { Fail("movement", e); }
        }

        // ---- extra jumps ---------------------------------------------------------------------------------------

        private static void HandleInputsPrefix(NewMovement __instance, out int __state)
        {
            __state = __instance != null ? __instance.currentWallJumps : 0;
        }

        private static void HandleInputsPostfix(NewMovement __instance, int __state)
        {
            try
            {
                var nm = __instance;
                if (nm == null || nm.gc == null) return;
                if (nm.gc.onGround) { _airJumpsUsed = 0; return; }
                int extra = HostStats.ExtraJumps;
                if (extra <= 0 || JumpCooldown == null) return;
                if (nm.dead || !nm.activated || nm.sliding || nm.gc.heavyFall) return;
                if (!MonoSingleton<InputManager>.Instance.InputSource.Jump.WasPerformedThisFrame) return;
                // The game already jumped (ground / coyote / enemy step: it arms the jump cooldown) or wall-jumped: not ours.
                if (JumpCooldown(nm) || nm.currentWallJumps != __state) return;
                if (!StatsWire.CanAirJump(_airJumpsUsed, (uint)extra, BridgeConfig.MaxExtraJumps.Value)) return;
                _airJumpsUsed++;
                nm.Jump();
                if (!_airJumpLogged)
                {
                    _airJumpLogged = true;
                    Plugin.Log.LogInfo($"Stats: first extra mid-air jump ({extra} granted by the host).");
                }
            }
            catch (Exception e) { Fail("extra jumps", e); }
        }

        /// <summary>Dash stamina regeneration (utility cooldown reduction).</summary>
        private static void MovementUpdatePostfix(NewMovement __instance)
        {
            try
            {
                float k = HostStats.RechargeDash;
                var nm = __instance;
                if (k == 1f || nm == null || nm.sliding || nm.slowMode || nm.boostCharge >= 300f) return;
                nm.boostCharge = Mathf.Clamp(nm.boostCharge + StatsWire.ExtraAdvance(70f, Time.deltaTime, k), 0f, 300f);
            }
            catch (Exception e) { Fail("dash recharge", e); }
        }

        // ---- attack speed --------------------------------------------------------------------------------------

        private static void RevolverPrefix(Revolver __instance)
        {
            try
            {
                float k = HostStats.Attack;
                if (k == 1f || ShootReady == null || ShootReady(__instance)) return;
                // Stay below 100 so the game's own code completes the timer (and sets shootReady) this frame.
                __instance.shootCharge = Mathf.Clamp(__instance.shootCharge + StatsWire.ExtraAdvance(200f, Time.deltaTime, k), 0f, 99.9f);
            }
            catch (Exception e) { Fail("revolver", e); }
        }

        private static void NailgunPrefix(Nailgun __instance)
        {
            try
            {
                float k = HostStats.Attack;
                if (k == 1f || NailCooldown == null) return;
                ref float cd = ref NailCooldown(__instance);
                if (cd > 0f) cd = Mathf.Max(0f, cd - StatsWire.ExtraAdvance(100f, Time.deltaTime, k));
            }
            catch (Exception e) { Fail("nailgun", e); }
        }

        private static void RocketPrefix(RocketLauncher __instance)
        {
            try
            {
                float k = HostStats.Attack;
                if (k == 1f || RocketCooldown == null) return;
                ref float cd = ref RocketCooldown(__instance);
                if (cd > 0f) cd = Mathf.Max(0f, cd - StatsWire.ExtraAdvance(1f, Time.deltaTime, k));
            }
            catch (Exception e) { Fail("rocket", e); }
        }

        private static void FistPrefix(FistControl __instance)
        {
            try
            {
                float k = HostStats.Attack;
                if (k == 1f || __instance.fistCooldown <= 0f) return;
                __instance.fistCooldown = Mathf.Max(0f, __instance.fistCooldown - StatsWire.ExtraAdvance(2f, Time.deltaTime, k));
            }
            catch (Exception e) { Fail("punch", e); }
        }

        private static void ShotgunPostfix(Shotgun __instance) => SetAnimatorSpeed(ShotgunAnim == null ? null : ShotgunAnim(__instance), "shotgun");
        private static void HammerPostfix(ShotgunHammer __instance) => SetAnimatorSpeed(HammerAnim == null ? null : HammerAnim(__instance), "hammer");

        private static void SetAnimatorSpeed(Animator anim, string hook)
        {
            try
            {
                if (anim == null) return;
                float k = BridgeConfig.AttackAnimations.Value ? HostStats.Attack : 1f;
                if (anim.speed == k) return;
                if (k == 1f && !_animTouched) return; // never write when we have not touched it
                if (k != 1f) _animTouched = true;
                anim.speed = k;
            }
            catch (Exception e) { Fail(hook, e); }
        }
        private static bool _animTouched;

        // ---- recharge ------------------------------------------------------------------------------------------

        private static void ChargePrefix(ref float amount, out float __state)
        {
            __state = amount;
            // CutsceneSkip charges everything with the skipped time at once; leave that alone.
            if (HostStats.RechargeAlt != 1f && amount <= 0.5f) amount *= HostStats.RechargeAlt;
        }

        private static void ChargePostfix(WeaponCharges __instance, float __state)
        {
            try
            {
                float sp = HostStats.RechargeSpecial, alt = HostStats.RechargeAlt;
                if (sp == alt || __state > 0.5f || __instance.raicharge >= 5f) return;
                // The railcannon charges at 0.25 per second; the prefix already gave it the alt-fire rate, add the difference.
                __instance.raicharge = Mathf.Clamp(__instance.raicharge + 0.25f * __state * (sp - alt), 0f, 5f);
            }
            catch (Exception e) { Fail("railcannon recharge", e); }
        }
    }
}
