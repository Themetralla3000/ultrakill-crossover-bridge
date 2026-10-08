using System;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using UltrakillBridge.Link;

namespace UltrakillBridge.HostSdk
{
    /// <summary>
    /// The HOST's view of bridge.shm: a game-agnostic C# counterpart of the native host side
    /// of Minecraft Ring (shm.cpp / game.cpp / frame.cpp). Used by the fake host and usable by any C# host plugin
    /// (for example a BepInEx or MelonLoader plugin for a Unity game).
    /// The game-specific parts (world raycast, entity list, stand-in handling) stay in the caller; this class
    /// owns the protocol: header init, seqlocks, control staleness, ray mailbox, damage ring, counters.
    /// Same memory-model rules as <see cref="GuestLink"/>: plain stores + barriers + Volatile on counters.
    /// </summary>
    public sealed unsafe class HostLink : IDisposable
    {
        /// <summary>Control block counts as inactive if its seq did not change for this long (game.cpp:546-565).</summary>
        public const long ControlTimeoutMs = 1000;
        /// <summary>mcHeartbeat unchanged for this long = guest gone (environment rule, reused as liveness).</summary>
        public const long GuestAliveTimeoutMs = 2000;
        /// <summary>Ray mailbox time budget per host frame (game.cpp:2214-2222).</summary>
        public const double DefaultRayBudgetMs = 2.0;

        /// <summary>
        /// Casts one ray segment start->end in host stable-frame metres. Returns true on hit and the hit point.
        /// The normal is NOT supplied: the protocol host synthesises it (see <see cref="ServiceRays"/>).
        /// </summary>
        public delegate bool RayCastFunc(Vector3 start, Vector3 end, out Vector3 hitPos);

        private MappedFile _file;
        private byte* _b;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // control_active state
        private ErmcControl _ctrl;
        private bool _haveCtrl;
        private uint _ctrlSeenSeq;
        private bool _ctrlSeenValid;
        private long _ctrlSeqChangeMs;
        private bool _ctrlActive;

        // guest liveness (mcHeartbeat)
        private ulong _lastGuestHb;
        private bool _haveGuestHb;
        private long _guestHbChangeMs;

        // baselines for guest->host counters (first poll only latches, like g_actionSeen)
        private uint _seenDeaths, _seenAction, _seenFocus;
        private bool _haveDeaths, _haveAction, _haveFocus;

        private string _prompt = "";

        public string LastError { get; private set; }
        public bool Mapped => _b != null;
        public long NowMs => _clock.ElapsedMilliseconds;
        public string Path { get; private set; }

        // statistics (cheap counters for debug overlays)
        public uint RayBatchesServed { get; private set; }
        public ulong RaysServed { get; private set; }
        public uint DamageConsumed { get; private set; }
        public uint DamageDropped { get; private set; }

        private ErmcHeader* H => (ErmcHeader*)(_b + Protocol.OffHeader);

        // ---- open / init / shutdown -------------------------------------------------------------

        /// <summary>Opens or creates bridge.shm and initialises it exactly like shm.cpp. False on failure (see LastError).</summary>
        public bool Open(string path = null)
        {
            if (_b != null) return true;
            try
            {
                Path = path ?? BridgePaths.File("bridge.shm");
                _file = MappedFile.Open(Path, Protocol.ShmSize);
                _b = _file.Base;
                InitHeader();
                LastError = null;
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                _file?.Dispose();
                _file = null;
                _b = null;
                return false;
            }
        }

        private void InitHeader()
        {
            ErmcHeader* h = H;
            if (h->magic != Protocol.Magic || h->version != Protocol.Version)
            {
                // memset(base, 0, 0x100000): everything below the ray mailbox, nothing above.
                for (long i = 0; i < Protocol.OffRays; i += 8) *(ulong*)(_b + i) = 0;
                // Also drop a stale optional run block left by a previous host (ErmcHostEvents).
                for (long i = 0; i < sizeof(ErmcHostEvents); i += 8) *(ulong*)(_b + Protocol.OffHostEvents + i) = 0;
                for (long i = 0; i < sizeof(ErmcGuestRequests); i += 8) *(ulong*)(_b + Protocol.OffGuestRequests + i) = 0;
                for (long i = 0; i < sizeof(ErmcHostCombat); i += 8) *(ulong*)(_b + Protocol.OffHostCombat + i) = 0;
                h->version = Protocol.Version;
                h->size = Protocol.ShmSize;
                Thread.MemoryBarrier();
                Volatile.Write(ref h->magic, Protocol.Magic);
            }
            uint pid = (uint)Process.GetCurrentProcess().Id;
            if (h->hostPid != pid)
            {
                h->hostPid = pid;
                h->hostStartMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                h->coreGeneration = 0;
                h->coreStatus = 0;
            }
            // The loader's part (proxy.cpp): generation + "core running".
            h->coreGeneration = h->coreGeneration + 1;
            Thread.MemoryBarrier();
            Volatile.Write(ref h->coreStatus, 1);
            // NOTE: the ray mailbox, entity table and damage ring are deliberately NOT cleared (stale data from a
            // previous session survives, same as the real host); the damage consumer handles it.
        }

        public void SetCoreStatus(int status)
        {
            if (_b != null) Volatile.Write(ref H->coreStatus, status);
        }

