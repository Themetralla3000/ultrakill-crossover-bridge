using System;
using System.IO;
using System.Linq;
using UltrakillBridge.Guest.Combat;
using UltrakillBridge.HostSdk;
using UltrakillBridge.Link;

/// <summary>Unity-free checks of the stat damage extension: wire layout and round trip, proc budget, shot grouping, classifier.</summary>
public static unsafe class StatTests
{
    private static int _failures;
    private static void Expect(bool cond, string name)
    {
        if (!cond) _failures++;
        Console.WriteLine($"{(cond ? "ok  " : "FAIL")} {name}");
    }
    private static bool Near(float a, float b, float eps = 1e-4f) => Math.Abs(a - b) <= eps;

    public static int Run()
    {
        _failures = 0;
        Layout();
        Packing();
        Budget();
        Grouping();
        Classifier();
        WireRoundTrip();
        return _failures;
    }

    private static void Layout()
    {
        Expect(sizeof(ErmcHostCombat) == 0x280, "combat: ErmcHostCombat is 0x280 bytes");
        Expect(sizeof(ErmcWeaponCoeff) == 8, "combat: ErmcWeaponCoeff is 8 bytes");
        ErmcHostCombat c = default;
        Expect((int)((byte*)&c.damage - (byte*)&c) == 0x14, "combat: damage @0x14");
        Expect((int)((byte*)&c.critPercent - (byte*)&c) == 0x1C, "combat: critPercent @0x1C");
        Expect((int)((byte*)&c.critMultiplier - (byte*)&c) == 0x20, "combat: critMultiplier @0x20");
        Expect((int)((byte*)&c.damageScale - (byte*)&c) == 0x60, "combat: damageScale @0x60");
        Expect((int)((byte*)&c.headshotMultiplier - (byte*)&c) == 0x64, "combat: headshotMultiplier @0x64");
        Expect((int)((byte*)c.weapons - (byte*)&c) == 0x80, "combat: weapon table @0x80");
        Expect(Protocol.OffHostCombat >= Protocol.OffGuestRequests + sizeof(ErmcGuestRequests) && Protocol.OffHostCombat + sizeof(ErmcHostCombat) <= Protocol.ShmSize && (Protocol.OffHostCombat & 15) == 0, "combat: block in range after guest requests");
        Expect(Protocol.DamageStat == 0x10 && Protocol.DamageWeakpoint == 0x20 && Protocol.DamageExplosion == 0x40 && Protocol.DamageFraction == 0x80, "damage flag bits 4..7");
        Expect(Protocol.HostFlagStatDamage == 4, "host flag StatDamage is bit 2");
        Expect(WeaponId.Fallback == 63 && WeaponId.Count == 28, "weapon id range");
    }

    private static void Packing()
    {
        uint r = StatWire.Pack(WeaponId.ShoPellet, 12, 200, HitKind.Direct);
        Expect(r == (5u | 12u << 8 | 200u << 16), "pack: bit layout");
        Expect(StatWire.WeaponOf(r) == 5 && StatWire.HitCountOf(r) == 12 && StatWire.ShotSeqOf(r) == 200 && StatWire.HitKindOf(r) == 0, "pack: round trip");
        uint q = StatWire.Pack(63, 999, 300, HitKind.CoinChain);
        Expect(StatWire.WeaponOf(q) == 63 && StatWire.HitCountOf(q) == 255 && StatWire.ShotSeqOf(q) == (300 & 255) && StatWire.HitKindOf(q) == 4, "pack: hit count clamps to 255, seq wraps");
        Expect(StatWire.HitCountOf(StatWire.Pack(1, 0, 0, 0)) == 1 && StatWire.HitCountOf(0) == 1, "pack: hit count at least 1");
        Expect((StatWire.Pack(5, 1, 1, 1) & 0xF00000C0u) == 0, "pack: reserved bits stay 0");
        Expect(StatWire.Flags(true, true) == (Protocol.DamageStat | Protocol.DamageWeakpoint | Protocol.DamageExplosion), "flags: weakpoint + explosion");
        Expect(StatWire.Flags(false, false, true) == (Protocol.DamageStat | Protocol.DamageFraction), "flags: fraction");
        Expect(Near(StatWire.HostDamage(12f, 1f, 2f, 1f, false, 1.5f), 24f), "damage: revolver shot at level 1 = 24");
        Expect(Near(StatWire.HostDamage(12f, 1f, 2f, 1f, true, 1.5f), 36f), "damage: head x1.5");
        Expect(Near(StatWire.HostDamage(26.4f, 1f, 2f, 1f, false, 1.5f), 52.8f), "damage: scales with level (damage stat 26.4)");
        Expect(Near(StatWire.HostDamage(12f, 0.5f, 2f, 1f, false, 1.5f), 12f), "damage: global scale");
        for (int w = 0; w < WeaponId.Count; w++)
            if (WeaponId.FromName(WeaponId.NameOf(w)) != w) { Expect(false, "weapon names round trip " + w); return; }
        Expect(WeaponId.FromName("rev_shot") == 1 && WeaponId.FromName("nope") == -1 && WeaponId.FromName("FALLBACK") == 63, "weapon names: lookup");
    }

