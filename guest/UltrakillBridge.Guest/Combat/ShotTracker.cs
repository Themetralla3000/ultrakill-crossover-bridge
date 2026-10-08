using System;
using HarmonyLib;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>
    /// Shot sequence numbers: one counter per weapon family, bumped by a prefix on the family's fire method, so every hit
    /// that comes from the same trigger pull (all pellets, the point blank zone, a ricocheting beam) carries the same
    /// value and the host can roll crit once per shot. Families without a counter (nails, damage over time, hammer...)
    /// use the guest tick instead, so a burst of nails in one tick is one group.
    /// Each patch is installed on its own and only logs when the method does not exist (game update).
    /// </summary>
    internal static class ShotTracker
    {
        private static readonly int[] Counters = new int[ShotGroups.Count];
        private static int _tick;

        public static void NextTick() => _tick++;

        public static int Current(int weaponId)
        {
            int g = WeaponClassifier.ShotGroup(weaponId);
            return g == ShotGroups.None ? _tick & 255 : Counters[g] & 255;
        }

        private static void Bump(int group) => Counters[group]++;

        // Prefixes (static methods looked up by name): called with no arguments, so signatures stay independent of the game's.
        private static void RevolverShoot() => Bump(ShotGroups.Revolver);
        private static void ShotgunShoot() => Bump(ShotGroups.Shotgun);
        private static void RailcannonShoot() => Bump(ShotGroups.Railcannon);
        private static void RocketShoot() => Bump(ShotGroups.Rocket);
        private static void PunchStart() => Bump(ShotGroups.Punch);

        public static void Install(Harmony harmony)
        {
            Hook(harmony, typeof(Revolver), "Shoot", nameof(RevolverShoot));
            Hook(harmony, typeof(Shotgun), "Shoot", nameof(ShotgunShoot));
            Hook(harmony, typeof(Railcannon), "Shoot", nameof(RailcannonShoot));
            Hook(harmony, typeof(RocketLauncher), "Shoot", nameof(RocketShoot));
            Hook(harmony, typeof(Punch), "PunchStart", nameof(PunchStart));
        }

        private static void Hook(Harmony harmony, Type type, string method, string prefix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                var pre = AccessTools.Method(typeof(ShotTracker), prefix);
                if (target == null || pre == null) { Plugin.Log.LogWarning($"Shot tracker: {type.Name}.{method} not found (crit grouping for that weapon falls back to per tick)."); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(pre));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Shot tracker: could not patch {type.Name}.{method}: {e.Message}");
            }
        }
    }
}