        public void Dispose()
        {
            if (_b != null) Volatile.Write(ref H->coreStatus, 0);   // clean shutdown: "core not loaded"
            _file?.Dispose();
            _file = null;
            _b = null;
        }

        // ---- header: heartbeat, life, counters ------------------------------------------------------

        public ulong HostHeartbeat => _b != null ? Volatile.Read(ref H->hostHeartbeat) : 0;

        /// <summary>hostHeartbeat++ (once per host frame). Returns the new value (also the state's frame counter).</summary>
        public ulong BumpHeartbeat()
        {
            if (_b == null) return 0;
            ulong v = H->hostHeartbeat + 1;
            Volatile.Write(ref H->hostHeartbeat, v);
            return v;
        }

        public uint HostLife => _b != null ? Volatile.Read(ref H->hostLife) : 0;
        public uint HostDeaths => _b != null ? Volatile.Read(ref H->hostDeaths) : 0;
        public uint SwitchRequests => _b != null ? Volatile.Read(ref H->mcSwitchReq) : 0;
        public uint GuestDeaths => _b != null ? Volatile.Read(ref H->mcDeaths) : 0;
        public uint GuestPid => _b != null ? Volatile.Read(ref H->mcPid) : 0;
        public ulong GuestHeartbeat => _b != null ? Volatile.Read(ref H->mcHeartbeat) : 0;

        /// <summary>The host character became usable at a place the guest did not choose: the guest must recall.</summary>
        public void BumpHostLife()
        {
            if (_b != null) Volatile.Write(ref H->hostLife, H->hostLife + 1);
        }

        /// <summary>The stood-in character died in the host without the guest asking.</summary>
        public void BumpHostDeaths()
        {
            if (_b != null) Volatile.Write(ref H->hostDeaths, H->hostDeaths + 1);
        }

        /// <summary>F8 pressed in the host window: ask the guest to take control back (mcSwitchReq++).</summary>
        public void BumpSwitchRequest()
        {
            if (_b != null) Volatile.Write(ref H->mcSwitchReq, H->mcSwitchReq + 1);
        }

        /// <summary>True when the guest bumped hostFocusReq since the last call (first call only latches).</summary>
        public bool PollFocusRequest()
        {
            if (_b == null) return false;
            uint v = Volatile.Read(ref H->hostFocusReq);
            if (!_haveFocus) { _haveFocus = true; _seenFocus = v; return false; }
            if (v == _seenFocus) return false;
            _seenFocus = v;
            return true;
        }

        /// <summary>
        /// True when the guest bumped mcDeaths since the last call (first call only latches). The caller applies the
        /// "stood in within 2000 ms, ALIVE, not dead" conditions.
        /// </summary>
        public bool PollGuestDeath()
        {
            if (_b == null) return false;
            uint v = Volatile.Read(ref H->mcDeaths);
            if (!_haveDeaths) { _haveDeaths = true; _seenDeaths = v; return false; }
            if (v == _seenDeaths) return false;
            _seenDeaths = v;
            return true;
        }

        /// <summary>
        /// True when the guest bumped mcActionReq since the last call. Like game.cpp's service_action the first
        /// call only latches (requests made before the first service are ignored).
        /// </summary>
        public bool PollActionRequest(out uint req)
        {
            req = 0;
            if (_b == null) return false;
            uint v = Volatile.Read(ref H->mcActionReq);
            if (!_haveAction) { _haveAction = true; _seenAction = v; return false; }
            if (v == _seenAction) return false;
            _seenAction = v;
            req = v;
            return true;
        }

        /// <summary>Writes hostActionResult, then (release) hostActionAck = req.</summary>
        public void AckAction(uint req, int result)
        {
            if (_b == null) return;
            Volatile.Write(ref H->hostActionResult, result);
            Thread.MemoryBarrier();
            Volatile.Write(ref H->hostActionAck, req);
        }

        /// <summary>Publishes the interact prompt ("" = none); hostPromptSeq goes odd while rewriting. No-op if unchanged.</summary>
        public void SetPrompt(string text)
        {
            if (_b == null) return;
            text = text ?? "";
            if (text == _prompt) return;
            // UTF-8, at most 63 bytes + NUL, never cut inside a character.
            while (text.Length > 0 && Encoding.UTF8.GetByteCount(text) > 63) text = text.Substring(0, text.Length - 1);
            _prompt = text;
            ErmcHeader* h = H;
            uint s = Volatile.Read(ref h->hostPromptSeq);
            if ((s & 1) != 0) s++;
            Volatile.Write(ref h->hostPromptSeq, s + 1);
            Thread.MemoryBarrier();
            byte* dst = h->hostPrompt;
            for (int i = 0; i < 64; i++) dst[i] = 0;
            if (text.Length > 0) Encoding.UTF8.GetBytes(text.AsSpan(), new Span<byte>(dst, 63));
            Thread.MemoryBarrier();
            Volatile.Write(ref h->hostPromptSeq, s + 2);
        }

        // ---- guest liveness ---------------------------------------------------------------------------

        /// <summary>Samples mcHeartbeat. Call once per host frame. The first sample never counts as live.</summary>
        public void UpdateGuestLiveness()
        {
            if (_b == null) return;
            ulong hb = Volatile.Read(ref H->mcHeartbeat);
            long now = NowMs;
            if (!_haveGuestHb) { _haveGuestHb = true; _lastGuestHb = hb; _guestHbChangeMs = long.MinValue; }
            else if (hb != _lastGuestHb) { _lastGuestHb = hb; _guestHbChangeMs = now; }
        }

