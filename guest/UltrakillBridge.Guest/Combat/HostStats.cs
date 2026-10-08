using System;
using UltrakillBridge.Link;
using UnityEngine;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>
    /// The host's RoR2-style stats, guest side (UltraRain docs/COMBAT-DESIGN.md F.3, phase P5). Active when
    /// <c>[Stats] Enabled</c>, the host advertises <see cref="Protocol.HostFlagStats"/> and its combat block carries valid
    /// ratios (<see cref="Protocol.CombatRatiosValid"/>) and V1 is driven. The ratios (1.0 = the host character's base) are
    /// mapped to the multipliers below with the config gains and caps (<see cref="StatsWire.Gain"/>) and followed smoothly
    /// (<see cref="StatsWire.Approach"/>); <see cref="StatPatches"/> reads these multipliers from the ULTRAKILL hooks. When the
    /// feature turns off (config, capability gone, host lost, not driving) every target is 1.0 so the hooks hand the game its own
    /// numbers back.
    /// </summary>
    internal static class HostStats
    {
        public static bool Active { get; private set; }

        /// <summary>Walk and air speed multiplier (NewMovement.walkSpeed outside slide / dash).</summary>
        public static float Move { get; private set; } = 1f;
        /// <summary>walkSpeed multiplier while sliding.</summary>
        public static float Slide { get; private set; } = 1f;
        public static float Jump { get; private set; } = 1f;
        /// <summary>Weapon cycle speed (revolver, nailgun, rocket, punch, shotgun / hammer animators).</summary>
        public static float Attack { get; private set; } = 1f;
        /// <summary>WeaponCharges.Charge multiplier (alt fire recharges).</summary>
        public static float RechargeAlt { get; private set; } = 1f;
        /// <summary>Railcannon charge multiplier.</summary>
        public static float RechargeSpecial { get; private set; } = 1f;
        /// <summary>Dash stamina regeneration multiplier.</summary>
        public static float RechargeDash { get; private set; } = 1f;
        /// <summary>Mid-air jumps granted (already capped by [Stats] MaxExtraJumps).</summary>
        public static int ExtraJumps { get; private set; }

        public static string Status => Active
            ? $"host stats move x{Move:F2} slide x{Slide:F2} attack x{Attack:F2} jump x{Jump:F2} +{ExtraJumps} air jump(s) recharge x{RechargeAlt:F2}/{RechargeSpecial:F2}/{RechargeDash:F2}"
            : "own stats";

        private static float _tMove = 1f, _tSlide = 1f, _tJump = 1f, _tAttack = 1f, _tAlt = 1f, _tSpecial = 1f, _tDash = 1f;
        private static bool _everLogged, _lastLogged;

        /// <summary>Reads the host's ratios and moves the applied multipliers one step. Call once per frame.</summary>
        public static void Update(GuestLink link, uint hostFlags, bool driving, float dt)
        {
            bool on = false;
            ErmcHostCombat c = default;
            if (BridgeConfig.StatsEnabled.Value && driving && (hostFlags & Protocol.HostFlagStats) != 0 && link.ReadHostCombat(out c)
                && (c.flags & Protocol.CombatRatiosValid) != 0)
                on = true;

            if (on) Targets(c); else ResetTargets();
            float step = Math.Max(0.01f, BridgeConfig.StatSmoothing.Value) * Math.Max(0f, dt);
            Move = StatsWire.Approach(Move, _tMove, step);
            Slide = StatsWire.Approach(Slide, _tSlide, step);
            Jump = StatsWire.Approach(Jump, _tJump, step);
            Attack = StatsWire.Approach(Attack, _tAttack, step);
            RechargeAlt = StatsWire.Approach(RechargeAlt, _tAlt, step);
            RechargeSpecial = StatsWire.Approach(RechargeSpecial, _tSpecial, step);
            RechargeDash = StatsWire.Approach(RechargeDash, _tDash, step);
            Active = on;
            if (!_everLogged || on != _lastLogged)
            {
                _everLogged = true;
                _lastLogged = on;
                Plugin.Log.LogInfo(on
                    ? "Stats: the host's movement / attack speed / jumps / recharge drive V1 ([Stats] section)."
                    : "Stats: V1 keeps ULTRAKILL's own numbers ([Stats] Enabled is off, the host does not advertise HostStats, or V1 is not driven).");
            }
        }

        /// <summary>The host is gone or the bridge is idle: every multiplier back to 1 at once.</summary>
        public static void Reset()
        {
            Active = false;
            ResetTargets();
            Move = Slide = Jump = Attack = RechargeAlt = RechargeSpecial = RechargeDash = 1f;
        }

        private static void ResetTargets()
        {
            _tMove = _tSlide = _tJump = _tAttack = _tAlt = _tSpecial = _tDash = 1f;
            ExtraJumps = 0;
        }

        private static void Targets(in ErmcHostCombat c)
        {
            float mMin = BridgeConfig.MoveSpeedMin.Value, mMax = BridgeConfig.MoveSpeedMax.Value;
            _tMove = BridgeConfig.MoveSpeedOn.Value ? StatsWire.Gain(c.moveSpeedRatio, BridgeConfig.MoveSpeedGain.Value, mMin, mMax) : 1f;
            _tSlide = BridgeConfig.SlideSpeedOn.Value
                ? StatsWire.SlideMultiplier(c.moveSpeedRatio, c.sprintSpeedRatio, BridgeConfig.SlideGain.Value, mMin, mMax) : 1f;
            _tJump = BridgeConfig.JumpPowerOn.Value ? StatsWire.Gain(c.jumpPowerRatio, BridgeConfig.JumpPowerGain.Value, 1f, BridgeConfig.JumpPowerMax.Value) : 1f;
            _tAttack = BridgeConfig.AttackSpeedOn.Value
                ? StatsWire.Gain(c.attackSpeedRatio, BridgeConfig.AttackSpeedGain.Value, BridgeConfig.AttackSpeedMin.Value, BridgeConfig.AttackSpeedMax.Value) : 1f;
            float rg = BridgeConfig.RechargeGain.Value, rMin = BridgeConfig.RechargeMin.Value, rMax = BridgeConfig.RechargeMax.Value;
            _tAlt = BridgeConfig.RechargeOn.Value ? StatsWire.Gain(c.rechargeSecondary, rg, rMin, rMax) : 1f;
            _tSpecial = BridgeConfig.RechargeOn.Value ? StatsWire.Gain(c.rechargeSpecial, rg, rMin, rMax) : 1f;
            _tDash = BridgeConfig.DashRechargeOn.Value ? StatsWire.Gain(c.rechargeUtility, rg, rMin, rMax) : 1f;
            ExtraJumps = BridgeConfig.ExtraJumpsOn.Value ? (int)Math.Min(c.extraJumps, (uint)Math.Max(0, BridgeConfig.MaxExtraJumps.Value)) : 0;
        }
    }
}
