using UltrakillBridge.Link;
using UltrakillBridge.Guest.Terrain;
using UnityEngine;
using UnityEngine.UI;

namespace UltrakillBridge.Guest.Interaction
{
    /// <summary>
    /// The host's own interactions (doors, levers, items, checkpoints): shows the action the host offers on
    /// ULTRAKILL's HUD and performs it on the interact key (protocol.md section 7). Holding the key repeats the action at
    /// the configured cadence; the equipment and ping keys raise the optional guest requests (ErmcGuestRequests); the
    /// HUD label is hidden when the host advertises that it draws its own prompt.
    /// </summary>
    internal sealed class HostInteraction
    {
        private const long AckTimeoutMs = 2000;
        private const long PromptPollMs = 150;
        private const long FlashMs = 2000;
        /// <summary>A door or lever needs a moment to move before its collision is worth resampling.</summary>
        private const long ResampleDelayMs = 900;

        private bool _pending;
        private uint _pendingReq;
        private long _pendingAtMs;
        private long _nextPromptPollMs;
        private string _prompt = "";
        private string _flash;
        private long _flashUntilMs;
        private long _resampleAtMs = long.MinValue;
        private Text _label;
        private string _shown;
        private int _performed;
        private uint _timedOutReq;
        private string _promptCacheSrc, _promptCacheText = "";
        private KeyCode _promptCacheKey;
        private long _nextRepeatMs;
        private bool _repeating;
        private KeyCode _labelKey = KeyCode.None;
        private bool _hostDrawsPrompt;

        public string Status => $"prompt '{_prompt}', {_performed} actions{(_pending ? ", waiting for the host" : "")}";

        public void Tick(GuestLink link, bool driving, uint hostFlags, TerrainManager terrain, CoordMap map, Vector3 feetUk)
        {
            long now = link.NowMs;
            _hostDrawsPrompt = (hostFlags & Protocol.HostFlagDrawsPrompt) != 0;
            TickGuestRequests(link, driving);
            if (now >= _nextPromptPollMs)
            {
                _nextPromptPollMs = now + PromptPollMs;
                _prompt = driving ? link.HostPrompt : "";
            }

            var interactKey = BridgeConfig.InteractKey.Value;
            bool press = driving && !_pending && Input.GetKeyDown(interactKey);
            bool repeat = !press && driving && !_pending && BridgeConfig.HoldRepeat.Value && Input.GetKey(interactKey)
                          && now >= _nextRepeatMs && _prompt.Length > 0;
            if (!Input.GetKey(interactKey)) _repeating = false;
            if (press || repeat)
            {
                _timedOutReq = 0;
                _pendingReq = link.RequestAction();
                _pending = _pendingReq != 0;
                _pendingAtMs = now;
                _repeating = repeat;
                _nextRepeatMs = now + BridgeConfig.RepeatIntervalMs.Value;
            }
            if (_pending)
            {
                if (link.ActionAck == _pendingReq)
                {
                    _pending = false;
                    HandleResult(link.ActionResult, now);
                    // The cadence runs from the answer, like RoR2's own cooldown.
                    _nextRepeatMs = now + BridgeConfig.RepeatIntervalMs.Value;
                }
                else if (now - _pendingAtMs > AckTimeoutMs)
                {
                    _pending = false;
                    _timedOutReq = _pendingReq;
                    Flash("The host did not answer", now, always: true);
                }
            }
            else if (_timedOutReq != 0 && link.ActionAck == _timedOutReq)
            {
                // The ack arrived after the timeout: the action happened, so the collision still has to be resampled.
                _timedOutReq = 0;
                if (link.ActionResult == 1) HandleResult(1, now);
            }

            if (_resampleAtMs != long.MinValue && now >= _resampleAtMs && map != null)
            {
                _resampleAtMs = long.MinValue;
                terrain.Invalidate(feetUk, map.ToUkLength(8f));
            }

            UpdateLabel(now);
        }

