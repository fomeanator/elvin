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
        private static string Key(string titleId) => LvnKeep.Scoped("lvn.gallery.", titleId);

        /// <summary>The set of unlocked item ids for a title (a fresh copy).</summary>
        public static HashSet<string> Unlocked(string titleId)
        {
            var json = LvnKeep.Get(Key(titleId), "");
            if (string.IsNullOrEmpty(json)) return new HashSet<string>();
            try { return JsonConvert.DeserializeObject<HashSet<string>>(json) ?? new HashSet<string>(); }
            catch { return new HashSet<string>(); }
        }

        public static bool IsUnlocked(string titleId, string itemId) =>
            !string.IsNullOrEmpty(itemId) && Unlocked(titleId).Contains(itemId);

        /// <summary>Unlock an item; returns true when it was newly unlocked.</summary>
        public static bool Unlock(string titleId, string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            var set = Unlocked(titleId);
            if (!set.Add(itemId)) return false;
            LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(set));
            return true;
        }

        /// <summary>Forget every unlock for a title (debug / "reset progress").</summary>
        public static void Clear(string titleId)
        {
            LvnKeep.Drop(Key(titleId));
        }
    }
}
