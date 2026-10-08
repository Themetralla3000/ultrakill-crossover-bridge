using System;
using System.Collections.Generic;
using UltrakillBridge.Link;
using UltrakillBridge.Loadout;

namespace UltrakillBridge.Guest
{
    /// <summary>
    /// Decides which weapons V1 has (config Mode + the host's optional ErmcHostEvents block) and applies it through
    /// ULTRAKILL's forced-loadout mechanism (GunSetter.forcedLoadout / FistControl.forcedLoadout, as PlayerLoadout.SetLoadout does),
    /// so the player's save is never touched. Progression: a seeded, reproducible order of unlocks; one more item per boss defeated.
    /// </summary>
    internal sealed class LoadoutManager
    {
        private enum Effective { Save, All, Progression }

        private ErmcHostEvents _host;
        private bool _haveHost;

        private ForcedLoadout _applied;       // what we installed (null = nothing / restored)
        private GunSetter _appliedGuns;
        private FistControl _appliedFists;
        private string _appliedKey;           // identifies the desired state, to detect changes
        private int _lastCount = -1;
        private ulong _lastSeed;
        private bool _lastWasProgression;
        private long _retryAtMs;
        private List<string> _order = new List<string>();
        private string _orderSig;

        /// <summary>Items currently unlocked (progression); 0 otherwise.</summary>
        public int Unlocked { get; private set; }
        public int Total => _order.Count;

        /// <summary>Call every frame in a level; it only acts when something changed or the scene was rebuilt.</summary>
        public void Tick(GuestLink link, bool hostAlive, long nowMs)
        {
            if (hostAlive)
            {
                if (link.ReadHostEvents(out var ev)) { _host = ev; _haveHost = true; }
                else _haveHost = false; // alive, but the host never writes the block
            }
            // host gone: keep the last known events so a hiccup does not flip the loadout

            var mode = Resolve();
            var guns = MonoSingleton<GunSetter>.TryGetInstance(out var g) ? g : null;
            var fists = MonoSingleton<FistControl>.TryGetInstance(out var f) ? f : null;
            if (guns == null || fists == null || guns.gunc == null) return;

            if (mode == Effective.Save)
            {
                // Hand the player's own loadout back if we had replaced it.
                if (_applied != null)
                {
                    if (guns.forcedLoadout == _applied) guns.forcedLoadout = null;
                    if (fists.forcedLoadout == _applied) fists.forcedLoadout = null;
                    _applied = null;
                    _appliedKey = null;
                    Safe(() => { guns.ResetWeapons(); fists.ResetFists(); }, nowMs, null);
                    Plugin.Log.LogInfo("Loadout: back to the save's weapons.");
                }
                _lastWasProgression = false;
                Unlocked = 0;
                return;
            }

            ulong seed = _haveHost ? _host.runSeed : 0UL;
            uint bosses = mode == Effective.Progression && _haveHost ? HostCounter(Protocol.CtrBossesDefeated) : 0u;
            List<string> unlocked;
            string key;
            if (mode == Effective.All)
            {
                unlocked = null; // everything
                key = "all";
            }
            else
            {
                BuildOrder(seed);
                int n = ProgressionOrder.UnlockedCount(Math.Max(1, ParseStart().Count), bosses, BridgeConfig.UnlocksPerBoss.Value, _order.Count);
                unlocked = _order.GetRange(0, Math.Min(n, _order.Count));
                key = "prog:" + seed + ":" + unlocked.Count;
                Unlocked = unlocked.Count;
            }

            // The game replaces the loadout on scene loads (new GunSetter) and some levels call PlayerLoadout.SetLoadout.
            bool intact = _applied != null && guns == _appliedGuns && fists == _appliedFists
                          && guns.forcedLoadout == _applied && fists.forcedLoadout == _applied;
            if (key == _appliedKey && intact) return;
            if (nowMs < _retryAtMs) return;

            ForcedLoadout fl = Compose(unlocked);
            bool ok = true;
            Safe(() =>
            {
                guns.forcedLoadout = fl;
                fists.forcedLoadout = fl;
                guns.ResetWeapons();
                fists.ResetFists();
            }, nowMs, () => ok = false);
            if (!ok) return;

            bool sameRun = mode == Effective.Progression && _lastWasProgression && _lastSeed == seed && _lastCount >= 0;
            if (sameRun && unlocked.Count > _lastCount)
                for (int i = _lastCount; i < unlocked.Count; i++) Announce(unlocked[i]);
            Plugin.Log.LogInfo(mode == Effective.All
                ? "Loadout: all weapons."
                : $"Loadout: progression, {unlocked.Count}/{_order.Count} unlocked ({string.Join(",", unlocked)}), run seed {seed}, bosses {bosses}.");
            _applied = fl;
            _appliedGuns = guns;
            _appliedFists = fists;
            _appliedKey = key;
            _lastWasProgression = mode == Effective.Progression;
            _lastSeed = seed;
            _lastCount = unlocked != null ? unlocked.Count : -1;
        }

