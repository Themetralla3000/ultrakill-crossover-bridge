using System;
using UltrakillBridge.Link;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>
    /// Maps what ULTRAKILL tells DeliverDamage (<c>eid.hitter</c>, the last <c>eid.hitterWeapons</c> entry, the type and
    /// variation of <c>sourceWeapon</c>, <c>tryForExplode</c>) to a stat damage weapon id (see <see cref="WeaponId"/>) and
    /// a hit kind. Unity-free so it can be unit-tested; the strings are the ones the decompile sets. Anything unknown
    /// falls back to the explosion / fire / unknown ids, never throws.
    /// </summary>
    internal static class WeaponClassifier
    {
        /// <param name="hitter"><c>eid.hitter</c>.</param>
        /// <param name="lastWeapon">Last entry of <c>eid.hitterWeapons</c> ("revolver0", "shotgun1", "hammer", ...), or "".</param>
        /// <param name="sourceType">Component type name on <c>sourceWeapon</c> ("Revolver", "Shotgun", "Nailgun", "Railcannon", "RocketLauncher", "ShotgunHammer", "Punch") or "".</param>
        /// <param name="variation">Variation of that component (0..2) or -1.</param>
        public static int Classify(string hitter, string lastWeapon, string sourceType, int variation, bool tryForExplode,
            bool fromExplosion, out int kind)
        {
            hitter = hitter ?? ""; lastWeapon = lastWeapon ?? ""; sourceType = sourceType ?? "";
            kind = HitKind.Direct;
            switch (hitter)
            {
                case "revolver":
                    if (tryForExplode) return variation == 2 ? WeaponId.RevMarksman : WeaponId.RevPiercer;
                    return WeaponId.RevShot;
                case "coin": kind = HitKind.CoinChain; return WeaponId.CoinHit;
                case "shotgunzone": return WeaponId.ShoZone;
                case "chainsaw": case "chainsawbounce": case "chainsawprojectile": case "chainsawzone":
                    kind = HitKind.DamageOverTime; return WeaponId.ShoSaw;
                case "hammer": case "hammerzone": kind = HitKind.Melee; return WeaponId.Hammer;
                case "nail": return tryForExplode ? WeaponId.NailBurst : WeaponId.Nail;
                case "sawblade": return WeaponId.Sawblade;
                case "zapper": case "zap": kind = HitKind.DamageOverTime; return WeaponId.Zapper;
                case "railcannon":
                    bool malicious = sourceType == "Railcannon" ? variation == 2 : lastWeapon == "railcannon2";
                    return malicious ? WeaponId.RailMalicious : WeaponId.RailBeam;
                case "harpoon": case "drill": case "drillpunch":
                    kind = hitter == "harpoon" ? HitKind.Direct : HitKind.DamageOverTime; return WeaponId.RailHarpoon;
                case "cannonball": return WeaponId.Cannonball;
                case "punch": kind = HitKind.Melee; return WeaponId.Punch;
                case "heavypunch": kind = HitKind.Melee; return WeaponId.Knuckle;
                case "hook": kind = HitKind.Melee; return WeaponId.Whip;
                case "ground slam": kind = HitKind.Melee; return WeaponId.Slam;
                case "fire":
                    kind = HitKind.DamageOverTime;
                    return sourceType == "RocketLauncher" || (sourceType.Length == 0 && lastWeapon.StartsWith("rocket", StringComparison.Ordinal)) ? WeaponId.Napalm : WeaponId.FireOther;
                case "lightningbolt": kind = HitKind.Area; return WeaponId.ExplosionOther;
                case "explosion": case "ffexplosion":
                    kind = HitKind.Area;
                    return ClassifyExplosion(lastWeapon, sourceType, variation);
            }
            if (hitter.StartsWith("shotgun", StringComparison.Ordinal)) return WeaponId.ShoPellet; // Projectile.bulletType of the pellets
            if (fromExplosion) { kind = HitKind.Area; return ClassifyExplosion(lastWeapon, sourceType, variation); }
            return WeaponId.Fallback;
        }

        // eid.hitterWeapons only gets NEW names appended, so its last entry can be stale: the type of sourceWeapon wins,
        // the name is only a fallback when the explosion has no source weapon.
        private static int ClassifyExplosion(string lastWeapon, string sourceType, int variation)
        {
            switch (sourceType)
            {
                case "ShotgunHammer": return WeaponId.Hammer;
                case "RocketLauncher": return WeaponId.Rocket;
                case "Railcannon": return WeaponId.RailMalicious;
                case "Shotgun": return variation == 1 ? WeaponId.ShoOvercharge : WeaponId.ShoGrenade;
            }
            if (lastWeapon.StartsWith("hammer", StringComparison.Ordinal)) return WeaponId.Hammer;
            if (lastWeapon.StartsWith("rocket", StringComparison.Ordinal)) return WeaponId.Rocket;
            if (lastWeapon.StartsWith("railcannon", StringComparison.Ordinal)) return WeaponId.RailMalicious;
            if (lastWeapon == "shotgun1") return WeaponId.ShoOvercharge;
            if (lastWeapon.StartsWith("shotgun", StringComparison.Ordinal)) return WeaponId.ShoGrenade;
            if (lastWeapon == "heavypunch") return WeaponId.Knuckle;
            return WeaponId.ExplosionOther;
        }

        /// <summary>Fire counter group of a weapon id (see ShotTracker): 0 = none (hits use the guest tick as shot key).</summary>
        public static int ShotGroup(int weaponId)
        {
            switch (weaponId)
            {
                case WeaponId.RevShot: case WeaponId.RevPiercer: case WeaponId.RevMarksman: case WeaponId.CoinHit: return ShotGroups.Revolver;
                case WeaponId.ShoPellet: case WeaponId.ShoZone: case WeaponId.ShoOvercharge: case WeaponId.ShoGrenade: return ShotGroups.Shotgun;
                case WeaponId.RailBeam: case WeaponId.RailMalicious: case WeaponId.RailHarpoon: return ShotGroups.Railcannon;
                case WeaponId.Rocket: case WeaponId.Cannonball: return ShotGroups.Rocket;
                case WeaponId.Punch: case WeaponId.Knuckle: return ShotGroups.Punch;
                default: return ShotGroups.None;
            }
        }
    }

    internal static class ShotGroups
    {
        public const int None = 0, Revolver = 1, Shotgun = 2, Railcannon = 3, Rocket = 4, Punch = 5, Count = 6;
    }
}