        public bool GuestAlive => _guestHbChangeMs != long.MinValue && _haveGuestHb && NowMs - _guestHbChangeMs < GuestAliveTimeoutMs;

        // ---- game state (host -> guest) -----------------------------------------------------------------

        /// <summary>
        /// Seqlocked publish of bytes [4, 0x114) of the state (frame.cpp:77-112). seq is managed here; s.seq is
        /// updated to the published value for convenience. The caller fills s.frame (usually BumpHeartbeat()).
        /// </summary>
        public void WriteState(ref ErmcGameState s)
        {
            if (_b == null) return;
            var p = (ErmcGameState*)(_b + Protocol.OffState);
            uint seq = SeqBegin(&p->seq);
            fixed (ErmcGameState* src = &s)
                Buffer.MemoryCopy((byte*)src + 4, (byte*)p + 4, sizeof(ErmcGameState) - 4, sizeof(ErmcGameState) - 4);
            s.seq = SeqEnd(&p->seq, seq);
        }

        // ---- control (guest -> host) ------------------------------------------------------------------

        /// <summary>
        /// control_active() semantics (game.cpp:546-565): takes a seqlocked snapshot (the last good one is reused on
        /// torn reads), tracks when seq last changed, and reports inactive if it has not changed for more than
        /// 1000 ms or was never published (seq == 0). Call once per host frame.
        /// </summary>
        public bool UpdateControl()
        {
            if (_b == null) return _ctrlActive = false;
            long now = NowMs;
            var p = (ErmcControl*)(_b + Protocol.OffControl);
            for (int tries = 0; tries < 64; tries++)
            {
                uint s1 = Volatile.Read(ref p->seq);
                if (s1 == 0) { _haveCtrl = false; _ctrlSeenValid = false; break; }          // never published (or re-initialised)
                if ((s1 & 1) != 0) { Thread.SpinWait(1); continue; }
                ErmcControl tmp = *p;
                Thread.MemoryBarrier();
                if (Volatile.Read(ref p->seq) == s1) { _ctrl = tmp; _haveCtrl = true; break; }
            }
            if (!_haveCtrl) return _ctrlActive = false;
            if (!_ctrlSeenValid || _ctrl.seq != _ctrlSeenSeq)
            {
                _ctrlSeenValid = true;
                _ctrlSeenSeq = _ctrl.seq;
                _ctrlSeqChangeMs = now;
            }
            _ctrlActive = now - _ctrlSeqChangeMs <= ControlTimeoutMs;
            return _ctrlActive;
        }

        /// <summary>Result of the last <see cref="UpdateControl"/>.</summary>
        public bool ControlActive => _ctrlActive;

        /// <summary>Last good control snapshot (valid if <see cref="HasControl"/>, even when inactive).</summary>
        public ErmcControl Control => _ctrl;
        public bool HasControl => _haveCtrl;

        /// <summary>
        /// The compositor's view (compositor.cpp:950-952): last snapshot, NO staleness check. A guest that died with
        /// COMPOSITE set keeps its last frame on screen.
        /// </summary>
        public bool CompositorControl(out ErmcControl c)
        {
            c = _ctrl;
            return _haveCtrl;
        }

        // ---- hunter events (host -> guest damage) ------------------------------------------------------

        /// <summary>
        /// Records one HP-loss event of the stand-in (game.cpp:1064-1089): hitCount++, totalDamage += damage,
        /// lastDamage/lastHitFrom/hunterMaxHp/lastHitFrame/lastHitKind, all under the seqlock. <paramref name="kind"/> is an
        /// ErmcEntity kind; OR in <see cref="Protocol.HunterKindParried"/> when the host rejected the hit for the guest's parry window.
        /// </summary>
        public void ReportHunterHit(float damage, Vector3 from, float hunterMaxHp, ulong frame, uint kind)
        {
            if (_b == null) return;
            var p = (ErmcHunterEvents*)(_b + Protocol.OffHunter);
            uint s = SeqBegin(&p->seq);
            p->hitCount = p->hitCount + 1;
            p->totalDamage = p->totalDamage + damage;
            p->lastDamage = damage;
            p->lastHitFrom[0] = from.X; p->lastHitFrom[1] = from.Y; p->lastHitFrom[2] = from.Z;
            p->hunterMaxHp = hunterMaxHp;
            p->lastHitFrame = frame;
            p->lastHitKind = kind;
            SeqEnd(&p->seq, s);
        }

        // ---- host events (optional run info, ErmcHostEvents) ------------------------------------------

        /// <summary>
        /// Publishes the optional run block (loadout mode, run seed, progress counters; at most 16, extra ignored).
        /// Cheap to call every frame: it only touches memory when something changed. Entries of
        /// <paramref name="counters"/> beyond its length are written as 0.
        /// </summary>
        public void WriteHostEvents(uint loadoutMode, ulong runSeed, uint[] counters) =>
            WriteHostEvents(loadoutMode, runSeed, counters, 0);