    private static void Budget()
    {
        var t = WeaponTable.CreateDefaults();
        float P = WeaponTable.DefaultProcTargetPerSec, cap = WeaponTable.DefaultProcCap;
        Expect(Near(t.ProcPerHit(WeaponId.RevShot, P, cap), 1.5f), "proc: revolver 1.5 per hit");
        Expect(Near(t.ProcPerHit(WeaponId.Nail, P, cap), 0.25f), "proc: nail 0.25 per hit");
        Expect(Near(t.ProcPerHit(WeaponId.ShoPellet, P, cap), 0.125f), "proc: pellet 0.125 (shot budget 1.5 / 12)");
        Expect(Near(ProcBudget.Entry(0.125f, 12, cap), 1.5f), "proc: 12 pellets = the shot's 1.5");
        Expect(Near(ProcBudget.Entry(0.125f, 24, cap), 1.5f), "proc: 24 pellets (Pump Charge max) capped at 1.5");
        Expect(Near(ProcBudget.Entry(0.125f, 5, cap), 0.625f), "proc: 5 pellets landed = 5/12 of the budget");
        Expect(Near(t.ProcPerHit(WeaponId.Punch, P, cap), 1.5f) && Near(t.ProcPerHit(WeaponId.Knuckle, P, cap), 1.5f), "proc: melee capped at 1.5");
        Expect(Near(t.ProcPerHit(WeaponId.Hammer, P, cap), 1.5f), "proc: hammer 1.5");
        Expect(Near(t.ProcPerHit(WeaponId.RevPiercer, P, cap), 1.0f) && Near(t.ProcPerHit(WeaponId.RailBeam, P, cap), 1.0f) && Near(t.ProcPerHit(WeaponId.Rocket, P, cap), 1.0f), "proc: cooldown / charged shots 1.0");
        Expect(Near(t.ProcPerHit(WeaponId.ShoZone, P, cap), 0.25f), "proc: explicit row value wins");
        // sustained rates land around 3 procs/s
        foreach (int w in new[] { WeaponId.RevShot, WeaponId.Nail })
        {
            var r = t[w];
            float rate = ProcBudget.Entry(t.ProcPerHit(w, P, cap), r.PelletsPerShot, cap) * r.HitsPerSec * (w == WeaponId.Nail ? 1f : 1f);
            Expect(Near(rate, 3f), $"proc: {r.Name} sustained {rate:0.00} procs/s = 3");
        }
        Expect(Near(ProcBudget.PerShot(1.25f, P, cap) * 1.25f, 1.875f), "proc: shotgun 1.25 shots/s gives 1.875 procs/s (capped shot budget)");
        Expect(Near(ProcBudget.PerShot(100f, P, cap), WeaponTable.MinDerivedProc), "proc: derived value has a floor");
        Expect(Near(ProcBudget.PerShot(0f, P, cap), 1f), "proc: burst weapon without rate = 1.0");
        Expect(Near(t.ProcPerHit(WeaponId.RevShot, 6f, cap), 1.5f) && Near(t.ProcPerHit(WeaponId.RevShot, 1f, cap), 0.5f), "proc: target per second regenerates derived rows");
        t[WeaponId.RevShot].Proc = 0.4f;
        Expect(Near(t.ProcPerHit(WeaponId.RevShot, P, cap), 0.4f), "proc: override applies");
        var k = new float[64]; var proc = new float[64];
        t.Fill(k, proc, P, cap);
        Expect(Near(k[WeaponId.Nail], 1.65f) && Near(proc[WeaponId.ShoPellet], 0.125f) && Near(k[63], 1f), "table: Fill publishes k and proc by id");
        Expect(t.Describe(P, cap).Count == WeaponId.Count, "table: one description line per weapon");
        // default table sanity: every named weapon has a positive k and proc
        bool all = true;
        for (int i = 0; i < WeaponId.Count; i++) all &= t[i].K > 0f && t.ProcPerHit(i, P, cap) > 0f;
        Expect(all, "table: every weapon has positive k and proc");
    }

