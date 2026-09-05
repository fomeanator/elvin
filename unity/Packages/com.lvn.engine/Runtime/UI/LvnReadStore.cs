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
        static LvnReadStore()
        {
            // Уход в фон — самый частый способ закрыть игру на телефоне, и
            // накопленное между фиксациями обязано уехать в книжку ВМЕСТЕ с
            // ней. Без этой подписки экономия на горячем пути обернулась бы
            // потерей нескольких последних отметок — того самого, что она
            // экономить не должна.
            Application.focusChanged += focused => { if (!focused) FlushNow(); };
            Application.quitting += FlushNow;
        }

        private static string Key(string titleId) => LvnKeep.Scoped("lvn.read.", titleId);

        // One live set per title; loaded lazily, written back coalesced.
        private static readonly Dictionary<string, HashSet<ulong>> _cache
            = new Dictionary<string, HashSet<ulong>>();
        // ГОТОВАЯ СТРОКА РЯДОМ С НАБОРОМ — и это не кэш ради кэша.
        //
        // Строка собиралась заново ИЗ ВСЕГО набора на каждой прочитанной
        // реплике, а набор растёт по всей новелле, а не по главе. Замер 05.09:
        // 1000 реплик — 1206 мс, 3000 — 4070 мс, 9000 — 17756 мс, то есть от
        // 1,2 до 2,0 мс на КАЖДЫЙ тап, на маке с SSD; на телефоне дороже.
        // Теперь новый хэш дописывается в хвост (амортизированно постоянная
        // цена), а в записную книжку строка уходит вместе с фиксацией — раз в
        // SaveEvery реплик, а не каждую.
        private static readonly Dictionary<string, System.Text.StringBuilder> _text
            = new Dictionary<string, System.Text.StringBuilder>();
        private static readonly HashSet<string> _dirty = new HashSet<string>();
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
            var sb = new System.Text.StringBuilder(raw ?? "");
            _text[key] = sb;
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
            var key = Key(titleId);
            var sb = _text[key];
            if (sb.Length > 0) sb.Append(',');
            sb.Append(Hash(who, text).ToString("x"));
            _dirty.Add(key);
            // Карандашом и НЕ КАЖДУЮ РЕПЛИКУ: строка со всем прочитанным
            // отдаётся книжке вместе с фиксацией. Между фиксациями она живёт в
            // памяти — ровно те же несколько реплик, которые и так объявлены
            // допустимой потерей (SaveEvery), и ни одной больше.
            if (++_sinceSave >= SaveEvery)
            {
                _sinceSave = 0;
                WriteDirty();
                LvnKeep.Flush();
            }
            return true;
        }

        /// <summary>Отдать книжке всё накопленное. Зовётся при фиксации и при
        /// любом чтении наружу, которому нужна согласованность с диском.</summary>
        private static void WriteDirty()
        {
            if (_dirty.Count == 0) return;
            foreach (var key in _dirty)
                if (_text.TryGetValue(key, out var sb))
                    LvnKeep.Jot(key, sb.ToString());
            _dirty.Clear();
        }

        /// <summary>How many distinct lines this title has recorded as read.</summary>
        public static int ReadCount(string titleId) => Load(titleId).Count;

        /// <summary>Forget a title's read history ("reset progress").</summary>
        public static void Clear(string titleId)
        {
            var key = Key(titleId);
            _cache.Remove(key);
            _text.Remove(key);
            _dirty.Remove(key);
            LvnKeep.Drop(key);
        }

        /// <summary>Немедленно записать накопленное — для хоста, который уходит
        /// в фон или закрывается раньше, чем набежит SaveEvery.</summary>
        public static void FlushNow()
        {
            WriteDirty();
            LvnKeep.Flush();
        }
    }
}