        /// <param name="flags"><see cref="Protocol.HostFlagDrawsPrompt"/> | <see cref="Protocol.HostFlagNeedsInput"/>.</param>
        public void WriteHostEvents(uint loadoutMode, ulong runSeed, uint[] counters, uint flags)
        {
            if (_b == null) return;
            var p = (ErmcHostEvents*)(_b + Protocol.OffHostEvents);
            bool init = p->magic != Protocol.HostEventsMagic || p->version != Protocol.HostEventsVersion;
            if (!init && p->loadoutMode == loadoutMode && p->runSeed == runSeed && p->flags == flags)
            {
                bool same = true;
                for (int i = 0; i < Protocol.HostEventCounters && same; i++)
                    same = p->counters[i] == (counters != null && i < counters.Length ? counters[i] : 0u);
                if (same) return;
            }
            uint s = SeqBegin(&p->seq);
            p->version = Protocol.HostEventsVersion;
            p->flags = flags;
            p->loadoutMode = loadoutMode;
            p->runSeed = runSeed;
            for (int i = 0; i < Protocol.HostEventCounters; i++)
                p->counters[i] = counters != null && i < counters.Length ? counters[i] : 0u;
            Thread.MemoryBarrier();
            p->magic = Protocol.HostEventsMagic;
            SeqEnd(&p->seq, s);
        }

        // ---- host combat (optional, ErmcHostCombat) ---------------------------------------------------

        /// <summary>
        /// Publishes the optional combat block that goes with <see cref="Protocol.HostFlagStatDamage"/>: the host
        /// character's damage / crit stats and the per-weapon table (<paramref name="k"/> and <paramref name="proc"/>,
        /// indexed by weapon id, up to 64 entries; shorter arrays leave the rest 0). Cheap to call every frame: it only
        /// touches memory when something changed. Call it BEFORE setting the flag in <see cref="WriteHostEvents(uint, ulong, uint[], uint)"/>.
        /// </summary>
        public void WriteHostCombat(uint level, float damage, float critPercent, float critMultiplier,
            float damageScale, float headshotMultiplier, float[] k, float[] proc)
        {
            if (_b == null) return;
            var p = (ErmcHostCombat*)(_b + Protocol.OffHostCombat);
            bool init = p->magic != Protocol.HostCombatMagic || p->version != Protocol.HostCombatVersion;
            bool same = !init && p->level == level && p->damage == damage && p->critPercent == critPercent
                && p->critMultiplier == critMultiplier && p->damageScale == damageScale
                && p->headshotMultiplier == headshotMultiplier && (p->flags & Protocol.CombatStatsValid) != 0;
            for (int i = 0; i < Protocol.WeaponSlots && same; i++)
            {
                float kk = k != null && i < k.Length ? k[i] : 0f, pp = proc != null && i < proc.Length ? proc[i] : 0f;
                same = p->weapons[i * 2] == kk && p->weapons[i * 2 + 1] == pp;
            }
            if (same) return;
            uint s = SeqBegin(&p->seq);
            p->version = Protocol.HostCombatVersion;
            p->flags |= Protocol.CombatStatsValid; // keep the health bits (WriteHostHealth)
            p->level = level;
            p->damage = damage;
            p->critPercent = critPercent;
            p->critMultiplier = critMultiplier;
            p->damageScale = damageScale;
            p->headshotMultiplier = headshotMultiplier;
            for (int i = 0; i < Protocol.WeaponSlots; i++)
            {
                p->weapons[i * 2] = k != null && i < k.Length ? k[i] : 0f;
                p->weapons[i * 2 + 1] = proc != null && i < proc.Length ? proc[i] : 0f;
            }
            Thread.MemoryBarrier();
            p->magic = Protocol.HostCombatMagic;
            SeqEnd(&p->seq, s);
        }

        /// <summary>
        /// Publishes the host character's health in the combat block (host-authoritative health, advertised with
        /// <see cref="Protocol.HostFlagOwnsHealth"/>): current / full health, shield, barrier and curse, and whether the host
        /// considers the character dead (<see cref="Protocol.CombatDead"/>). Independent of <see cref="WriteHostCombat"/>
        /// (each keeps the other's flag bits). Cheap: only touches memory when a value changed. Call it every frame; health
        /// regenerates, so the block changes most frames. Call it BEFORE setting the flag in WriteHostEvents.
        /// </summary>
        public void WriteHostHealth(float health, float fullHealth, float shield, float fullShield, float barrier, float cursePenalty, bool dead)
        {
            if (_b == null) return;
            var p = (ErmcHostCombat*)(_b + Protocol.OffHostCombat);
            bool init = p->magic != Protocol.HostCombatMagic || p->version != Protocol.HostCombatVersion;
            bool wasDead = (p->flags & Protocol.CombatDead) != 0;
            if (!init && (p->flags & Protocol.CombatHealthValid) != 0 && wasDead == dead && p->health == health && p->fullHealth == fullHealth
                && p->shield == shield && p->fullShield == fullShield && p->barrier == barrier && p->cursePenalty == cursePenalty) return;
            uint s = SeqBegin(&p->seq);
            p->version = Protocol.HostCombatVersion;
            uint f = (p->flags | Protocol.CombatHealthValid) & ~Protocol.CombatDead;
            if (dead) f |= Protocol.CombatDead;
            p->flags = f;
            p->health = health;
            p->fullHealth = fullHealth;
            p->shield = shield;
            p->fullShield = fullShield;
            p->barrier = barrier;
            p->cursePenalty = cursePenalty;
            Thread.MemoryBarrier();
            p->magic = Protocol.HostCombatMagic;
            SeqEnd(&p->seq, s);
        }

