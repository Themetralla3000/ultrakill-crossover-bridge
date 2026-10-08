using UltrakillBridge.Link;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>
    /// Which damage encoding the guest uses this tick. STAT when the host advertises HostFlagStatDamage and publishes a
    /// valid ErmcHostCombat block (and [Combat] StatDamage is on); the legacy fraction encoding otherwise (Elden Ring).
    /// Also holds the host's damage numbers so the guest can predict the host's damage for its local health.
    /// </summary>
    internal static class StatMode
    {
        public static bool Active;
        public static float Damage = 12f, Scale = 1f, HeadshotMultiplier = 1.5f;
        public static readonly float[] K = new float[Protocol.WeaponSlots];
        private static bool _loggedOnce;
        private static bool _last;

        public static unsafe void Update(GuestLink link, uint hostFlags)
        {
            bool on = false;
            if (BridgeConfig.StatDamage.Value && (hostFlags & Protocol.HostFlagStatDamage) != 0 && link.ReadHostCombat(out ErmcHostCombat c)
                && (c.flags & Protocol.CombatStatsValid) != 0 && c.damage > 0f)
            {
                on = true;
                Damage = c.damage;
                Scale = c.damageScale;
                HeadshotMultiplier = c.headshotMultiplier > 0f ? c.headshotMultiplier : 1f;
                for (int i = 0; i < Protocol.WeaponSlots; i++) K[i] = c.weapons[i * 2];
            }
            Active = on;
            if (!_loggedOnce || on != _last)
            {
                _loggedOnce = true;
                _last = on;
                Plugin.Log.LogInfo(on
                    ? $"Damage wire: STAT (host damage {Damage:F1}, scale {Scale:F2}, weak point x{HeadshotMultiplier:F2})."
                    : "Damage wire: legacy fraction of max HP (host did not advertise stat damage, or [Combat] StatDamage is off).");
            }
        }

        /// <summary>Predicted host damage (host HP) of a hit, for the guest's local health only (crit is not predicted).</summary>
        public static float PredictHostDamage(int weaponId, float ukDamage, bool weakpoint) =>
            StatWire.HostDamage(Damage, Scale, K[weaponId & 63], ukDamage, weakpoint, HeadshotMultiplier);
    }
}
