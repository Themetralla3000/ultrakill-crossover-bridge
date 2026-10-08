using System;
using System.Collections.Generic;

namespace UltrakillBridge.Loadout
{
    /// <summary>One unlockable item: a weapon variant (main or alt prefab of a GunSetter pref name) or an arm.</summary>
    public sealed class LoadoutItem
    {
        public string Id;        // "rev0", "sho1alt", "arm2"
        public string Display;   // "Shotgun (Pump Charge)"
        public string Family;    // "rev" | "sho" | "nai" | "rai" | "rock" | "arm"
        public int Variant;      // the digit of the pref name
        public bool Alt;         // second prefab of the variant (only rev/sho/nai have one)
    }

    /// <summary>
    /// Unity-free list of everything the guest can unlock, keyed by ULTRAKILL's own pref names
    /// (GunSetter.CheckWeapon: weapon.rev0..2, sho0..2, nai0..2, rai0..2, rock0..2, FistControl: arm0..2).
    /// Unlock granularity is one variant (and one alt variant), not one weapon family.
    /// </summary>
    public static class LoadoutCatalog
    {
        public static readonly IReadOnlyList<LoadoutItem> All = Build();

        private static List<LoadoutItem> Build()
        {
            var l = new List<LoadoutItem>();
            Fam(l, "rev", "Revolver", new[] { "Piercer", "Sharpshooter", "Marksman" }, true);
            Fam(l, "sho", "Shotgun", new[] { "Core Eject", "Pump Charge", "Sawed-On" }, true);
            Fam(l, "nai", "Nailgun", new[] { "Attractor", "Overheat", "Sawblade Launcher" }, true);
            Fam(l, "rai", "Railcannon", new[] { "Electric", "Screwdriver", "Malicious" }, false);
            Fam(l, "rock", "Rocket Launcher", new[] { "Freezeframe", "S.R.S. Cannon", "Napalm" }, false);
            string[] arms = { "Feedbacker", "Knuckleblaster", "Whiplash" };
            for (int i = 0; i < 3; i++)
                l.Add(new LoadoutItem { Id = "arm" + i, Display = "Arm (" + arms[i] + ")", Family = "arm", Variant = i });
            return l;
        }

        private static void Fam(List<LoadoutItem> l, string fam, string name, string[] variants, bool alt)
        {
            for (int i = 0; i < variants.Length; i++)
            {
                l.Add(new LoadoutItem { Id = fam + i, Display = name + " (" + variants[i] + ")", Family = fam, Variant = i });
                if (alt)
                    l.Add(new LoadoutItem { Id = fam + i + "alt", Display = "Alt " + name + " (" + variants[i] + ")", Family = fam, Variant = i, Alt = true });
            }
        }

        public static LoadoutItem Find(string id)
        {
            foreach (var it in All)
                if (string.Equals(it.Id, id, StringComparison.OrdinalIgnoreCase)) return it;
            return null;
        }

        /// <summary>Parses "a, b;c" into catalog ids ("all" or "*" = everything). Unknown tokens go to <paramref name="unknown"/>.</summary>
        public static List<string> ParseList(string text, List<string> unknown = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var raw in text.Split(',', ';', ' '))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.Equals("all", StringComparison.OrdinalIgnoreCase) || t == "*")
                {
                    foreach (var it in All) if (!result.Contains(it.Id)) result.Add(it.Id);
                    continue;
                }
                var item = Find(t);
                if (item == null) { unknown?.Add(t); continue; }
                if (!result.Contains(item.Id)) result.Add(item.Id);
            }
            return result;
        }
    }

    /// <summary>Deterministic unlock order for a run; same inputs always give the same list on every platform.</summary>
    public static class ProgressionOrder
    {
        /// <summary>
        /// Start items first (in the given order), then the rest of the pool shuffled with a seeded
        /// SplitMix64 Fisher-Yates over the ordinally sorted pool (so config ordering does not change the result).
        /// </summary>
        public static List<string> Build(IList<string> start, IList<string> pool, ulong seed)
        {
            var order = new List<string>();
            foreach (var s in start) if (!order.Contains(s)) order.Add(s);
            var rest = new List<string>();
            foreach (var p in pool) if (!order.Contains(p) && !rest.Contains(p)) rest.Add(p);
            rest.Sort(StringComparer.Ordinal);
            ulong state = seed ^ 0x9E3779B97F4A7C15UL;
            for (int i = rest.Count - 1; i > 0; i--)
            {
                int j = (int)(Next(ref state) % (ulong)(i + 1));
                string tmp = rest[i]; rest[i] = rest[j]; rest[j] = tmp;
            }
            order.AddRange(rest);
            return order;
        }

        /// <summary>Items unlocked after <paramref name="bosses"/> defeats: start count + bosses * perBoss, clamped to [startCount, total].</summary>
        public static int UnlockedCount(int startCount, uint bosses, int perBoss, int total)
        {
            long n = (long)startCount + (long)bosses * Math.Max(1, perBoss);
            if (n > total) n = total;
            if (n < Math.Min(startCount, total)) n = Math.Min(startCount, total);
            return (int)n;
        }

        private static ulong Next(ref ulong x)
        {
            ulong z = (x += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
