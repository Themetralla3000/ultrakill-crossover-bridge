using System;
using System.IO;
using UltrakillBridge.HostSdk;
using UltrakillBridge.Link;

/// <summary>Unity-free checks of host-authoritative health: layouts, the mapping maths and the wire round trip.</summary>
public static unsafe class HealthTests
{
    private static int _failures;
    private static void Expect(bool cond, string name)
    {
        if (!cond) _failures++;
        Console.WriteLine($"{(cond ? "ok  " : "FAIL")} {name}");
    }
    private static bool Near(float a, float b, float eps = 1e-3f) => Math.Abs(a - b) <= eps;

    public static int Run()
    {
        _failures = 0;
        Layout();
        Mapping();
        Heals();
        WireRoundTrip();
        return _failures;
    }

    private static void Layout()
    {
        ErmcHostCombat c = default;
        Expect(sizeof(ErmcHostCombat) == 0x280, "health: ErmcHostCombat still 0x280 bytes");
        Expect((int)((byte*)&c.health - (byte*)&c) == 0x24, "health: health @0x24");
        Expect((int)((byte*)&c.fullHealth - (byte*)&c) == 0x28, "health: fullHealth @0x28");
        Expect((int)((byte*)&c.shield - (byte*)&c) == 0x2C, "health: shield @0x2C");
        Expect((int)((byte*)&c.fullShield - (byte*)&c) == 0x30, "health: fullShield @0x30");
        Expect((int)((byte*)&c.barrier - (byte*)&c) == 0x34, "health: barrier @0x34");
        Expect((int)((byte*)&c.cursePenalty - (byte*)&c) == 0x38, "health: cursePenalty @0x38");
        Expect((int)((byte*)&c.moveSpeedRatio - (byte*)&c) == 0x3C && (int)((byte*)c.reserved1 - (byte*)&c) == 0x58, "health: first ratio @0x3C, reserved1 @0x58 up to damageScale @0x60");
        Expect((int)((byte*)&c.damageScale - (byte*)&c) == 0x60, "health: damageScale unchanged @0x60");
        ErmcGuestRequests r = default;
        Expect(sizeof(ErmcGuestRequests) == 0x40, "health: ErmcGuestRequests is 0x40 bytes");
        Expect((int)((byte*)&r.extFlags - (byte*)&r) == 0x18, "health: extFlags @0x18");
        Expect((int)(r.interactKey - (byte*)&r) == 0x20, "health: interactKey unchanged @0x20");
        Expect((int)((byte*)&r.combatFlags - (byte*)&r) == 0x30, "health: combatFlags @0x30");
        Expect((int)((byte*)&r.healMilli - (byte*)&r) == 0x34, "health: healMilli @0x34");
        Expect((int)((byte*)&r.punchSeq - (byte*)&r) == 0x38, "health: punchSeq @0x38");
        Expect(Protocol.OffHostCombat >= Protocol.OffGuestRequests + sizeof(ErmcGuestRequests), "health: guest requests still end before the combat block");
        Expect(Protocol.HostFlagOwnsHealth == 8 && Protocol.CombatHealthValid == 2 && Protocol.CombatDead == 4, "health: capability and flag bits");
        Expect(Protocol.GuestDashing == 1 && Protocol.GuestHurtFrames == 2 && Protocol.GuestParryWindow == 4 && Protocol.HunterKindParried == 0x100, "health: guest combat bits");
    }

    private static void Mapping()
    {
        Expect(HealthWire.UkHp(110, 110, 0, 0, true) == 100, "map: full health = 100");
        Expect(HealthWire.UkHp(55, 110, 0, 0, true) == 50, "map: half health = 50");
        Expect(HealthWire.UkHp(1, 1000, 0, 0, true) == 1, "map: alive is never below 1");
        Expect(HealthWire.UkHp(0, 110, 0, 0, true) == 1, "map: zero health but alive (lethal pending) shows 1");
        Expect(HealthWire.UkHp(0, 110, 0, 0, false) == 0, "map: dead = 0");
        Expect(HealthWire.UkHp(110, 110, 11, 0, true) == 110, "map: shield above full shows as overheal");
        Expect(HealthWire.UkHp(110, 110, 0, 22, true) == 120, "map: barrier shows as overheal");
        Expect(HealthWire.UkHp(110, 110, 500, 500, true) == 200, "map: overheal capped at 200");
        Expect(HealthWire.UkHp(110, 110, 500, 500, true, 150f) == 150, "map: configurable cap");
        Expect(HealthWire.UkHp(110, 110, 500, 500, true, 50f) == 100, "map: cap never below 100");
        Expect(HealthWire.UkHp(100, 0, 0, 0, true) == 100, "map: unknown full health = 100");
        // A level up that raises max HP keeps the bar where it is only if the fraction is kept: 110/110 -> 143/143.
        Expect(HealthWire.UkHp(143, 143, 0, 0, true) == 100, "map: max-HP growth does not change a full bar");
        // 33 damage of 143 max HP is 23 % of the bar.
        Expect(HealthWire.UkHp(110, 143, 0, 0, true) == 77, "map: damage relative to the grown max HP");
    }

