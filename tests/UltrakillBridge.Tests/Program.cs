using System;
using System.Runtime.CompilerServices;
using UltrakillBridge.Link;

// Layout checks mirroring the static_asserts at the end of bridge_protocol.h.
int failures = 0;
void Check(string name, int actual, int expected)
{
    bool ok = actual == expected;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {actual:X} (expected {expected:X})");
}
unsafe
{
    Check("ErmcHeader", sizeof(ErmcHeader), 0xC0);
    Check("ErmcGameState", sizeof(ErmcGameState), 0x114);
    Check("ErmcControl", sizeof(ErmcControl), 0x64);
    Check("ErmcHunterEvents", sizeof(ErmcHunterEvents), 0x30);
    Check("ErmcRayHeader", sizeof(ErmcRayHeader), 0x20);
    Check("ErmcRay", sizeof(ErmcRay), 24);
    Check("ErmcRayHit", sizeof(ErmcRayHit), 32);
    Check("ErmcEntity", sizeof(ErmcEntity), 0x80);
    Check("ErmcEntityTableHeader", sizeof(ErmcEntityTableHeader), 0x10);
    Check("ErmcDamage", sizeof(ErmcDamage), 0x20);
    Check("ErmcDamageQueueHeader", sizeof(ErmcDamageQueueHeader), 0x10);
    Check("ErmcTerrainContact", sizeof(ErmcTerrainContact), 32);
    Check("ErmcTerrainContactsHeader", sizeof(ErmcTerrainContactsHeader), 40);
    Check("ErmcCollisionControl", sizeof(ErmcCollisionControl), 48);
    Check("ErmcPassage", sizeof(ErmcPassage), 0x20);
    Check("ErmcPlatformCell", sizeof(ErmcPlatformCell), 32);
    Check("ErmcEnvironment", sizeof(ErmcEnvironment), 24);
    Check("ErmcHostEvents", sizeof(ErmcHostEvents), 0x60);
    Check("ErmcFramesHeader", sizeof(ErmcFramesHeader), 0x18);
    Check("ErmcFrameHeader", sizeof(ErmcFrameHeader), 0x38);
    ErmcHeader h = default;
    Check("hostPrompt offset", (int)((byte*)h.hostPrompt - (byte*)&h), 0x80);
    Check("hostLife offset", (int)((byte*)&h.hostLife - (byte*)&h), 0x58);
    ErmcGameState s = default;
    Check("stageId offset", (int)((byte*)&s.stageId - (byte*)&s), 0x7C);
    Check("supportPos offset", (int)((byte*)s.supportPos - (byte*)&s), 0x108);
    ErmcHostEvents he = default;
    Check("hostEvents seq offset", (int)((byte*)&he.seq - (byte*)&he), 0x08);
    Check("hostEvents loadoutMode offset", (int)((byte*)&he.loadoutMode - (byte*)&he), 0x10);
    Check("hostEvents runSeed offset", (int)((byte*)&he.runSeed - (byte*)&he), 0x18);
    Check("hostEvents counters offset", (int)((byte*)he.counters - (byte*)&he), 0x20);
    Check("OffHostEvents free + in range", Protocol.OffHostEvents >= Protocol.OffCollisionControl + 48 && Protocol.OffHostEvents + sizeof(ErmcHostEvents) <= Protocol.ShmSize ? 1 : 0, 1);
    Check("ErmcGuestRequests", sizeof(ErmcGuestRequests), 0x30);
    ErmcGuestRequests gr = default;
    Check("guestReq seq offset", (int)((byte*)&gr.seq - (byte*)&gr), 0x08);
    Check("guestReq held offset", (int)((byte*)&gr.held - (byte*)&gr), 0x0C);
    Check("guestReq useEquipment offset", (int)((byte*)&gr.useEquipment - (byte*)&gr), 0x10);
    Check("guestReq ping offset", (int)((byte*)&gr.ping - (byte*)&gr), 0x14);
    Check("guestReq interactKey offset", (int)(gr.interactKey - (byte*)&gr), 0x20);
    Check("OffGuestRequests after host events + in range", Protocol.OffGuestRequests >= Protocol.OffHostEvents + sizeof(ErmcHostEvents) && Protocol.OffGuestRequests + sizeof(ErmcGuestRequests) <= Protocol.ShmSize && (Protocol.OffGuestRequests & 15) == 0 ? 1 : 0, 1);
    Check("host flag bits", (int)(Protocol.HostFlagDrawsPrompt | Protocol.HostFlagNeedsInput << 4), 0x21);
    ErmcControl c = default;
    Check("hunterYawDeg offset", (int)((byte*)&c.hunterYawDeg - (byte*)&c), 0x58);
}
// Bridge directory resolution: UKBRIDGE_DIR wins over ERMC_DIR, ERMC_DIR alone still works.
{
    string oldU = Environment.GetEnvironmentVariable("UKBRIDGE_DIR"), oldE = Environment.GetEnvironmentVariable("ERMC_DIR");
    Environment.SetEnvironmentVariable("UKBRIDGE_DIR", null);
    Environment.SetEnvironmentVariable("ERMC_DIR", "ermc-dir");
    bool a = BridgePaths.Dir == "ermc-dir";
    Environment.SetEnvironmentVariable("UKBRIDGE_DIR", "uk-dir");
    bool b = BridgePaths.Dir == "uk-dir";
    Environment.SetEnvironmentVariable("UKBRIDGE_DIR", oldU);
    Environment.SetEnvironmentVariable("ERMC_DIR", oldE);
    if (!a) failures++;
    if (!b) failures++;
    Console.WriteLine($"{(a ? "ok  " : "FAIL")} BridgePaths: ERMC_DIR fallback");
    Console.WriteLine($"{(b ? "ok  " : "FAIL")} BridgePaths: UKBRIDGE_DIR wins");
}
failures += RoundTripTests.Run();
failures += CacheTests.Run();
failures += LoadoutTests.Run();
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
return failures == 0 ? 0 : 1;
