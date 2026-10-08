using System;
using System.Globalization;
using System.IO;
using System.Text;
using UltrakillBridge.Link;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>
    /// Measurement probe ([Combat] HitLog = true): appends every hit on a host enemy proxy to
    /// &lt;bridge dir&gt;/hitlog.csv. Buffered (flushed about once a second and on exit). Summarise it with
    /// scripts/hitlog-summary.ps1. The log never affects behaviour.
    /// </summary>
    internal static class HitLog
    {
        public const string FileName = "hitlog.csv";
        private const string Header =
            "t_ms,hitter,hitterWeapon,sourceName,sourceType,variation,weaponId,weapon,target,rawMultiplier,critMultiplier,limb,weakpoint,fromExplosion,tryForExplode,shotSeq,kind,ukDamage";

        private static readonly object Gate = new object();
        private static readonly StringBuilder Buf = new StringBuilder(8192);
        private static string _path;
        private static bool _failed, _hooked;
        private static long _lastFlushMs;

        public static void Record(string hitter, string lastWeapon, string sourceName, string sourceType, int variation,
            int weaponId, ulong target, float rawMultiplier, float critMultiplier, string limb, bool weakpoint,
            bool fromExplosion, bool tryForExplode, int shotSeq, int kind, float ukDamage)
        {
            if (_failed) return;
            var ci = CultureInfo.InvariantCulture;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (Gate)
            {
                if (!_hooked) { _hooked = true; AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush(true); }
                Buf.Append(now.ToString(ci)).Append(',')
                   .Append(Esc(hitter)).Append(',').Append(Esc(lastWeapon)).Append(',').Append(Esc(sourceName)).Append(',')
                   .Append(Esc(sourceType)).Append(',').Append(variation.ToString(ci)).Append(',')
                   .Append(weaponId.ToString(ci)).Append(',').Append(WeaponId.NameOf(weaponId)).Append(',')
                   .Append(target.ToString("X", ci)).Append(',')
                   .Append(rawMultiplier.ToString("0.####", ci)).Append(',').Append(critMultiplier.ToString("0.####", ci)).Append(',')
                   .Append(limb).Append(',').Append(weakpoint ? '1' : '0').Append(',').Append(fromExplosion ? '1' : '0').Append(',')
                   .Append(tryForExplode ? '1' : '0').Append(',').Append(shotSeq.ToString(ci)).Append(',')
                   .Append(kind.ToString(ci)).Append(',').Append(ukDamage.ToString("0.####", ci)).Append('\n');
                if (Buf.Length > 32768) FlushLocked();
            }
        }

        /// <summary>Writes the buffer when it is older than about a second (or always with <paramref name="force"/>).</summary>
        public static void Flush(bool force)
        {
            lock (Gate)
            {
                if (Buf.Length == 0) return;
                long now = Environment.TickCount;
                if (!force && now - _lastFlushMs < 1000) return;
                FlushLocked();
            }
        }

        private static void FlushLocked()
        {
            _lastFlushMs = Environment.TickCount;
            try
            {
                if (_path == null) _path = BridgePaths.File(FileName);
                bool fresh = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                using (var w = new StreamWriter(_path, true, new UTF8Encoding(false)))
                {
                    if (fresh) w.Write(Header + "\n");
                    w.Write(Buf.ToString());
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("Hit log disabled (could not write " + _path + "): " + e.Message);
            }
            Buf.Length = 0;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
