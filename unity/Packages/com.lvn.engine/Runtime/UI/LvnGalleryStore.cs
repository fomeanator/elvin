using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Persistent CG-gallery unlocks, namespaced per title so two novels in one
    /// app never see each other's art. PlayerPrefs-backed like the save store:
    /// an unlock is meta-progress that must survive deleted saves and new
    /// playthroughs — "seen once" means "seen forever".
    /// </summary>
    public static class LvnGalleryStore
    {
        static LvnGalleryStore()
        {
            // Игрока забыли — забываем и мы (см. LvnKeep.ForgetPlayerData).
            LvnKeep.Wiped += () => { _cached = null; _cachedKey = null; };
        }

        private static string Key(string titleId) => LvnKeep.Scoped("lvn.gallery.", titleId);

        // ЖИВОЙ НАБОР ОДНОЙ НОВЕЛЛЫ — и почему он кэшируется.
        //
        // Экран галереи спрашивает про КАЖДУЮ карточку отдельно, а каждый
        // вопрос читал строку из книжки и разбирал весь набор заново. Замер
        // 05.09: 50 карточек — 1 мс, 200 — 15 мс, 500 — 88 мс, то есть цена
        // росла квадратом от наполнения галереи. Кэш держится по КЛЮЧУ, а не по
        // имени новеллы: ключ включает владельца данных, поэтому смена аккаунта
        // сама даёт другой кэш и чужое открытое не покажет.
        private static string _cachedKey;
        private static HashSet<string> _cached;

        private static HashSet<string> Live(string titleId)
        {
            var key = Key(titleId);
            if (_cachedKey == key && _cached != null) return _cached;
            var set = new HashSet<string>();
            var json = LvnKeep.Get(key, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { set = JsonConvert.DeserializeObject<HashSet<string>>(json) ?? new HashSet<string>(); }
                catch { set = new HashSet<string>(); }
            }
            _cachedKey = key;
            _cached = set;
            return set;
        }

        /// <summary>The set of unlocked item ids for a title (a fresh copy).</summary>
        public static HashSet<string> Unlocked(string titleId) => new HashSet<string>(Live(titleId));

        public static bool IsUnlocked(string titleId, string itemId) =>
            !string.IsNullOrEmpty(itemId) && Live(titleId).Contains(itemId);

        /// <summary>Unlock an item; returns true when it was newly unlocked.</summary>
        public static bool Unlock(string titleId, string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            var set = Live(titleId);
            if (!set.Add(itemId)) return false;
            LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(set));
            return true;
        }

        /// <summary>Forget every unlock for a title (debug / "reset progress").</summary>
        public static void Clear(string titleId)
        {
            var key = Key(titleId);
            LvnKeep.Drop(key);
            if (_cachedKey == key) { _cached = null; _cachedKey = null; }
        }
    }
}