        /// <summary>Clears the health fields of the combat block (the host stops owning health, e.g. a config switch): the guest falls back to V1's own HP.</summary>
        public void ClearHostHealth()
        {
            if (_b == null) return;
            var p = (ErmcHostCombat*)(_b + Protocol.OffHostCombat);
            if (p->magic != Protocol.HostCombatMagic || (p->flags & (Protocol.CombatHealthValid | Protocol.CombatDead)) == 0) return;
            uint s = SeqBegin(&p->seq);
            p->flags &= ~(Protocol.CombatHealthValid | Protocol.CombatDead);
            SeqEnd(&p->seq, s);
        }

        /// <summary>
        /// Publishes the RoR2-style stat ratios (advertised with <see cref="Protocol.HostFlagStats"/>) in the combat block. Every
        /// argument is a ratio against the stand-in body's base value (1.0 = unchanged; see <see cref="StatsWire"/> for helpers
        /// that build them); <paramref name="extraJumps"/> is maxJumpCount - baseJumpCount. Ratios are clamped to
        /// [0, <see cref="StatsWire.MaxRatio"/>]. Independent of WriteHostCombat / WriteHostHealth (each keeps the other's flag
        /// bits). Cheap: only touches memory when a value changed. Call it BEFORE setting the flag in WriteHostEvents.
        /// </summary>
        public void WriteHostRatios(float attackSpeed, float moveSpeed, uint extraJumps, float jumpPower, float sprintSpeed,
            float rechargeSecondary, float rechargeSpecial, float rechargeUtility)
        {
            if (_b == null) return;
            attackSpeed = Fix(attackSpeed); moveSpeed = Fix(moveSpeed); jumpPower = Fix(jumpPower); sprintSpeed = Fix(sprintSpeed);
            rechargeSecondary = Fix(rechargeSecondary); rechargeSpecial = Fix(rechargeSpecial); rechargeUtility = Fix(rechargeUtility);
            var p = (ErmcHostCombat*)(_b + Protocol.OffHostCombat);
            bool init = p->magic != Protocol.HostCombatMagic || p->version != Protocol.HostCombatVersion;
            if (!init && (p->flags & Protocol.CombatRatiosValid) != 0 && p->attackSpeedRatio == attackSpeed && p->moveSpeedRatio == moveSpeed
                && p->extraJumps == extraJumps && p->jumpPowerRatio == jumpPower && p->sprintSpeedRatio == sprintSpeed
                && p->rechargeSecondary == rechargeSecondary && p->rechargeSpecial == rechargeSpecial && p->rechargeUtility == rechargeUtility) return;
            uint s = SeqBegin(&p->seq);
            p->version = Protocol.HostCombatVersion;
            p->flags |= Protocol.CombatRatiosValid;
            p->attackSpeedRatio = attackSpeed;
            p->moveSpeedRatio = moveSpeed;
            p->extraJumps = extraJumps;
            p->jumpPowerRatio = jumpPower;
            p->sprintSpeedRatio = sprintSpeed;
            p->rechargeSecondary = rechargeSecondary;
            p->rechargeSpecial = rechargeSpecial;
            p->rechargeUtility = rechargeUtility;
            Thread.MemoryBarrier();
            p->magic = Protocol.HostCombatMagic;
            SeqEnd(&p->seq, s);
        }