    private static void Grouping()
    {
        // guest side: one blast = one entry
        var a = new StatAggregator();
        for (int i = 0; i < 12; i++) a.Add(WeaponId.ShoPellet, false, 7, HitKind.Direct, 0.75f);
        Expect(a.Count == 1 && a[0].Count == 12 && Near(a[0].Uk, 9f), "group: 12 pellets of one shot = 1 entry, hit count 12, uk sum 9");
        a.Add(WeaponId.ShoPellet, true, 7, HitKind.Direct, 0.75f);
        a.Add(WeaponId.ShoPellet, false, 8, HitKind.Direct, 0.75f);
        a.Add(WeaponId.ShoZone, false, 7, HitKind.Direct, 4f);
        Expect(a.Count == 4, "group: head, next shot and zone are separate entries");
        a.Add(WeaponId.RevShot, false, 1, HitKind.Direct, float.NaN);
        a.Add(WeaponId.RevShot, false, 1, HitKind.Direct, 0f);
        Expect(a.Count == 4, "group: zero / NaN damage ignored");
        a.RemoveFirst(2);
        Expect(a.Count == 2 && a[0].Weapon == WeaponId.ShoPellet && a[0].ShotSeq == 8, "group: RemoveFirst keeps the unsent tail");
        a.RemoveFirst(99);
        Expect(a.Count == 0, "group: RemoveFirst clears");
        for (int i = 0; i < 300; i++) a.Add(WeaponId.Nail, false, i, HitKind.Direct, 0.2f);
        Expect(a.Count == StatAggregator.MaxEntries, "group: bounded");
        for (int i = 0; i < 300; i++) a.Add(WeaponId.RevShot, false, 5, HitKind.Direct, 1f);
        Expect(a[a.Count - 1].Count == 255, "group: hit count saturates at 255");

        // host side: one crit roll per (weapon, shot)
        var cache = new ShotRollCache();
        int rolls = 0;
        bool Roll() { rolls++; return rolls % 2 == 1; }
        bool first = cache.Get(WeaponId.ShoPellet, 3, 1000, Roll);
        bool again = cache.Get(WeaponId.ShoPellet, 3, 1100, Roll);
        Expect(rolls == 1 && first == again, "crit: second entry of the same shot reuses the roll");
        cache.Get(WeaponId.ShoPellet, 4, 1100, Roll);
        cache.Get(WeaponId.ShoZone, 3, 1100, Roll);
        Expect(rolls == 3, "crit: other shot and other weapon roll separately");
        cache.Get(WeaponId.ShoPellet, 3, 1000 + ShotRollCache.Ttl + 1, Roll);
        Expect(rolls == 4, "crit: the cache entry expires (seq wrap protection)");
        cache.Get(WeaponId.ShoPellet, 3, 500, Roll);
        Expect(rolls == 5, "crit: clock going backwards re-rolls");
    }