        /// <summary>Guest-drawn prompt: forced by config, or automatic when the host draws none.</summary>
        private bool ShowPrompt
        {
            get
            {
                string v = (BridgeConfig.ShowGuestPrompt.Value ?? "Auto").Trim();
                if (v.Equals("true", System.StringComparison.OrdinalIgnoreCase)) return true;
                if (v.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return false;
                return !_hostDrawsPrompt;
            }
        }

        // ---- equipment, ping, held keys (ErmcGuestRequests) ----------------------------------------

        private void TickGuestRequests(GuestLink link, bool driving)
        {
            var ik = BridgeConfig.InteractKey.Value;
            if (ik != _labelKey)
            {
                _labelKey = ik;
                link.SetInteractKeyLabel(KeyName(ik));
            }
            var eq = BridgeConfig.EquipmentKey.Value;
            var ping = BridgeConfig.PingKey.Value;
            uint held = 0;
            if (driving)
            {
                if (Input.GetKey(ik)) held |= Protocol.HeldInteract;
                if (eq != KeyCode.None)
                {
                    if (Input.GetKey(eq)) held |= Protocol.HeldEquipment;
                    if (Input.GetKeyDown(eq)) link.RequestEquipment();
                }
                if (ping != KeyCode.None)
                {
                    if (Input.GetKey(ping)) held |= Protocol.HeldPing;
                    if (Input.GetKeyDown(ping)) link.RequestPing();
                }
            }
            link.SetHeldKeys(held);
            link.FlushGuestRequests();
        }

        /// <summary>Short name of a key for the host's glyph ("V", "MMB", "F8").</summary>
        internal static string KeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Mouse0: return "LMB";
                case KeyCode.Mouse1: return "RMB";
                case KeyCode.Mouse2: return "MMB";
                case KeyCode.Mouse3: return "M4";
                case KeyCode.Mouse4: return "M5";
                case KeyCode.None: return "";
            }
            string n = k.ToString();
            if (n.StartsWith("Alpha")) return n.Substring(5);
            if (n.StartsWith("Keypad")) return "Num" + n.Substring(6);
            return n;
        }

        private void HandleResult(int result, long now)
        {
            switch (result)
            {
                case 1:
                    _performed++;
                    _resampleAtMs = now + ResampleDelayMs;
                    break;
                case 0:
                    if (!_repeating) Flash("Nothing to interact with here", now);
                    break;
                case -2:
                    Flash($"Ladders need the host's controls ({BridgeConfig.SwitchKey.Value})", now, always: true);
                    break;
                default:
                    Flash("Not supported here", now, always: true);
                    break;
            }
        }

        private void Flash(string text, long now, bool always = false)
        {
            if (!always && !ShowPrompt) return;
            _flash = text;
            _flashUntilMs = now + FlashMs;
        }

        // ---- HUD label on ULTRAKILL's overlay canvas (captured into the host's GUI layer) ----------

        private void UpdateLabel(long now)
        {
            string text = now < _flashUntilMs ? _flash
                : string.IsNullOrEmpty(_prompt) || !ShowPrompt ? "" : PromptText();
            if (text == _shown && _label != null) return;
            if (!EnsureLabel()) return;
            _shown = text;
            _label.text = text;
            _label.enabled = text.Length > 0;
        }

        private string PromptText()
        {
            var key = BridgeConfig.InteractKey.Value;
            if (_prompt != _promptCacheSrc || key != _promptCacheKey || _promptCacheText.Length == 0)
            {
                _promptCacheSrc = _prompt;
                _promptCacheKey = key;
                _promptCacheText = $"[{key}]  {_prompt}";
            }
            return _promptCacheText;
        }

        private bool EnsureLabel()
        {
            if (_label != null) return true;
            if (!MonoSingleton<CanvasController>.TryGetInstance(out var canvas) || canvas == null) return false;
            var go = new GameObject("UKBridge Host Prompt", typeof(RectTransform));
            go.layer = canvas.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas.transform, false);
            rt.anchorMin = new Vector2(0.5f, 0.2f);
            rt.anchorMax = new Vector2(0.5f, 0.2f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1200f, 60f);
            _label = go.AddComponent<Text>();
            _label.font = LoadFont();
            _label.fontSize = 30;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = Color.white;
            _label.raycastTarget = false;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(2f, -2f);
            _shown = null;
            return true;
        }

        private static Font LoadFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            if (f == null) try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            return f;
        }
    }
}