        private static float Fix(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 1f : v < 0f ? 0f : v > StatsWire.MaxRatio ? StatsWire.MaxRatio : v;

        /// <summary>Clears the stat ratios (the host stops publishing them, e.g. a config switch): the guest returns V1 to its own stats.</summary>
        public void ClearHostRatios()
        {
            if (_b == null) return;
            var p = (ErmcHostCombat*)(_b + Protocol.OffHostCombat);
            if (p->magic != Protocol.HostCombatMagic || (p->flags & Protocol.CombatRatiosValid) == 0) return;
            uint s = SeqBegin(&p->seq);
            p->flags &= ~Protocol.CombatRatiosValid;
            SeqEnd(&p->seq, s);
        }

        // ---- guest requests (optional, ErmcGuestRequests) -----------------------------------------------

        /// <summary>
        /// Seqlocked read of the guest's optional request block (equipment / ping counters, held keys, interact key
        /// name). False if the guest never wrote it. Compare the counters with the previous read and baseline on the first.
        /// </summary>
        public bool ReadGuestRequests(out ErmcGuestRequests req)
        {
            req = default;
            if (_b == null) return false;
            var p = (ErmcGuestRequests*)(_b + Protocol.OffGuestRequests);
            for (int tries = 0; tries < 2000; tries++)
            {
                uint s1 = Volatile.Read(ref p->seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(1); continue; }
                req = *p;
                Thread.MemoryBarrier();
                if (Volatile.Read(ref p->seq) == s1)
                    return req.magic == Protocol.GuestRequestsMagic && req.version == Protocol.GuestRequestsVersion;
            }
            return false;
        }

        /// <summary>The UTF-8 interact key name from a request block ("" if none).</summary>
        public static unsafe string InteractKeyName(ref ErmcGuestRequests req)
        {
            fixed (byte* k = req.interactKey)
            {
                int n = 0;
                while (n < Protocol.InteractKeyChars && k[n] != 0) n++;
                return n == 0 ? "" : System.Text.Encoding.UTF8.GetString(k, n);
            }
        }

        // ---- ray mailbox ----------------------------------------------------------------------------------

        /// <summary>A batch is waiting (reqSeq != respSeq).</summary>
        public bool RayBatchPending =>
            _b != null && Volatile.Read(ref ((ErmcRayHeader*)(_b + Protocol.OffRays))->reqSeq) !=
                          Volatile.Read(ref ((ErmcRayHeader*)(_b + Protocol.OffRays))->respSeq);

        /// <summary>
        /// Services the ray mailbox (game.cpp:2201-2228). Call once per host frame, ONLY while the host is ALIVE.
        /// Processes rays from <c>processed</c> until <paramref name="budgetMs"/> has elapsed (the clock is checked
        /// every 16 rays, so at least 16 rays are done per call), writes the hits, and when the batch is complete
        /// sets respSeq = reqSeq then processed = 0. Hits: hit = 1 always on a hit, normal synthesised
        /// (a downward ray, -dy/len &gt; 0.7, gives (0,1,0), otherwise -dir), attr = 0; a miss is all zero.
        /// Returns the number of rays processed in this call.
        /// </summary>
        public int ServiceRays(RayCastFunc cast, double budgetMs = DefaultRayBudgetMs)
        {
            if (_b == null || cast == null) return 0;
            var r = (ErmcRayHeader*)(_b + Protocol.OffRays);
            uint req = Volatile.Read(ref r->reqSeq);
            if (req == Volatile.Read(ref r->respSeq)) return 0;
            Thread.MemoryBarrier();
            uint count = Math.Min(r->count, (uint)Protocol.MaxRays);
            uint i = Math.Min(r->processed, count);
            var rays = (ErmcRay*)(_b + Protocol.OffRays + Protocol.RaysOffRays);
            var hits = (ErmcRayHit*)(_b + Protocol.OffRays + Protocol.RaysOffHits);
            long t0 = Stopwatch.GetTimestamp();
            long budgetTicks = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            int done = 0;
            while (i < count)
            {
                if (done > 0 && (done & 15) == 0 && Stopwatch.GetTimestamp() - t0 >= budgetTicks) break;
                ErmcRay* ray = rays + i;
                ErmcRayHit* hit = hits + i;
                var s = new Vector3(ray->start[0], ray->start[1], ray->start[2]);
                var e = new Vector3(ray->end[0], ray->end[1], ray->end[2]);
                if (cast(s, e, out Vector3 pos))
                {
                    Vector3 d = e - s;
                    float len = d.Length();
                    Vector3 n;
                    if (len > 1e-6f)
                        n = (-d.Y / len > 0.7f) ? new Vector3(0, 1, 0) : -d / len;
                    else
                        n = new Vector3(0, 1, 0);
                    hit->pos[0] = pos.X; hit->pos[1] = pos.Y; hit->pos[2] = pos.Z;
                    hit->normal[0] = n.X; hit->normal[1] = n.Y; hit->normal[2] = n.Z;
                    hit->hit = 1;
                }
                else
                {
                    hit->pos[0] = hit->pos[1] = hit->pos[2] = 0;
                    hit->normal[0] = hit->normal[1] = hit->normal[2] = 0;
                    hit->hit = 0;
                }
                hit->attr = 0;
                i++;
                done++;
            }
            RaysServed += (ulong)done;
            if (i >= count)
            {
                Thread.MemoryBarrier();
                Volatile.Write(ref r->respSeq, req);
                Volatile.Write(ref r->processed, 0u);
                RayBatchesServed++;
            }
            else
            {
                Volatile.Write(ref r->processed, i);
            }
            return done;
        }

        // ---- entity table (host -> guest) --------------------------------------------------------------

        /// <summary>Seqlocked publish of the entity table (max 256 entries).</summary>
        public void PublishEntities(ReadOnlySpan<ErmcEntity> entities, ulong frame)
        {
            if (_b == null) return;
            int n = Math.Min(entities.Length, Protocol.MaxEntities);
            var hdr = (ErmcEntityTableHeader*)(_b + Protocol.OffEntities);
            var dst = (ErmcEntity*)(_b + Protocol.OffEntities + 0x10);
            uint s = SeqBegin(&hdr->seq);
            for (int i = 0; i < n; i++) dst[i] = entities[i];
            hdr->count = (uint)n;
            hdr->frame = frame;
            SeqEnd(&hdr->seq, s);
        }

        /// <summary>Empties the table (count = 0), done every tick while the host is not ALIVE.</summary>
        public void ClearEntities(ulong frame) => PublishEntities(ReadOnlySpan<ErmcEntity>.Empty, frame);

        /// <summary>
        /// Fills one entity the way game.cpp:1381 does: feet pos, world-aligned box (flag bit1) centre
        /// (x, y + h/2, z) and half extents (r, h/2, r), hp clamped at 0, name as ASCII.
        /// </summary>
        public static void FillEntity(ref ErmcEntity e, ulong id, uint kind, uint emId, Vector3 feet, float radius,
            float height, float hp, float maxHp, bool dead, string name)
        {
            e.id = id;
            e.kind = kind;
            e.emId = emId;
            e.pos[0] = feet.X; e.pos[1] = feet.Y; e.pos[2] = feet.Z;
            e.quat[0] = 0; e.quat[1] = 0; e.quat[2] = 0; e.quat[3] = 1;
            e.boxCenter[0] = feet.X; e.boxCenter[1] = feet.Y + height * 0.5f; e.boxCenter[2] = feet.Z;
            e.boxHalf[0] = radius; e.boxHalf[1] = height * 0.5f; e.boxHalf[2] = radius;
            e.hp = Math.Max(hp, 0f);
            e.maxHp = maxHp;
            e.flags = Protocol.EntityWorldBox | (dead ? Protocol.EntityDead : 0u);
            int n = 0;
            if (name != null)
                for (; n < 47 && n < name.Length; n++) e.name[n] = (byte)name[n];
            for (; n < 48; n++) e.name[n] = 0;
        }

        // ---- damage ring (guest -> host) --------------------------------------------------------------------

        /// <summary>Entries the guest queued and the host has not consumed yet (capped at the ring size).</summary>
        public int PendingDamage
        {
            get
            {
                if (_b == null) return 0;
                var q = (ErmcDamageQueueHeader*)(_b + Protocol.OffDamage);
                uint w = Volatile.Read(ref q->write), r = q->read;
                uint n = w - r;
                return n > Protocol.DamageRing ? Protocol.DamageRing : (int)n;
            }
        }

        /// <summary>
        /// Consumes queued damage into <paramref name="buffer"/> (game.cpp:1911-1951). If the guest is more than 256
        /// ahead the oldest entries are dropped (r = w - 256). Stores read after copying. Returns the number copied;
        /// if the buffer is smaller than the backlog the rest stays queued for the next call.
        /// </summary>
        public int DrainDamage(Span<ErmcDamage> buffer)
        {
            if (_b == null) return 0;
            var q = (ErmcDamageQueueHeader*)(_b + Protocol.OffDamage);
            var ring = (ErmcDamage*)(_b + Protocol.OffDamage + 0x10);
            uint w = Volatile.Read(ref q->write);
            uint r = q->read;
            if (w - r > Protocol.DamageRing)
            {
                DamageDropped += (w - r) - Protocol.DamageRing;
                r = w - Protocol.DamageRing;
            }
            Thread.MemoryBarrier();
            int n = 0;
            while (r != w && n < buffer.Length)
            {
                buffer[n++] = ring[r % Protocol.DamageRing];
                r++;
            }
            Volatile.Write(ref q->read, r);
            DamageConsumed += (uint)n;
            return n;
        }

        // ---- seqlock helpers -------------------------------------------------------------------------------

        private static uint SeqBegin(uint* p)
        {
            uint s = Volatile.Read(ref *p);
            if ((s & 1) != 0) s++;
            Volatile.Write(ref *p, s + 1);
            Thread.MemoryBarrier();
            return s;
        }

        /// <summary>Publishes the even seq (never 0, "never published" sentinel) and returns it.</summary>
        private static uint SeqEnd(uint* p, uint begin)
        {
            Thread.MemoryBarrier();
            uint e = begin + 2;
            if (e == 0) e = 2;
            Volatile.Write(ref *p, e);
            return e;
        }
    }

