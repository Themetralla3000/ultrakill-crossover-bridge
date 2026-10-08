# Third-party notices

## Minecraft Ring and minecraft-crossover-bridge (MIT)

The protocol and the host-side design of this kit are the work of [Minecraft Ring](https://github.com/siddoff/Minecraft-Ring)
(Copyright (c) 2026 siddoff, Copyright (c) 2026 justbustin), a Windows adaptation of the Elden Ring part of
[minecraft-crossover-bridge](https://github.com/justbustin/minecraft-crossover-bridge) (Copyright (c) 2026 justbustin).
Both are released under the MIT License, like this kit.

What this repository takes from them:

- **The shared-memory protocol.** [`protocol/c/bridge_protocol.h`](protocol/c/bridge_protocol.h) is Minecraft Ring's
  `bridge-base/elden-ring/er-bridge/include/bridge_protocol.h` at commit `711015afa67ab25c7b9597c68038718d1cef322e`,
  copied verbatim with only an attribution comment added at the top. `protocol/csharp/UltrakillBridge.Protocol` is a C#
  transcription of it (layout, offsets and semantics), and `host-sdk/csharp/UltrakillBridge.HostSdk` is a C# port of the
  behaviour of the host side of their DLL (seqlocks, control staleness, ray mailbox, entity table, damage ring, frame
  slot picking).
- **The design** documented in `docs/`: the stand-in and camera-override model, the ray mailbox as the terrain mechanism,
  the damage conversion, recalls and shared life, F8 hand-over, the input window and the frame compositing layout follow
  their Fabric mod and native host.
- **The reference host.** The Elden Ring host DLLs (`dinput8.dll`, `erbridge_core.dll`) are *not* part of this repository;
  they are built from Minecraft Ring's sources. The guest is compatible with them unchanged.

Their license notice, which also covers the header and the ported behaviour:

```
MIT License

Copyright (c) 2026 siddoff
Copyright (c) 2026 justbustin (upstream bridge)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

If you distribute the compiled Elden Ring host DLLs, also keep the MinHook (BSD 2-Clause) and Hacker Disassembler Engine
licenses bundled in Minecraft Ring's sources (`bridge-base/elden-ring/er-bridge/third_party/minhook/LICENSE.txt`).
This repository does not contain or distribute those binaries.

## UltraRing (MIT)

The ULTRAKILL guest, the fake host and the tooling were extracted from [UltraRing](https://github.com/Themetralla3000/UltraRing)
(Copyright (c) 2026 Arnau Encinas, MIT), the same author's project, release 0.2.0.

## BepInEx and its dependencies

The guest is loaded into ULTRAKILL through [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.5 (LGPL-2.1), which bundles
UnityDoorstop, HarmonyX, MonoMod and Mono.Cecil under their own licenses. `scripts/Build.ps1` downloads the official
release into `.tools/` and stages it into `dist/guest` and the release zip; the BepInEx binaries are not stored in this
repository. If you redistribute the release zip you are redistributing BepInEx: keep its license files and make its
source available as the LGPL requires (it is public at the link above). The guest plugin only calls BepInEx and HarmonyX;
it does not modify them. The build also uses the `BepInEx.AssemblyPublicizer.MSBuild` NuGet package (MIT) to access
ULTRAKILL's internals at compile time.

## Games

ULTRAKILL (New Blood Interactive / Arsi "Hakita" Patala) and any host game are not included. You need your own copies. Game
names and assets belong to their owners; the MIT License covers the bridge code only and grants no rights to any game. The
build references ULTRAKILL's `Managed` DLLs from your local installation; they are not copied into this repository or the
release zip.
