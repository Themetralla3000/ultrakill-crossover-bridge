using System.Collections.Generic;

namespace UltrakillBridge.Guest.Combat
{
    /// <summary>One aggregated stat entry: the hits of one (weapon, weak point, shot, kind) on one target.</summary>
    internal struct StatEntry
    {
        public int Weapon, ShotSeq, Kind, Count;
        public bool Head;
        /// <summary>Sum of the ULTRAKILL base damage (before the head bonus).</summary>
        public float Uk;
    }

    /// <summary>
    /// Pending stat hits of one target. Hits with the same key (weapon, head, shotSeq, kind) merge into one entry, so a
    /// shotgun blast is one entry per tick and the host rolls crit once per shot. Unity-free.
    /// </summary>
    internal sealed class StatAggregator
    {
        public const int MaxEntries = 256;
        private readonly List<StatEntry> _list = new List<StatEntry>(8);

        public int Count => _list.Count;
        public StatEntry this[int i] => _list[i];

        public void Add(int weapon, bool head, int shotSeq, int kind, float uk)
        {
            if (!(uk > 0f) || float.IsInfinity(uk)) return;
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                var e = _list[i];
                if (e.Weapon != weapon || e.Head != head || e.ShotSeq != shotSeq || e.Kind != kind) continue;
                if (e.Count < 255) e.Count++;
                e.Uk += uk;
                _list[i] = e;
                return;
            }
            if (_list.Count >= MaxEntries) _list.RemoveAt(0);
            _list.Add(new StatEntry { Weapon = weapon, Head = head, ShotSeq = shotSeq, Kind = kind, Count = 1, Uk = uk });
        }

        /// <summary>Forgets the first <paramref name="n"/> entries (they were sent).</summary>
        public void RemoveFirst(int n)
        {
            if (n >= _list.Count) _list.Clear(); else if (n > 0) _list.RemoveRange(0, n);
        }

        public void Clear() => _list.Clear();
    }
}
