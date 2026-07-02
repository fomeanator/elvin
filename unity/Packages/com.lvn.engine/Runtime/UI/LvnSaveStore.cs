using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>One persisted save slot: the player snapshot plus the display
    /// metadata a save/load UI shows (when, where, the last line read).</summary>
    public sealed class LvnSaveSlot
    {
        /// <summary>The slot schema this build reads and writes. Bump it when
        /// <see cref="LvnPlayer.LvnSnapshot"/> (or this class) changes meaning,
        /// and teach <see cref="LvnSaveStore.Migrate"/> the upgrade.</summary>
        public const int CurrentVersion = 1;

        /// <summary>Slot schema version. Older slots are migrated on read;
        /// slots from a NEWER build than this one are refused (a downgraded
        /// install must not misread them into corrupt state).</summary>
        public int Version = CurrentVersion;
        public LvnPlayer.LvnSnapshot Snap;
        public long SavedAtUnixMs;
        public string ChapterId;
        public string Preview; // the last dialogue line at save time
    }

    /// <summary>
    /// Disk-backed save slots, namespaced per title so two novels on one device
    /// never see each other's saves. PlayerPrefs-backed (like the stat store) —
    /// survives restarts on every platform without file-permission concerns.
    /// Slots are small (a cursor anchor + variables), so a title's whole slot
    /// map serializes as one JSON blob.
    /// </summary>
    public static class LvnSaveStore
    {
        /// <summary>The slot name the engine autosaves into.</summary>
        public const string AutoSlot = "auto";

        private static string Key(string titleId) =>
            "lvn_slots_" + (string.IsNullOrEmpty(titleId) ? "default" : titleId);

        /// <summary>All of a title's slots (name → slot). Never null. Every slot
        /// is version-gated: older schemas are migrated up, a newer build's slots
        /// are dropped from the view (never misread, never deleted — an upgrade
        /// back makes them loadable again).</summary>
        public static Dictionary<string, LvnSaveSlot> Slots(string titleId)
        {
            var ok = new Dictionary<string, LvnSaveSlot>();
            foreach (var kv in Raw(titleId))
            {
                var s = Migrate(kv.Value);
                if (s != null) ok[kv.Key] = s;
                else Debug.LogWarning("[lvn] slot '" + kv.Key + "' is schema v" + kv.Value?.Version +
                                      " from a newer build — hidden until the app updates");
            }
            return ok;
        }

        // The store as persisted, no version gate — the WRITE path works on this
        // so a hidden newer-schema slot survives unrelated Put/Delete round-trips.
        private static Dictionary<string, LvnSaveSlot> Raw(string titleId)
        {
            var json = PlayerPrefs.GetString(Key(titleId), "");
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, LvnSaveSlot>();
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, LvnSaveSlot>>(json)
                       ?? new Dictionary<string, LvnSaveSlot>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[lvn] save slots unreadable (" + e.Message + ") — starting empty");
                return new Dictionary<string, LvnSaveSlot>();
            }
        }

        /// <summary>Bring a slot up to <see cref="LvnSaveSlot.CurrentVersion"/>.
        /// Returns null for slots written by a NEWER schema than this build knows —
        /// the one case where reading would corrupt state. When the schema grows,
        /// add the vN→vN+1 steps here (each save re-persists at the current
        /// version on the next <see cref="Put"/>).</summary>
        private static LvnSaveSlot Migrate(LvnSaveSlot s)
        {
            if (s == null) return null;
            if (s.Version > LvnSaveSlot.CurrentVersion) return null;
            // v1 is the first schema — pre-version slots deserialize as v1 (the
            // field initializer) and need no transformation. Future steps:
            //   if (s.Version == 1) { …upgrade…; s.Version = 2; }
            return s;
        }

        /// <summary>A single slot, or null when empty/unreadable.</summary>
        public static LvnSaveSlot Get(string titleId, string slot)
        {
            return Slots(titleId).TryGetValue(slot ?? "", out var s) ? s : null;
        }

        /// <summary>Write a slot (stamps <see cref="LvnSaveSlot.SavedAtUnixMs"/>
        /// and the current schema version).</summary>
        public static void Put(string titleId, string slot, LvnSaveSlot data)
        {
            if (string.IsNullOrEmpty(slot) || data == null) return;
            data.Version = LvnSaveSlot.CurrentVersion;
            data.SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var all = Raw(titleId);
            all[slot] = data;
            Write(titleId, all);
        }

        public static void Delete(string titleId, string slot)
        {
            var all = Raw(titleId);
            if (!all.Remove(slot ?? "")) return;
            Write(titleId, all);
        }

        private static void Write(string titleId, Dictionary<string, LvnSaveSlot> all)
        {
            try
            {
                PlayerPrefs.SetString(Key(titleId), JsonConvert.SerializeObject(all));
                PlayerPrefs.Save();
            }
            catch (Exception e) { Debug.LogWarning("[lvn] save write failed: " + e.Message); }
        }
    }
}
