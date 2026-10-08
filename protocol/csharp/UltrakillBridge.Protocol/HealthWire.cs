using System;

namespace UltrakillBridge.Link
{
    /// <summary>
    /// The math of host-authoritative health (ErmcHostCombat health fields, ErmcGuestRequests heals). Unity-free so hosts,
    /// the guest and the tests share it.
    ///
    /// Mapping host -> V1: ULTRAKILL keeps its 0..100 bar (overheal up to 200). The bar shows the host's combined health as a
    /// percentage of its full health: <c>100 * (health + shield + barrier) / fullHealth</c>, rounded, capped at
    /// <c>overhealCap</c> (200) and at least 1 while the host says alive. Shield and barrier therefore appear as
    /// ULTRAKILL's overheal above 100 and are lost first, exactly like in RoR2. Max-HP items, level ups and curse change
    /// fullHealth and so never change the 100 of the bar, only how much a point of it is worth.
    ///
    /// Mapping V1 -> host heals: one ULTRAKILL HP is <c>fullHealth / 100 * scale</c> host HP (<c>scale</c> is the host's
    /// BloodHealScale). Heals travel as a cumulative counter in thousandths of an ULTRAKILL HP so no request is lost
    /// between two host frames.
    /// </summary>
    public static class HealthWire
    {
        public const float UkFull = 100f;
        public const float DefaultOverhealCap = 200f;
        /// <summary>A single ULTRAKILL heal call is capped here (a parry heals 999: that means "to full").</summary>
        public const float MaxHealPerCall = 200f;

        public static int UkHp(float health, float fullHealth, float shield, float barrier, bool alive, float overhealCap = DefaultOverhealCap)
        {
            if (!alive) return 0;
            if (!(fullHealth > 0f) || float.IsNaN(health + shield + barrier)) return (int)UkFull;
            float combined = Math.Max(0f, health) + Math.Max(0f, shield) + Math.Max(0f, barrier);
            float hp = (float)Math.Round(UkFull * combined / fullHealth, MidpointRounding.AwayFromZero);
            float cap = Math.Max(UkFull, overhealCap);
            if (hp > cap) hp = cap;
            if (hp < 1f) hp = 1f;
            return (int)hp;
        }

        /// <summary>Host HP that <paramref name="ukHp"/> ULTRAKILL HP of healing are worth.</summary>
        public static float HealAmount(float ukHp, float fullHealth, float scale)
        {
            if (!(ukHp > 0f) || !(fullHealth > 0f) || !(scale > 0f)) return 0f;
            return Math.Min(ukHp, MaxHealPerCall) / UkFull * fullHealth * scale;
        }

        /// <summary>Thousandths of an ULTRAKILL HP (rounded, at least 1 for a positive heal).</summary>
        public static uint ToMilli(float ukHp)
        {
            if (!(ukHp > 0f)) return 0;
            float v = Math.Min(ukHp, MaxHealPerCall) * 1000f;
            return (uint)Math.Max(1f, (float)Math.Round(v));
        }

        /// <summary>ULTRAKILL HP healed between two readings of the cumulative counter; 0 for a backwards (restarted) counter.</summary>
        public static float MilliDelta(uint now, uint last)
        {
            uint d = unchecked(now - last);
            if (d == 0 || d > 0x7FFFFFFFu) return 0f;
            return d / 1000f;
        }
    }
}
