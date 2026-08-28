using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Per-title "which lines has this player already seen" — the memory behind
    /// skip-read-only and any host-side seen-percentage UI. A line is keyed by a
    /// 64-bit FNV-1a hash of speaker + text, so it survives insertions around it
    /// and re-imports; editing the text itself makes the line new again (which is
    /// honest — the player hasn't read the new wording). Kept in the device
    /// notebook (LvnKeep) like
    /// the other meta-progress stores, with an in-memory cache because MarkRead
    /// runs on every rendered line.
    /// </summary>
    public static class LvnReadStore
    {
        private static string Key(string titleId) => $"lvn.read.{titleId ?? "default"}";

        // One live set per title; loaded lazily, written back coalesced.
        private static readonly Dictionary<string, HashSet<ulong>> _cache
            = new Dictionary<string, HashSet<ulong>>();
        private static int _sinceSave;
        private const int SaveEvery = 10; // реплик между фиксациями карандаша

        /// <summary>FNV-1a 64 over who + '\n' + text — the line's identity.</summary>
        public static ulong Hash(string who, string text)
        {
            const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
            ulong h = offset;
            void Mix(string s)
            {
                if (s == null) return;
                foreach (char c in s) { h ^= c; h *= prime; }
            }
            Mix(who);
            h ^= '\n'; h *= prime;
            Mix(text);
            return h;
        }

        private static HashSet<ulong> Load(string titleId)
        {
            var key = Key(titleId);
            if (_cache.TryGetValue(key, out var set)) return set;
            set = new HashSet<ulong>();
            var raw = LvnKeep.Get(key, "");
            if (!string.IsNullOrEmpty(raw))
                foreach (var part in raw.Split(','))
                    if (ulong.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var v))
                        set.Add(v);
            _cache[key] = set;
            return set;
        }

        public static bool IsRead(string titleId, string who, string text) =>
            Load(titleId).Contains(Hash(who, text));

        /// <summary>Remember a rendered line; returns true when it was new.
        /// Persists coalesced: every few lines, plus whenever the notebook
        /// flushes (app to background or quit).</summary>
        public static bool MarkRead(string titleId, string who, string text)
        {
            var set = Load(titleId);
            if (!set.Add(Hash(who, text))) return false;
            var sb = new System.Text.StringBuilder(set.Count * 17);
            foreach (var v in set)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(v.ToString("x"));
            }
            // Карандашом: реплика метится читанной на каждом клике, и полный
            // флаш на каждую был бы кадром в самом горячем месте игры.
            LvnKeep.Jot(Key(titleId), sb.ToString());
            if (++_sinceSave >= SaveEvery)
            {
                _sinceSave = 0;
                LvnKeep.Flush();
            }
            return true;
        }

        /// <summary>How many distinct lines this title has recorded as read.</summary>
        public static int ReadCount(string titleId) => Load(titleId).Count;

        /// <summary>Forget a title's read history ("reset progress").</summary>
        public static void Clear(string titleId)
        {
            _cache.Remove(Key(titleId));
            LvnKeep.Drop(Key(titleId));
        }
    }
}
