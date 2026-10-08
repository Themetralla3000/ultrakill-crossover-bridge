using System;

namespace UltrakillBridge.Link
{
    /// <summary>
    /// Weapon ids of the stat damage extension (ErmcDamage.reserved bits 0..5; bridge_protocol_ext.h has the same list).
    /// 0 and 63 both mean "unknown". The numbers follow the combat design table (UltraRain docs/COMBAT-DESIGN.md C.2).
    /// </summary>
    public static class WeaponId
    {
        public const int Unknown = 0;
        public const int RevShot = 1;        // revolver primary (all variants), also the beam that follows a coin
        public const int RevPiercer = 2;     // charged revolver beam (tryForExplode), Piercer / Sharpshooter
        public const int RevMarksman = 3;    // charged revolver beam, Marksman (ricochet)
        public const int CoinHit = 4;        // the coin itself was hit by a beam / punch
        public const int ShoPellet = 5;      // shotgun pellets (Projectile)
        public const int ShoZone = 6;        // shotgun point blank zone
        public const int ShoOvercharge = 7;  // Pump Charge overcharge explosion
        public const int ShoGrenade = 8;     // Core Eject grenade explosion
        public const int ShoSaw = 9;         // Sawed-On saw (chainsaw*)
        public const int Hammer = 10;        // hammer alts (hammer, hammerzone, hammer explosion)
        public const int Nail = 11;
        public const int NailBurst = 12;     // reserved (not distinguishable from Nail in the guest yet)
        public const int Sawblade = 13;
        public const int Zapper = 14;
        public const int RailBeam = 15;
        public const int RailMalicious = 16;
        public const int RailHarpoon = 17;   // harpoon / drill / drillpunch
        public const int Rocket = 18;
        public const int Cannonball = 19;
        public const int Napalm = 20;
        public const int Punch = 21;
        public const int Knuckle = 22;
        public const int Whip = 23;
        public const int Slam = 24;
        public const int Parry = 25;
        public const int ExplosionOther = 26;
        public const int FireOther = 27;
        public const int Fallback = 63;

        /// <summary>Highest id in use + 1 (table rows 0..Count-1 are named).</summary>
        public const int Count = 28;

        private static readonly string[] Names =
        {
            "UNKNOWN", "REV_SHOT", "REV_PIERCER", "REV_MARKSMAN", "COIN_HIT", "SHO_PELLET", "SHO_ZONE", "SHO_OVERCHARGE",
            "SHO_GRENADE", "SHO_SAW", "HAMMER", "NAIL", "NAIL_BURST", "SAWBLADE", "ZAPPER", "RAIL_BEAM", "RAIL_MALICIOUS",
            "RAIL_HARPOON", "ROCKET", "CANNONBALL", "NAPALM", "PUNCH", "KNUCKLE", "WHIP", "SLAM", "PARRY",
            "EXPLOSION_OTHER", "FIRE_OTHER",
        };

        public static string NameOf(int id) => id == Fallback ? "FALLBACK" : (id >= 0 && id < Names.Length ? Names[id] : "ID" + id);

        /// <summary>Id of a table name (case-insensitive); -1 when unknown.</summary>
        public static int FromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (string.Equals(name, "FALLBACK", StringComparison.OrdinalIgnoreCase)) return Fallback;
            for (int i = 0; i < Names.Length; i++)
                if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>ErmcDamage.reserved bits 24..27 (STAT entries).</summary>
    public static class HitKind
    {
        public const int Direct = 0, Area = 1, DamageOverTime = 2, Melee = 3, CoinChain = 4;
    }

    /// <summary>
    /// Pack / unpack of the stat damage entry (<c>ErmcDamage.reserved</c>):
    /// bits 0..5 weapon id, 8..15 hit count (1..255), 16..23 shot sequence (wraps), 24..27 hit kind.
    /// Unity-free, shared by the guest, the hosts and the tests.
    /// </summary>
    public static class StatWire
    {
        public static uint Pack(int weaponId, int hitCount, int shotSeq, int hitKind)
        {
            if (hitCount < 1) hitCount = 1; else if (hitCount > 255) hitCount = 255;
            return (uint)(weaponId & 63) | ((uint)hitCount << 8) | ((uint)(shotSeq & 255) << 16) | ((uint)(hitKind & 15) << 24);
        }

        public static int WeaponOf(uint reserved) => (int)(reserved & 63u);
        public static int HitCountOf(uint reserved) { int n = (int)((reserved >> 8) & 255u); return n < 1 ? 1 : n; }
        public static int ShotSeqOf(uint reserved) => (int)((reserved >> 16) & 255u);
        public static int HitKindOf(uint reserved) => (int)((reserved >> 24) & 15u);

        /// <summary>flags word of a STAT entry.</summary>
        public static uint Flags(bool weakpoint, bool area, bool fraction = false) =>
            Protocol.DamageStat | (weakpoint ? Protocol.DamageWeakpoint : 0u) | (area ? Protocol.DamageExplosion : 0u) |
            (fraction ? Protocol.DamageFraction : 0u);

        /// <summary>
        /// Host damage of a STAT entry: <c>bodyDamage * scale * k * ukDamage</c>, times the weak point multiplier when
        /// flagged. The guest uses it to predict the host's damage; the host to apply it (crit is added by RoR2 itself).
        /// </summary>
        public static float HostDamage(float bodyDamage, float scale, float k, float ukDamage, bool weakpoint, float headshotMultiplier) =>
            bodyDamage * scale * k * ukDamage * (weakpoint ? headshotMultiplier : 1f);
    }

    /// <summary>
    /// Groups hits into shots for hosts that roll crit once per shot (RoR2 rolls once per BulletAttack):
    /// one roll per (weaponId, shotSeq), cached for <see cref="Ttl"/> ms. Unity-free.
    /// </summary>
    public sealed class ShotRollCache
    {
        public const long Ttl = 1500;
        private readonly long[] _stamp = new long[64 * 256];
        private readonly bool[] _value = new bool[64 * 256];
        private readonly bool[] _used = new bool[64 * 256];

        /// <summary>The cached result for the shot, or <paramref name="roll"/> evaluated once and remembered.</summary>
        public bool Get(int weaponId, int shotSeq, long nowMs, Func<bool> roll)
        {
            int i = ((weaponId & 63) << 8) | (shotSeq & 255);
            if (_used[i] && nowMs >= _stamp[i] && nowMs - _stamp[i] <= Ttl) return _value[i];
            _value[i] = roll();
            _stamp[i] = nowMs;
            _used[i] = true;
            return _value[i];
        }
    }
}
