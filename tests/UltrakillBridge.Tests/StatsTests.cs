using System;
using System.IO;
using UltrakillBridge.HostSdk;
using UltrakillBridge.Link;

/// <summary>Unity-free checks of the stat ratios: layout, capability bits, the mapping maths and the wire round trip.</summary>
public static unsafe class StatsTests
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
        Ratios();
        Guest();
        Scaled();
        WireRoundTrip();
        return _failures;
    }

    private static void Layout()
    {
        ErmcHostCombat c = default;
        Expect(sizeof(ErmcHostCombat) == 0x280, "stats: ErmcHostCombat still 0x280 bytes");
        Expect((int)((byte*)&c.attackSpeedRatio - (byte*)&c) == 0x18, "stats: attackSpeedRatio @0x18");
        Expect((int)((byte*)&c.moveSpeedRatio - (byte*)&c) == 0x3C, "stats: moveSpeedRatio @0x3C");
        Expect((int)((byte*)&c.extraJumps - (byte*)&c) == 0x40, "stats: extraJumps @0x40");
        Expect((int)((byte*)&c.jumpPowerRatio - (byte*)&c) == 0x44, "stats: jumpPowerRatio @0x44");
        Expect((int)((byte*)&c.sprintSpeedRatio - (byte*)&c) == 0x48, "stats: sprintSpeedRatio @0x48");
        Expect((int)((byte*)&c.rechargeSecondary - (byte*)&c) == 0x4C, "stats: rechargeSecondary @0x4C");
        Expect((int)((byte*)&c.rechargeSpecial - (byte*)&c) == 0x50, "stats: rechargeSpecial @0x50");
        Expect((int)((byte*)&c.rechargeUtility - (byte*)&c) == 0x54, "stats: rechargeUtility @0x54");
        Expect((int)((byte*)c.reserved1 - (byte*)&c) == 0x58, "stats: reserved1 @0x58");
        Expect((int)((byte*)&c.damageScale - (byte*)&c) == 0x60, "stats: damageScale unchanged @0x60");
        Expect(Protocol.HostFlagStats == 16 && Protocol.CombatRatiosValid == 8, "stats: capability and flag bits");
        Expect((Protocol.HostFlagStats & (Protocol.HostFlagDrawsPrompt | Protocol.HostFlagNeedsInput | Protocol.HostFlagStatDamage | Protocol.HostFlagOwnsHealth)) == 0, "stats: capability bit is new");
    }

    private static void Ratios()
    {
        Expect(Near(StatsWire.Ratio(7f, 7f), 1f), "ratio: unchanged stat = 1");
        Expect(Near(StatsWire.Ratio(10.5f, 7f), 1.5f), "ratio: +50 % = 1.5");
        Expect(StatsWire.Ratio(5f, 0f) == 1f && StatsWire.Ratio(float.NaN, 7f) == 1f && StatsWire.Ratio(float.PositiveInfinity, 7f) == 1f, "ratio: nonsense = 1");
        Expect(StatsWire.Ratio(0f, 7f) == 0f && StatsWire.Ratio(-3f, 7f) == 0f, "ratio: rooted = 0, never negative");
        Expect(StatsWire.Ratio(700f, 7f) == StatsWire.MaxRatio, "ratio: capped");
        Expect(StatsWire.ExtraJumps(2, 1) == 1u && StatsWire.ExtraJumps(1, 1) == 0u && StatsWire.ExtraJumps(0, 1) == 0u && StatsWire.ExtraJumps(9999, 1) == 255u, "jumps: maxJumpCount - base, floor 0");
        Expect(Near(StatsWire.Recharge(0.5f), 2f) && Near(StatsWire.Recharge(1f), 1f), "recharge: cooldownScale 0.5 = rate 2");
        Expect(StatsWire.Recharge(0f) == 1f && StatsWire.Recharge(-1f) == 1f && StatsWire.Recharge(float.NaN) == 1f, "recharge: nonsense = 1");
        Expect(Near(StatsWire.Recharge(0.001f), StatsWire.MaxRatio), "recharge: a tiny cooldownScale is capped");
        Expect(StatsWire.SprintRatio(0, 1f, 1.45f) == 1f, "sprint: no Energy Drink = 1");
        Expect(Near(StatsWire.SprintRatio(1, 1f, 1.45f), 1f + 0.25f / 1.45f), "sprint: one Energy Drink");
        Expect(StatsWire.SprintRatio(1, 2f, 1.45f) < StatsWire.SprintRatio(1, 1f, 1.45f), "sprint: the same drink is worth less on a faster body");
    }

    private static void Guest()
    {
        Expect(Near(StatsWire.Gain(1f, 0.6f, 0.5f, 2f), 1f), "gain: ratio 1 = x1 for any gain");
        Expect(Near(StatsWire.Gain(1.5f, 0.6f, 0.5f, 2f), 1.3f), "gain: +50 % at gain 0.6 = +30 %");
        Expect(Near(StatsWire.Gain(10f, 0.6f, 0.5f, 2f), 2f), "gain: capped at max");
        Expect(Near(StatsWire.Gain(0f, 0.6f, 0.5f, 2f), 0.5f), "gain: a root is floored at min");
        Expect(Near(StatsWire.Gain(2f, 0f, 0.5f, 2f), 1f), "gain: gain 0 turns the stat off");
        Expect(Near(StatsWire.Gain(float.NaN, 0.6f, 0.5f, 2f), 1f), "gain: NaN ratio = x1");
        Expect(Near(StatsWire.Gain(3f, 1f, 2f, 0.5f), 2f), "gain: swapped min / max are tolerated");
        Expect(Near(StatsWire.Gain(0.5f, 1f, 1f, 1.5f), 1f), "gain: jump power never below its min of 1");
        Expect(Near(StatsWire.SlideMultiplier(1.5f, 1f, 0.4f, 0.5f, 2f), 1.2f), "slide: move bonus at slide gain");
        Expect(StatsWire.SlideMultiplier(1.5f, 1.2f, 0.4f, 0.5f, 2f) > StatsWire.SlideMultiplier(1.5f, 1f, 0.4f, 0.5f, 2f), "slide: the sprint bonus adds");

        Expect(Near(StatsWire.Approach(1f, 1.3f, 0.1f), 1.1f) && Near(StatsWire.Approach(1f, 1.05f, 0.1f), 1.05f), "approach: steps and snaps");
        Expect(Near(StatsWire.Approach(1.3f, 1f, 0.1f), 1.2f), "approach: downwards");
        Expect(Near(StatsWire.Approach(float.NaN, 1.2f, 0.1f), 1.2f) && Near(StatsWire.Approach(1f, 1.2f, -1f), 1f), "approach: NaN snaps, negative step = hold");

        // Revolver: 200 per second; attack speed x2 adds another 200 per second, so the 0.5 s timer takes 0.25 s.
        Expect(Near(StatsWire.ExtraAdvance(200f, 0.01f, 1.5f), 1f) && StatsWire.ExtraAdvance(200f, 0.01f, 1f) == 0f, "advance: extra progress per step");
        Expect(StatsWire.ExtraAdvance(200f, 0.01f, 0.75f) < 0f, "advance: slower = negative");
        float t = 0, ch = 0;
        while (ch < 100f) { ch += 200f * 0.01f + StatsWire.ExtraAdvance(200f, 0.01f, 2f); t += 0.01f; }
        Expect(Near(t, 0.25f, 0.011f), "advance: attack speed x2 halves the revolver timer (0.5 s -> 0.25 s)");

        Expect(StatsWire.CanAirJump(0, 1, 3) && !StatsWire.CanAirJump(1, 1, 3), "air jumps: one extra jump = one air jump");
        Expect(StatsWire.CanAirJump(2, 5, 3) && !StatsWire.CanAirJump(3, 5, 3), "air jumps: capped by the config");
        Expect(!StatsWire.CanAirJump(0, 0, 3) && !StatsWire.CanAirJump(0, 5, 0), "air jumps: none granted, none allowed");
    }

    private static void Scaled()
    {
        var v = new ScaledValue();
        Expect(v.Apply(16f, 1f) == 16f && !v.Modified, "scaled: x1 with nothing written is a no-op");
        Expect(Near(v.Apply(16f, 1.25f), 20f) && v.Modified, "scaled: base * multiplier");
        Expect(Near(v.Apply(20f, 1.5f), 24f) && Near(v.Base, 16f), "scaled: the base stays the original value when it sees its own write");
        Expect(Near(v.Apply(24f, 1f), 16f) && !v.Modified, "scaled: back to x1 restores the original value exactly");
        v.Apply(16f, 2f);
        Expect(Near(v.Apply(10f, 2f), 20f) && Near(v.Base, 10f), "scaled: a value somebody else changed becomes the new base");
        Expect(Near(v.Apply(20f, float.NaN), 10f) && Near(v.Apply(10f, -3f), 10f), "scaled: nonsense multiplier = x1");
        v.Forget();
        Expect(Near(v.Apply(7f, 2f), 14f) && Near(v.Base, 7f), "scaled: Forget starts over");
    }

    private static void WireRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ukbridge-stats-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string old = Environment.GetEnvironmentVariable("UKBRIDGE_DIR");
        Environment.SetEnvironmentVariable("UKBRIDGE_DIR", dir);
        try
        {
            using var host = new HostLink();
            using var guest = new GuestLink();
            Expect(host.Open(), "swire: host open");
            guest.Poll();
            host.WriteHostRatios(1.4f, 1.5f, 2, 1.1f, 1.17f, 2f, 1.5f, 1.25f);
            Expect(guest.ReadHostCombat(out var c), "swire: block present after WriteHostRatios alone");
            Expect((c.flags & Protocol.CombatRatiosValid) != 0 && (c.flags & Protocol.CombatStatsValid) == 0 && (c.flags & Protocol.CombatHealthValid) == 0, "swire: ratios valid, nothing else claimed");
            Expect(c.attackSpeedRatio == 1.4f && c.moveSpeedRatio == 1.5f && c.extraJumps == 2 && c.jumpPowerRatio == 1.1f && c.sprintSpeedRatio == 1.17f
                   && c.rechargeSecondary == 2f && c.rechargeSpecial == 1.5f && c.rechargeUtility == 1.25f, "swire: every field arrives");
            uint seq0 = c.seq;
            host.WriteHostRatios(1.4f, 1.5f, 2, 1.1f, 1.17f, 2f, 1.5f, 1.25f);
            guest.ReadHostCombat(out c);
            Expect(c.seq == seq0, "swire: unchanged write is a no-op");
            host.WriteHostRatios(float.NaN, 99f, 0, -1f, 1f, 1f, 1f, 1f);
            guest.ReadHostCombat(out c);
            Expect(c.attackSpeedRatio == 1f && c.moveSpeedRatio == StatsWire.MaxRatio && c.jumpPowerRatio == 0f && c.extraJumps == 0, "swire: NaN -> 1, clamped to [0, max]");

            // The three writers keep each other's flags.
            host.WriteHostHealth(50f, 100f, 0f, 0f, 0f, 1f, false);
            host.WriteHostCombat(2, 15f, 5f, 2f, 1f, 1.5f, new float[64], new float[64]);
            guest.ReadHostCombat(out c);
            uint all = Protocol.CombatRatiosValid | Protocol.CombatHealthValid | Protocol.CombatStatsValid;
            Expect((c.flags & all) == all && c.moveSpeedRatio == StatsWire.MaxRatio && c.health == 50f && c.level == 2, "swire: ratios, health and stats coexist");
            host.ClearHostRatios();
            guest.ReadHostCombat(out c);
            Expect((c.flags & Protocol.CombatRatiosValid) == 0 && (c.flags & Protocol.CombatHealthValid) != 0 && (c.flags & Protocol.CombatStatsValid) != 0, "swire: ClearHostRatios drops only the ratio bit");

            host.WriteHostEvents(Protocol.LoadoutGuest, 1, null, Protocol.HostFlagStats);
            Expect(guest.ReadHostEvents(out var ev) && (ev.flags & Protocol.HostFlagStats) != 0 && (ev.flags & Protocol.HostFlagStatDamage) == 0, "swire: capability bit");
        }
        finally
        {
            Environment.SetEnvironmentVariable("UKBRIDGE_DIR", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