    /// <summary>
    /// Host-side reader of frames.shm (compositor.cpp:141-226): opens the EXISTING file only, validates size,
    /// magic and version 3 once at map time, keeps the 8-entry applied-pose history, picks slots like pick_slot()
    /// and copies a slot's layers with seq re-validation (copy_slot()). Memory (BGRA) path only.
    /// </summary>
    public sealed unsafe class HostFrames : IDisposable
    {
        public const long RetryMs = 2000;

        private MappedFile _file;
        private byte* _b;
        private long _lastTryMs = long.MinValue;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ulong[] _poseHist = new ulong[8];
        private uint _posePos;

        public string LastError { get; private set; }
        public bool IsOpen => _b != null;

        /// <summary>Frames with frameId &gt; LastUploaded are "fresh"; the caller advances it after consuming one.</summary>
        public ulong LastUploaded;

        // pick_slot statistics ("exact / older / missing")
        public uint PickExact, PickOlder, PickMissing;

        /// <summary>
        /// Tries to open frames.shm (at most every 2000 ms unless <paramref name="force"/>). Requires the file to exist,
        /// size &gt;= FramesFileSize, magic 0x524D484D and version 3; otherwise unmaps and leaves LastError.
        /// </summary>
        public bool TryOpen(string path = null, bool force = false)
        {
            if (_b != null) return true;
            long now = _clock.ElapsedMilliseconds;
            if (!force && _lastTryMs != long.MinValue && now - _lastTryMs < RetryMs) return false;
            _lastTryMs = now;
            string err;
            MappedFile f = MappedFile.OpenExisting(path ?? BridgePaths.File("frames.shm"), Protocol.FramesFileSize, out err);
            if (f == null) { LastError = err; return false; }
            var h = (ErmcFramesHeader*)f.Base;
            if (Volatile.Read(ref h->magic) != Protocol.FramesMagic || h->version != Protocol.FramesVersion)
            {
                LastError = "bad frames header: magic=" + h->magic.ToString("X8") + " version=" + h->version;
                f.Dispose();
                return false;
            }
            _file = f;
            _b = f.Base;
            LastError = null;
            return true;
        }

