using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UltrakillBridge.Link;

namespace UltrakillBridge.HostSdk
{
    /// <summary>Which of the host character's skill slots a weapon counts as (hosts map it to their own enum).</summary>
    public enum StatSource : byte { None = 0, Primary = 1, Secondary = 2, Utility = 3, Special = 4, Dot = 5 }

    /// <summary>One weapon of the stat damage table. Mutable so hosts can apply their own balance (config).</summary>
    public sealed class WeaponRow
    {
        public int Id;
        public string Name;
        /// <summary>Host damage coefficient per ULTRAKILL damage point, as a multiple of the character's damage stat (2.0 = 200 %).</summary>
        public float K;
        /// <summary>Sustained rate used to derive the proc coefficient: hits per second (shots per second for pellet weapons). 0 = burst / cooldown weapon.</summary>
        public float HitsPerSec;
        /// <summary>Explicit procCoefficient per hit; negative = derive it from <see cref="HitsPerSec"/> and the target procs per second.</summary>
        public float Proc = -1f;
        /// <summary>Pellets per shot for weapons that split one shot's proc budget (Commando-style shotgun); 1 otherwise.</summary>
        public int PelletsPerShot = 1;
        public StatSource Source;
    }

    /// <summary>
    /// Per-weapon damage and proc table of the stat damage model (ErmcDamage STAT entries) and the proc budget maths.
    /// Defaults are the design values of UltraRain docs/COMBAT-DESIGN.md C.2 (level 1, body damage 12). Unity- and game-free.
    /// </summary>
    public sealed class WeaponTable
    {
        /// <summary>Default sustained procs per second per weapon (RoR2 primaries land around 3).</summary>
        public const float DefaultProcTargetPerSec = 3f;
        /// <summary>Default cap of procCoefficient per entry.</summary>
        public const float DefaultProcCap = 1.5f;
        /// <summary>Lower bound of a derived per-shot coefficient.</summary>
        public const float MinDerivedProc = 0.05f;

        private readonly WeaponRow[] _rows = new WeaponRow[Protocol.WeaponSlots];

        public WeaponRow this[int id] => _rows[id & 63];

        public static WeaponTable CreateDefaults()
        {
            var t = new WeaponTable();
            void R(int id, float k, float hps, float proc, int pellets, StatSource src) =>
                t._rows[id] = new WeaponRow { Id = id, Name = WeaponId.NameOf(id), K = k, HitsPerSec = hps, Proc = proc, PelletsPerShot = pellets, Source = src };
            var P = StatSource.Primary; var S = StatSource.Secondary; var U = StatSource.Utility; var X = StatSource.Special;
            // id, k (x body dmg per UK point), sustained hits/s (0 = burst), explicit proc (-1 derive), pellets, source
            R(WeaponId.Unknown, 1.00f, 0, 0.25f, 1, StatSource.None);
            R(WeaponId.RevShot, 2.00f, 2.0f, -1, 1, P);
            R(WeaponId.RevPiercer, 3.00f, 0, 1.0f, 1, S);
            R(WeaponId.RevMarksman, 2.67f, 0, 1.0f, 1, S);
            R(WeaponId.CoinHit, 1.00f, 0, 1.0f, 1, S);
            R(WeaponId.ShoPellet, 0.30f, 1.25f, -1, 12, P);
            R(WeaponId.ShoZone, 0.15f, 1.25f, 0.25f, 1, P);
            R(WeaponId.ShoOvercharge, 1.00f, 0, 1.0f, 1, P);
            R(WeaponId.ShoGrenade, 0.80f, 0, 1.0f, 1, S);
            R(WeaponId.ShoSaw, 1.00f, 0, 0.2f, 1, S);
            R(WeaponId.Hammer, 0.50f, 0.8f, -1, 1, P);
            R(WeaponId.Nail, 1.65f, 12f, -1, 1, P);
            R(WeaponId.NailBurst, 1.65f, 0, 1.0f, 1, S);
            R(WeaponId.Sawblade, 2.00f, 0, 0.5f, 1, P);
            R(WeaponId.Zapper, 1.00f, 0, 0.2f, 1, S);
            R(WeaponId.RailBeam, 3.75f, 0, 1.0f, 1, X);
            R(WeaponId.RailMalicious, 3.12f, 0, 1.0f, 1, X);
            R(WeaponId.RailHarpoon, 2.50f, 0, 0.2f, 1, X);
            R(WeaponId.Rocket, 0.56f, 0, 1.0f, 1, P);
            R(WeaponId.Cannonball, 0.90f, 0, 1.0f, 1, S);
            R(WeaponId.Napalm, 1.00f, 0, 0.1f, 1, S);
            R(WeaponId.Punch, 2.50f, 1.25f, -1, 1, U);
            R(WeaponId.Knuckle, 2.60f, 0.83f, -1, 1, U);
            R(WeaponId.Whip, 2.00f, 0, 0.5f, 1, U);
            R(WeaponId.Slam, 2.00f, 0, 1.0f, 1, U);
            R(WeaponId.Parry, 15.0f, 0, 1.0f, 1, X);
            R(WeaponId.ExplosionOther, 0.80f, 0, 0.5f, 1, StatSource.None);
            R(WeaponId.FireOther, 1.00f, 0, 0.05f, 1, StatSource.Dot);
            for (int i = 0; i < t._rows.Length; i++)
                if (t._rows[i] == null)
                    t._rows[i] = new WeaponRow { Id = i, Name = WeaponId.NameOf(i), K = 1f, Proc = 0.25f, Source = StatSource.None };
            t._rows[WeaponId.Fallback].Name = "FALLBACK";
            return t;
        }