    private static void Heals()
    {
        Expect(Near(HealthWire.HealAmount(10f, 110f, 1f), 11f), "heal: 10 UK HP of 110 = 11 host HP at scale 1");
        Expect(Near(HealthWire.HealAmount(10f, 110f, 0.5f), 5.5f), "heal: scale 0.5 halves it");
        Expect(Near(HealthWire.HealAmount(999f, 110f, 1f), 220f), "heal: a parry's 999 is capped at 200 UK HP");
        Expect(HealthWire.HealAmount(0f, 110f, 1f) == 0f && HealthWire.HealAmount(5f, 0f, 1f) == 0f && HealthWire.HealAmount(5f, 110f, 0f) == 0f, "heal: zero for nonsense input");
        Expect(HealthWire.ToMilli(3f) == 3000u && HealthWire.ToMilli(0.0001f) == 1u && HealthWire.ToMilli(0f) == 0u, "milli: conversion");
        Expect(Near(HealthWire.MilliDelta(5000u, 2000u), 3f), "milli: delta");
        Expect(HealthWire.MilliDelta(2000u, 2000u) == 0f, "milli: no change = 0");
        Expect(Near(HealthWire.MilliDelta(500u, 0xFFFFFC18u), 1.5f), "milli: wraps around zero (1000 + 500 thousandths)");
        Expect(HealthWire.MilliDelta(100u, 5000u) == 0f, "milli: a counter that went backwards (restart) heals nothing");
    }

    private static void WireRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ukbridge-health-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string old = Environment.GetEnvironmentVariable("UKBRIDGE_DIR");
        Environment.SetEnvironmentVariable("UKBRIDGE_DIR", dir);
        try
        {
            using var host = new HostLink();
            using var guest = new GuestLink();
            Expect(host.Open(), "hwire: host open");
            guest.Poll();
            Expect(!guest.ReadHostCombat(out _), "hwire: absent before the host writes it");

            host.WriteHostHealth(80f, 110f, 10f, 20f, 5f, 1.5f, false);
            Expect(guest.ReadHostCombat(out var c), "hwire: block present after WriteHostHealth alone");
            Expect(c.health == 80f && c.fullHealth == 110f && c.shield == 10f && c.fullShield == 20f && c.barrier == 5f && c.cursePenalty == 1.5f, "hwire: health fields");
            Expect((c.flags & Protocol.CombatHealthValid) != 0 && (c.flags & Protocol.CombatDead) == 0 && (c.flags & Protocol.CombatStatsValid) == 0, "hwire: health valid, not dead, stats not claimed");
            uint seq0 = c.seq;
            host.WriteHostHealth(80f, 110f, 10f, 20f, 5f, 1.5f, false);
            guest.ReadHostCombat(out c);
            Expect(c.seq == seq0, "hwire: unchanged write is a no-op");
            host.WriteHostHealth(60f, 110f, 10f, 20f, 5f, 1.5f, true);
            guest.ReadHostCombat(out c);
            Expect(c.seq != seq0 && c.health == 60f && (c.flags & Protocol.CombatDead) != 0, "hwire: change and dead flag published");

            // The stat writer and the health writer keep each other's flags and fields.
            var k = new float[64]; var proc = new float[64];
            host.WriteHostCombat(3, 20f, 5f, 2f, 1f, 1.5f, k, proc);
            guest.ReadHostCombat(out c);
            Expect((c.flags & Protocol.CombatStatsValid) != 0 && (c.flags & Protocol.CombatHealthValid) != 0 && (c.flags & Protocol.CombatDead) != 0 && c.health == 60f && c.level == 3, "hwire: stat write keeps the health bits");
            host.WriteHostHealth(110f, 110f, 0f, 0f, 0f, 1f, false);
            guest.ReadHostCombat(out c);
            Expect((c.flags & Protocol.CombatStatsValid) != 0 && (c.flags & Protocol.CombatDead) == 0 && c.level == 3 && c.health == 110f, "hwire: health write keeps the stats, clears dead");

            host.WriteHostEvents(Protocol.LoadoutGuest, 1, null, Protocol.HostFlagStatDamage | Protocol.HostFlagOwnsHealth);
            Expect(guest.ReadHostEvents(out var ev) && (ev.flags & Protocol.HostFlagOwnsHealth) != 0 && (ev.flags & Protocol.HostFlagStatDamage) != 0, "hwire: capability bit");

            host.ClearHostHealth();
            guest.ReadHostCombat(out c);
            Expect((c.flags & Protocol.CombatHealthValid) == 0 && (c.flags & Protocol.CombatStatsValid) != 0, "hwire: ClearHostHealth drops only the health bits");

            // Guest -> host: i-frame state, heals, punches.
            Expect(!host.ReadGuestRequests(out _), "hwire: guest requests absent");
            guest.SetGuestCombatState(Protocol.GuestDashing | Protocol.GuestParryWindow);
            guest.RequestHeal(3f);
            guest.RequestHeal(2.5f);
            guest.NotePunchStart();
            guest.FlushGuestRequests();
            Expect(host.ReadGuestRequests(out var r), "hwire: guest requests present");
            Expect((r.extFlags & Protocol.GuestCombatValid) != 0 && r.combatFlags == 5u && r.healMilli == 5500u && r.punchSeq == 1u, "hwire: combat flags, heal total and punch counter");
            Expect(Near(HealthWire.MilliDelta(r.healMilli, 0u), 5.5f), "hwire: the host heals by the counter difference");
            uint gseq = r.seq;
            guest.FlushGuestRequests();
            host.ReadGuestRequests(out r);
            Expect(r.seq == gseq, "hwire: unchanged flush is a no-op");
            guest.SetGuestCombatState(0);
            guest.FlushGuestRequests();
            host.ReadGuestRequests(out r);
            Expect(r.combatFlags == 0 && r.healMilli == 5500u && r.seq != gseq, "hwire: flags clear, counters stay");

            // A restarted guest continues the counters instead of going backwards.
            using (var guest2 = new GuestLink())
            {
                guest2.Poll();
                guest2.RequestHeal(1f);
                guest2.NotePunchStart();
                guest2.FlushGuestRequests();
                host.ReadGuestRequests(out r);
                Expect(r.healMilli == 6500u && r.punchSeq == 2u, "hwire: a new guest continues heal and punch counters");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("UKBRIDGE_DIR", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
