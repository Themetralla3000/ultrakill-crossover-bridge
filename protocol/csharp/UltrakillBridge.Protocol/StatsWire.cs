using System;

namespace UltrakillBridge.Link
{
    /// <summary>
    /// The math of the stat ratios (ErmcHostCombat ratio fields, <see cref="Protocol.HostFlagStats"/>). Unity-free so hosts,
    /// the guest and the tests share it.
    ///
    /// Host side: every stat is published as a ratio against the stand-in body's base value at its current level, so a
    /// character with no items and no buffs publishes 1.0 (extraJumps 0). <see cref="Ratio"/> builds them, <see cref="Recharge"/>
    /// turns a RoR2 cooldownScale into a rate. Guest side: <see cref="Gain"/> maps a ratio to the multiplier ULTRAKILL gets
    /// (1 + (ratio - 1) * gain, clamped), <see cref="Approach"/> moves the applied value towards it smoothly, and
    /// <see cref="ScaledValue"/> writes base * multiplier into a game field while remembering the field's own value, so turning
    /// the feature off restores exactly what the game had.
    /// </summary>
    public static class StatsWire
    {
        /// <summary>Largest ratio a host publishes (and the guest accepts).</summary>
        public const float MaxRatio = 10f;

        /// <summary>current / baseValue, 1 for a missing / non-positive base or a NaN, clamped to [0, <see cref="MaxRatio"/>].</summary>
        public static float Ratio(float current, float baseValue)
        {
            if (!(baseValue > 1e-4f) || float.IsNaN(current) || float.IsInfinity(current)) return 1f;
            return Clamp(current / baseValue, 0f, MaxRatio);
        }

        /// <summary>Extra jumps: maxJumpCount - baseJumpCount, never negative, capped at 255.</summary>
        public static uint ExtraJumps(int maxJumpCount, int baseJumpCount)
        {
            int d = maxJumpCount - baseJumpCount;
            return d <= 0 ? 0u : (uint)Math.Min(d, 255);
        }

        /// <summary>Recharge rate from a RoR2 cooldownScale (0.5 = cooldowns take half as long = rate 2). 1 when unknown.</summary>
        public static float Recharge(float cooldownScale)
        {
            if (float.IsNaN(cooldownScale) || float.IsInfinity(cooldownScale) || cooldownScale <= 0f) return 1f;
            return Clamp(1f / Math.Max(cooldownScale, 1f / MaxRatio), 0f, MaxRatio);
        }

        /// <summary>
        /// The sprint ratio of Energy Drink style items: sprinting multiplies speed by <c>sprintMultiplier</c> (RoR2 1.45) and
        /// each Energy Drink adds <c>0.25</c> of base speed on top, so the bonus relative to a vanilla sprint is
        /// <c>1 + 0.25 * stacks / (moveRatio * sprintMultiplier)</c>. 1.0 without items.
        /// </summary>
        public static float SprintRatio(int sprintBonusStacks, float moveRatio, float sprintMultiplier)
        {
            if (sprintBonusStacks <= 0 || !(moveRatio > 1e-3f) || !(sprintMultiplier > 1e-3f)) return 1f;
            return Clamp(1f + 0.25f * sprintBonusStacks / (moveRatio * sprintMultiplier), 1f, MaxRatio);
        }

        /// <summary>Guest: the multiplier ULTRAKILL gets for a host <paramref name="ratio"/>: <c>1 + (ratio - 1) * gain</c> clamped to [min, max].</summary>
        public static float Gain(float ratio, float gain, float min, float max)
        {
            if (float.IsNaN(ratio) || float.IsInfinity(ratio)) ratio = 1f;
            if (float.IsNaN(gain)) gain = 0f;
            if (min > max) { float t = min; min = max; max = t; }
            return Clamp(1f + (ratio - 1f) * gain, min, max);
        }

        /// <summary>Guest: the slide multiplier. The slide is V1's sprint, so the movement multiplier and the sprint bonus both apply, with <paramref name="slideGain"/>.</summary>
        public static float SlideMultiplier(float moveRatio, float sprintRatio, float slideGain, float min, float max) =>
            Gain(moveRatio * sprintRatio, slideGain, min, max);

        /// <summary>Moves <paramref name="current"/> towards <paramref name="target"/> by at most <paramref name="maxDelta"/>.</summary>
        public static float Approach(float current, float target, float maxDelta)
        {
            if (float.IsNaN(current)) return target;
            if (maxDelta < 0f) maxDelta = 0f;
            float d = target - current;
            if (Math.Abs(d) <= maxDelta) return target;
            return current + Math.Sign(d) * maxDelta;
        }

        /// <summary>
        /// Extra progress per second a fixed-rate timer needs on top of its own to run at <paramref name="speed"/> times its normal
        /// rate: a timer that advances <c>rate * dt</c> advances <c>rate * dt * (speed - 1)</c> extra. Negative slows it.
        /// </summary>
        public static float ExtraAdvance(float rate, float dt, float speed) => rate * dt * (speed - 1f);

        /// <summary>
        /// How many mid-air jumps a counter still allows: <c>allowed = min(extraJumps, cap)</c>, <c>used</c> resets on the ground.
        /// </summary>
        public static bool CanAirJump(int used, uint extraJumps, int cap) => used < Math.Min((int)Math.Min(extraJumps, 255u), Math.Max(0, cap));

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// Writes <c>base * multiplier</c> into a game float field (walk speed, jump power) without losing the game's own value:
    /// the base is whatever the field held the last time it was NOT what this object wrote (first use, a scene change that
    /// reset it, another mod). With a multiplier of exactly 1 and nothing written yet it touches nothing.
    /// </summary>
    public sealed class ScaledValue
    {
        private float _base, _written;
        private bool _have;

        /// <summary>The game's own value of the field (the base), valid after the first <see cref="Apply"/>.</summary>
        public float Base => _base;
        /// <summary>True while the field holds a value this object wrote that differs from the base.</summary>
        public bool Modified => _have && _written != _base;

        /// <summary>Returns the value the field should now hold. Pass the field's current value and the wanted multiplier.</summary>
        public float Apply(float current, float multiplier)
        {
            if (float.IsNaN(multiplier) || multiplier <= 0f) multiplier = 1f;
            if (!_have || current != _written)
            {
                _base = current;
                _have = true;
            }
            _written = _base * multiplier;
            if (multiplier == 1f) _written = _base;
            return _written;
        }

        /// <summary>Forget the base (a new game object, a new scene).</summary>
        public void Forget() { _have = false; }
    }
}