    private static void Classifier()
    {
        int Cl(string hitter, string last = "", string src = "", int v = -1, bool boom = false, bool expl = false) =>
            WeaponClassifier.Classify(hitter, last, src, v, boom, expl, out _);
        Expect(Cl("revolver", "revolver0", "Revolver", 0) == WeaponId.RevShot, "classify: revolver shot");
        Expect(Cl("revolver", "revolver0", "Revolver", 0, boom: true) == WeaponId.RevPiercer, "classify: charged beam = piercer");
        Expect(Cl("revolver", "revolver2", "Revolver", 2, boom: true) == WeaponId.RevMarksman, "classify: marksman charged");
        int kind;
        Expect(WeaponClassifier.Classify("coin", "coin", "", -1, false, false, out kind) == WeaponId.CoinHit && kind == HitKind.CoinChain, "classify: coin chain");
        Expect(Cl("shotgun", "shotgun0", "Shotgun", 0) == WeaponId.ShoPellet, "classify: pellets (bulletType starting with shotgun)");
        Expect(Cl("shotgunzone", "shotgun0", "Shotgun", 0) == WeaponId.ShoZone, "classify: point blank zone");
        Expect(Cl("explosion", "shotgun1", "Shotgun", 1, expl: true) == WeaponId.ShoOvercharge, "classify: pump charge explosion");
        Expect(Cl("explosion", "shotgun0", "Shotgun", 0, expl: true) == WeaponId.ShoGrenade, "classify: core eject grenade");
        Expect(Cl("explosion", "rocket", "RocketLauncher", 0, expl: true) == WeaponId.Rocket, "classify: rocket");
        Expect(Cl("explosion", "shotgun1", "RocketLauncher", 0, expl: true) == WeaponId.Rocket, "classify: stale hitterWeapons does not beat the source weapon");
        Expect(Cl("explosion") == WeaponId.ExplosionOther, "classify: unknown explosion");
        Expect(Cl("hammer", "hammer0", "ShotgunHammer", 0) == WeaponId.Hammer && Cl("hammerzone") == WeaponId.Hammer, "classify: hammer");
        Expect(Cl("nail", "nailgun0", "Nailgun", 0) == WeaponId.Nail && Cl("nail", "", "Nailgun", 0, boom: true) == WeaponId.NailBurst, "classify: nail and nail burst");
        Expect(Cl("sawblade") == WeaponId.Sawblade && Cl("zapper") == WeaponId.Zapper && Cl("zap") == WeaponId.Zapper, "classify: sawblade / zapper");
        Expect(Cl("railcannon", "railcannon0", "Railcannon", 0) == WeaponId.RailBeam && Cl("railcannon", "railcannon2", "Railcannon", 2) == WeaponId.RailMalicious, "classify: railcannon variants");
        Expect(Cl("harpoon") == WeaponId.RailHarpoon && Cl("drill") == WeaponId.RailHarpoon && Cl("drillpunch") == WeaponId.RailHarpoon, "classify: screwdriver");
        Expect(Cl("cannonball") == WeaponId.Cannonball && Cl("fire", "rocket2", "RocketLauncher", 2) == WeaponId.Napalm && Cl("fire") == WeaponId.FireOther, "classify: cannonball / napalm / other fire");
        Expect(Cl("punch") == WeaponId.Punch && Cl("heavypunch") == WeaponId.Knuckle && Cl("hook") == WeaponId.Whip && Cl("ground slam") == WeaponId.Slam, "classify: melee");
        Expect(Cl("chainsawprojectile") == WeaponId.ShoSaw && Cl("chainsawzone") == WeaponId.ShoSaw, "classify: saw");
        Expect(Cl("who knows") == WeaponId.Fallback && Cl(null) == WeaponId.Fallback && Cl("who knows", expl: true) == WeaponId.ExplosionOther, "classify: unknown falls back");
        Expect(WeaponClassifier.ShotGroup(WeaponId.ShoPellet) == WeaponClassifier.ShotGroup(WeaponId.ShoZone) && WeaponClassifier.ShotGroup(WeaponId.Nail) == ShotGroups.None, "shot group: shotgun parts share a counter, nails use the tick");
    }

