using System;
using System.Collections.Generic;
using System.Linq;
using UltrakillBridge.Loadout;

/// <summary>Unity-free checks of the progression order and the item catalogue.</summary>
public static class LoadoutTests
{
    private static int _failures;
    private static void Expect(bool cond, string name)
    {
        if (!cond) _failures++;
        Console.WriteLine($"{(cond ? "ok  " : "FAIL")} {name}");
    }

    public static int Run()
    {
        _failures = 0;
        var all = LoadoutCatalog.ParseList("all");
        var start = new[] { "rev0" };
        Expect(all.Count == 27 && all.Distinct().Count() == 27, "catalog: 27 unique items");
        Expect(LoadoutCatalog.ParseList("rev0, sho1alt;arm2 bogus", null).SequenceEqual(new[] { "rev0", "sho1alt", "arm2" }), "catalog: list parsing");

        var a = ProgressionOrder.Build(start, all, 42);
        var b = ProgressionOrder.Build(start, all, 42);
        var c = ProgressionOrder.Build(start, all.AsEnumerable().Reverse().ToList(), 42);
        var d = ProgressionOrder.Build(start, all, 43);
        Expect(a.SequenceEqual(b), "order: same seed is reproducible");
        Expect(a.SequenceEqual(c), "order: independent of pool ordering");
        Expect(!a.SequenceEqual(d), "order: another seed gives another order");
        Expect(a[0] == "rev0" && a.Count == 27 && a.Distinct().Count() == 27, "order: start first, every item exactly once");
        Expect(ProgressionOrder.Build(new string[0], new[] { "x" }, 0).SequenceEqual(new[] { "x" }), "order: empty start");

        Expect(ProgressionOrder.UnlockedCount(1, 0, 1, 27) == 1, "count: start only");
        Expect(ProgressionOrder.UnlockedCount(1, 5, 1, 27) == 6, "count: start + bosses");
        Expect(ProgressionOrder.UnlockedCount(1, 100, 1, 27) == 27, "count: clamped to total");
        Expect(ProgressionOrder.UnlockedCount(1, 3, 2, 27) == 7, "count: unlocks per boss");
        Expect(ProgressionOrder.UnlockedCount(2, 0, 1, 1) == 1, "count: start larger than pool");
        var p2 = a.Take(ProgressionOrder.UnlockedCount(1, 2, 1, a.Count)).ToList();
        var p5 = a.Take(ProgressionOrder.UnlockedCount(1, 5, 1, a.Count)).ToList();
        Expect(p5.Take(p2.Count).SequenceEqual(p2), "order: unlocks are cumulative");
        return _failures;
    }
}