        private void Safe(Action a, long nowMs, Action onFail)
        {
            try { a(); }
            catch (Exception e)
            {
                _retryAtMs = nowMs + 1000;
                Plugin.Log.LogWarning("Loadout apply failed, retrying: " + e.Message);
                if (onFail != null) onFail();
            }
        }

        private unsafe uint HostCounter(int i) => i >= 0 && i < Protocol.HostEventCounters ? _host.counters[i] : 0u;

        private Effective Resolve()
        {
            string m = (BridgeConfig.LoadoutMode.Value ?? "Host").Trim();
            if (m.Equals("All", StringComparison.OrdinalIgnoreCase)) return Effective.All;
            if (m.Equals("Progression", StringComparison.OrdinalIgnoreCase)) return Effective.Progression;
            if (m.Equals("Save", StringComparison.OrdinalIgnoreCase)) return Effective.Save;
            if (!_haveHost) return Effective.Save;
            switch (_host.loadoutMode)
            {
                case Protocol.LoadoutAll: return Effective.All;
                case Protocol.LoadoutProgression: return Effective.Progression;
                default: return Effective.Save;
            }
        }

        private static List<string> ParseStart()
        {
            var start = LoadoutCatalog.ParseList(BridgeConfig.ProgressionStart.Value, null);
            if (start.Count == 0) start.Add("rev0");
            return start;
        }

        private void BuildOrder(ulong seed)
        {
            string sig = seed + "|" + BridgeConfig.ProgressionStart.Value + "|" + BridgeConfig.ProgressionPool.Value;
            if (sig == _orderSig) return;
            _orderSig = sig;
            var unknown = new List<string>();
            var start = LoadoutCatalog.ParseList(BridgeConfig.ProgressionStart.Value, unknown);
            if (start.Count == 0) start.Add("rev0");
            var pool = LoadoutCatalog.ParseList(BridgeConfig.ProgressionPool.Value, unknown);
            if (pool.Count == 0) pool = LoadoutCatalog.ParseList("all", null);
            if (unknown.Count > 0) Plugin.Log.LogWarning("Loadout: unknown item ids ignored: " + string.Join(",", unknown));
            _order = ProgressionOrder.Build(start, pool, seed);
        }

        private static ForcedLoadout Compose(List<string> unlocked)
        {
            var fl = new ForcedLoadout
            {
                revolver = new VariantSetting(), altRevolver = new VariantSetting(),
                shotgun = new VariantSetting(), altShotgun = new VariantSetting(),
                nailgun = new VariantSetting(), altNailgun = new VariantSetting(),
                railcannon = new VariantSetting(), rocketLauncher = new VariantSetting(),
                arm = new ArmVariantSetting(),
            };
            foreach (var it in LoadoutCatalog.All)
            {
                bool on = unlocked == null || unlocked.Contains(it.Id);
                Set(fl, it, on ? VariantOption.ForceOn : VariantOption.ForceOff);
            }
            return fl;
        }

        // Pref digit -> colour slot (GunSetter.CheckWeapon / FistControl.CheckFist):
        // rev0 blue, rev1 red, rev2 green; sho/nai/rai/rock 0 blue, 1 green, 2 red; arm0 blue, arm1 red, arm2 green.
        private static void Set(ForcedLoadout fl, LoadoutItem it, VariantOption v)
        {
            switch (it.Family)
            {
                case "rev": Colour(it.Alt ? fl.altRevolver : fl.revolver, it.Variant, 0, 2, 1, v); break;
                case "sho": Colour(it.Alt ? fl.altShotgun : fl.shotgun, it.Variant, 0, 1, 2, v); break;
                case "nai": Colour(it.Alt ? fl.altNailgun : fl.nailgun, it.Variant, 0, 1, 2, v); break;
                case "rai": Colour(fl.railcannon, it.Variant, 0, 1, 2, v); break;
                case "rock": Colour(fl.rocketLauncher, it.Variant, 0, 1, 2, v); break;
                case "arm":
                    if (it.Variant == 0) fl.arm.blueVariant = v;
                    else if (it.Variant == 1) fl.arm.redVariant = v;
                    else fl.arm.greenVariant = v;
                    break;
            }
        }

        private static void Colour(VariantSetting s, int digit, int blueDigit, int greenDigit, int redDigit, VariantOption v)
        {
            if (digit == blueDigit) s.blueVariant = v;
            else if (digit == greenDigit) s.greenVariant = v;
            else if (digit == redDigit) s.redVariant = v;
        }

        private static void Announce(string id)
        {
            var it = LoadoutCatalog.Find(id);
            if (it == null) return;
            Plugin.Log.LogInfo("NEW WEAPON: " + it.Display);
            try
            {
                if (MonoSingleton<HudMessageReceiver>.TryGetInstance(out var hud))
                    hud.SendHudMessage("<color=orange>NEW WEAPON</color>: " + it.Display);
            }
            catch (Exception e) { Plugin.Log.LogWarning("HUD message failed: " + e.Message); }
        }
    }
}