        public void Dispose()
        {
            _file?.Dispose();
            _file = null;
            _b = null;
        }

        /// <summary>latestFrameId at +0x10 (the guest's release-stored value).</summary>
        public ulong LatestFrameId => _b != null ? Volatile.Read(ref *(ulong*)(_b + 0x10)) : 0;

        // ---- applied pose history (compositor.cpp:183-198) ----

        /// <summary>Call once per host frame in which a guest camera was actually applied, with control.mcFrame.</summary>
        public void NoteAppliedPose(ulong mcFrame)
        {
            _poseHist[_posePos & 7] = mcFrame;
            _posePos++;
        }

        /// <summary>
        /// poseId = history[(pos-1-lag) &amp; 7] with lag clamped to &lt;= pos-1 and &lt;= 6; 0 if there is no history.
        /// </summary>
        public ulong PoseForPresent(uint lag)
        {
            if (_posePos == 0) return 0;
            uint maxLag = _posePos - 1;
            if (maxLag > 6) maxLag = 6;
            if (lag > maxLag) lag = maxLag;
            return _poseHist[(_posePos - 1 - lag) & 7];
        }

        // ---- slots ----

        private ErmcFrameHeader* SlotHeader(int slot) => (ErmcFrameHeader*)(_b + 0x1000 + (long)slot * Protocol.FrameSlotSize);

        /// <summary>
        /// pick_slot(): skips odd seq and zero/oversized width/height; returns the slot with poseId == wanted at once,
        /// else the slot with the greatest poseId less than wanted, else -1.
        /// </summary>
        public int PickSlot(ulong wanted)
        {
            if (_b == null) return -1;
            int best = -1;
            ulong bestPose = 0;
            for (int i = 0; i < Protocol.FrameSlots; i++)
            {
                ErmcFrameHeader* h = SlotHeader(i);
                uint s = Volatile.Read(ref h->seq);
                if ((s & 1) != 0) continue;
                uint w = h->width, ht = h->height;
                if (w == 0 || w > Protocol.FrameMaxW || ht == 0 || ht > Protocol.FrameMaxH) continue;
                ulong pose = h->poseId;
                if (pose == wanted) { PickExact++; return i; }
                if (pose < wanted && (best < 0 || pose > bestPose)) { best = i; bestPose = pose; }
            }
            if (best >= 0) PickOlder++; else PickMissing++;
            return best;
        }

        /// <summary>Consistent copy of a slot header (false if the slot is being written or has an invalid size).</summary>
        public bool ReadSlotHeader(int slot, out ErmcFrameHeader hdr)
        {
            hdr = default;
            if (_b == null || slot < 0 || slot >= Protocol.FrameSlots) return false;
            ErmcFrameHeader* h = SlotHeader(slot);
            uint s1 = Volatile.Read(ref h->seq);
            if ((s1 & 1) != 0) return false;
            ErmcFrameHeader tmp = *h;
            Thread.MemoryBarrier();
            if (Volatile.Read(ref h->seq) != s1) return false;
            if (tmp.width == 0 || tmp.width > Protocol.FrameMaxW || tmp.height == 0 || tmp.height > Protocol.FrameMaxH) return false;
            hdr = tmp;
            return true;
        }

        /// <summary>
        /// copy_slot(): copies the slot's layers (each width*height*4 bytes, bottom-up rows) into the caller's buffers
        /// and re-validates seq afterwards (false if the guest rewrote the slot meanwhile; retry later). world and gui
        /// must hold at least width*height*4 bytes; depth (float32 per pixel) and hand are optional: an empty/short
        /// span skips that layer, and hand is only copied if the slot has FrameHand. Use ReadSlotHeader first to size
        /// the buffers. GPU-path slots (FrameGpu) cannot be read here and return false.
        /// </summary>
        public bool CopySlot(int slot, out ErmcFrameHeader hdr, Span<byte> world, Span<byte> depth, Span<byte> gui,
            Span<byte> hand)
        {
            hdr = default;
            if (_b == null || slot < 0 || slot >= Protocol.FrameSlots) return false;
            ErmcFrameHeader* h = SlotHeader(slot);
            uint s1 = Volatile.Read(ref h->seq);
            if ((s1 & 1) != 0) return false;
            ErmcFrameHeader tmp = *h;
            if (tmp.width == 0 || tmp.width > Protocol.FrameMaxW || tmp.height == 0 || tmp.height > Protocol.FrameMaxH) return false;
            if ((tmp.flags & Protocol.FrameGpu) != 0) return false;
            int layer = (int)(tmp.width * tmp.height * 4);
            if (world.Length < layer || gui.Length < layer) return false;
            byte* data = (byte*)h + Protocol.FrameHdr;
            new ReadOnlySpan<byte>(data, layer).CopyTo(world);
            if (depth.Length >= layer) new ReadOnlySpan<byte>(data + layer, layer).CopyTo(depth);
            new ReadOnlySpan<byte>(data + 2L * layer, layer).CopyTo(gui);
            if ((tmp.flags & Protocol.FrameHand) != 0 && hand.Length >= layer)
                new ReadOnlySpan<byte>(data + 3L * layer, layer).CopyTo(hand);
            Thread.MemoryBarrier();
            if (Volatile.Read(ref h->seq) != s1) return false;
            hdr = tmp;
            return true;
        }
    }
}