        /// <summary>procCoefficient of ONE hit of the weapon (a pellet's share for shared-budget weapons).</summary>
        public float ProcPerHit(int id, float procTargetPerSec = DefaultProcTargetPerSec, float procCap = DefaultProcCap)
        {
            var r = _rows[id & 63];
            if (r.Proc >= 0f) return r.Proc;
            return ProcBudget.PerHit(r.HitsPerSec, r.PelletsPerShot, procTargetPerSec, procCap);
        }

        /// <summary>Fills the arrays published in the combat block (indexed by weapon id).</summary>
        public void Fill(float[] k, float[] proc, float procTargetPerSec, float procCap)
        {
            for (int i = 0; i < Protocol.WeaponSlots; i++)
            {
                if (k != null && i < k.Length) k[i] = _rows[i].K;
                if (proc != null && i < proc.Length) proc[i] = ProcPerHit(i, procTargetPerSec, procCap);
            }
        }

        /// <summary>One line per weapon for a startup log: coefficient, nominal proc, expected procs per second.</summary>
        public List<string> Describe(float procTargetPerSec, float procCap)
        {
            var lines = new List<string>();
            var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < WeaponId.Count; i++)
            {
                var r = _rows[i];
                float p = ProcPerHit(i, procTargetPerSec, procCap);
                float hps = r.HitsPerSec * r.PelletsPerShot;
                string rate = r.HitsPerSec > 0f
                    ? (ProcBudget.Entry(p, r.PelletsPerShot, procCap) * r.HitsPerSec).ToString("0.00", ci) + " procs/s"
                    : "burst";
                lines.Add(string.Format(ci, "{0,2} {1,-16} k {2,5:0.00} (x body damage per UK point)  proc/hit {3,5:0.000}  src {4,-9} {5}",
                    i, r.Name, r.K, p, r.Source, rate + (hps > 0f ? string.Format(ci, "  ({0:0.##} hits/s)", hps) : "")));
            }
            return lines;
        }
    }

    /// <summary>Proc budget maths (Unity-free). RoR2 convention: ~3 procs/s for sustained primaries, 1.0 for big single hits.</summary>
    public static class ProcBudget
    {
        /// <summary>Per-shot budget of a sustained weapon: clamp(target / shotsPerSec, <see cref="WeaponTable.MinDerivedProc"/>, cap).</summary>
        public static float PerShot(float shotsPerSec, float procTargetPerSec, float procCap)
        {
            if (!(shotsPerSec > 0f)) return Math.Min(1f, procCap);
            float v = procTargetPerSec / shotsPerSec;
            if (v < WeaponTable.MinDerivedProc) v = WeaponTable.MinDerivedProc;
            return v > procCap ? procCap : v;
        }

        /// <summary>Per-hit budget: the shot's budget split over its nominal pellets.</summary>
        public static float PerHit(float shotsPerSec, int pelletsPerShot, float procTargetPerSec, float procCap) =>
            PerShot(shotsPerSec, procTargetPerSec, procCap) / Math.Max(1, pelletsPerShot);

        /// <summary>procCoefficient of an entry of <paramref name="hitCount"/> aggregated hits, capped.</summary>
        public static float Entry(float procPerHit, int hitCount, float procCap)
        {
            float v = procPerHit * Math.Max(1, hitCount);
            return v > procCap ? procCap : v;
        }
    }
}