    private static void WireRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ukbridge-stat-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string old = Environment.GetEnvironmentVariable("UKBRIDGE_DIR");
        Environment.SetEnvironmentVariable("UKBRIDGE_DIR", dir);
        try
        {
            using var host = new HostLink();
            using var guest = new GuestLink();
            Expect(host.Open(), "wire: host open");
            guest.Poll();
            Expect(!guest.ReadHostCombat(out _), "wire: combat block absent before the host writes it");

            var t = WeaponTable.CreateDefaults();
            var k = new float[64]; var proc = new float[64];
            t.Fill(k, proc, WeaponTable.DefaultProcTargetPerSec, WeaponTable.DefaultProcCap);
            host.WriteHostCombat(5, 26.4f, 12.5f, 2f, 1f, 1.5f, k, proc);
            Expect(guest.ReadHostCombat(out var c), "wire: combat block present");
            Expect(c.level == 5 && c.damage == 26.4f && c.critPercent == 12.5f && c.critMultiplier == 2f && c.damageScale == 1f && c.headshotMultiplier == 1.5f, "wire: combat stats");
            Expect(c.weapons[WeaponId.RevShot * 2] == 2.0f && c.weapons[WeaponId.ShoPellet * 2 + 1] == 0.125f && c.weapons[63 * 2] == 1f, "wire: weapon table by id");
            Expect((c.flags & Protocol.CombatStatsValid) != 0 && (c.seq & 1) == 0 && c.seq != 0, "wire: stats valid, seq even");
            uint seq0 = c.seq;
            host.WriteHostCombat(5, 26.4f, 12.5f, 2f, 1f, 1.5f, k, proc);
            guest.ReadHostCombat(out c);
            Expect(c.seq == seq0, "wire: unchanged write is a no-op");
            host.WriteHostCombat(6, 28.8f, 12.5f, 2f, 1f, 1.5f, k, proc);
            guest.ReadHostCombat(out c);
            Expect(c.seq != seq0 && c.level == 6, "wire: change bumps seq");
            k[WeaponId.RevShot] = 3f;
            host.WriteHostCombat(6, 28.8f, 12.5f, 2f, 1f, 1.5f, k, proc);
            guest.ReadHostCombat(out c);
            Expect(c.weapons[WeaponId.RevShot * 2] == 3f, "wire: a table change alone is published");

            host.WriteHostEvents(Protocol.LoadoutGuest, 1, null, Protocol.HostFlagStatDamage);
            Expect(guest.ReadHostEvents(out var ev) && (ev.flags & Protocol.HostFlagStatDamage) != 0, "wire: capability bit in ErmcHostEvents.flags");

            uint flags = StatWire.Flags(true, false);
            uint reserved = StatWire.Pack(WeaponId.ShoPellet, 9, 42, HitKind.Direct);
            Expect(guest.PushDamage(0x77, 6.75f, 1, 2, 3, flags, reserved), "wire: stat entry pushed");
            Expect(guest.PushDamage(0x78, 2.5f, 1, 2, 3, 0), "wire: legacy entry pushed");
            var buf = new ErmcDamage[4];
            int n = host.DrainDamage(buf);
            Expect(n == 2 && buf[0].id == 0x77 && buf[0].amount == 6.75f && buf[0].flags == flags && buf[0].reserved == reserved, "wire: stat entry arrives intact");
            Expect(StatWire.WeaponOf(buf[0].reserved) == WeaponId.ShoPellet && StatWire.HitCountOf(buf[0].reserved) == 9 && StatWire.ShotSeqOf(buf[0].reserved) == 42, "wire: stat entry fields decode");
            Expect(buf[1].flags == 0 && buf[1].reserved == 0, "wire: legacy entry has flags 0 and reserved 0 (Elden Ring unchanged)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("UKBRIDGE_DIR", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
