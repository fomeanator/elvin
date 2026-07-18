using System;
using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The player's progress as an ABSOLUTE: one bundle holding everything a
    /// player would rage-quit over losing — chapter markers (id + number +
    /// furthest reached), the chosen name, wardrobe equips and the encountered
    /// collection, CG unlocks. Kept in THREE homes:
    ///   1. the live PlayerPrefs stores (the working copies),
    ///   2. an atomically-written JSON file in persistentDataPath (survives a
    ///      corrupted prefs blob),
    ///   3. the server's durable state store under the "__progress" scope
    ///      (survives the device, keyed by the double-homed user id).
    /// The host syncs the bundle at every progress-moving moment and restores
    /// it on boot when the local stores are VIRGIN — so a lost prefs file or a
    /// reinstalled app (same account/id) comes back exactly where it left off.
    /// Restore never overwrites live progress: markers only move forward
    /// (reached is monotonic; current restores only onto emptiness).
    /// </summary>
    internal static class ProgressVault
    {
        public const string Scope = "__progress";
        private const int Version = 1;

        private static string FilePath =>
            System.IO.Path.Combine(Application.persistentDataPath, "lvn_progress.json");

        /// <summary>Snapshot every progress store into one bundle.</summary>
        public static JObject Collect(LvnManifest manifest)
        {
            var titles = new JObject();
            if (manifest?.titles != null)
                foreach (var t in manifest.titles)
                {
                    if (t == null || string.IsNullOrEmpty(t.id)) continue;
                    var cur = LvnProgress.Current(t);
                    int reached = LvnProgress.Reached(t);
                    if (cur == null && reached <= 0) continue;
                    titles[t.id] = new JObject
                    {
                        ["cur"] = cur?.id,
                        ["num"] = cur?.number ?? 0,
                        ["reached"] = reached,
                    };
                    var unlocked = LvnGalleryStore.Unlocked(t.id);
                    if (unlocked != null && unlocked.Count > 0)
                        ((JObject)titles[t.id])["gallery"] = new JArray(unlocked);
                }

            var wardrobe = new JObject();
            if (manifest?.sprites != null)
                foreach (var kv in manifest.sprites)
                {
                    if (kv.Value?.wardrobe == null || kv.Value.wardrobe.Count == 0) continue;
                    var worn = LvnWardrobe.Equipped(kv.Key);
                    var seen = LvnWardrobe.SeenDump(kv.Key);
                    if (worn.Count == 0 && seen.Count == 0) continue;
                    var ent = new JObject();
                    if (worn.Count > 0)
                    {
                        var w = new JObject();
                        foreach (var a in worn) w[a.Key] = a.Value;
                        ent["worn"] = w;
                    }
                    if (seen.Count > 0)
                    {
                        var sn = new JObject();
                        foreach (var a in seen) sn[a.Key] = new JArray(a.Value);
                        ent["seen"] = sn;
                    }
                    wardrobe[kv.Key] = ent;
                }

            return new JObject
            {
                ["v"] = Version,
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["name"] = LvnPrefs.PlayerName,
                ["titles"] = titles,
                ["wardrobe"] = wardrobe,
            };
        }

        /// <summary>Write the bundle to its file home — atomically (tmp +
        /// rename), so a kill mid-write never leaves a half-bundle.</summary>
        public static void WriteLocal(JObject bundle)
        {
            try
            {
                var tmp = FilePath + ".tmp";
                System.IO.File.WriteAllText(tmp, bundle.ToString(Newtonsoft.Json.Formatting.None));
                if (System.IO.File.Exists(FilePath)) System.IO.File.Delete(FilePath);
                System.IO.File.Move(tmp, FilePath);
            }
            catch (Exception e) { Debug.LogWarning("[vault] local write failed: " + e.Message); }
        }

        public static JObject ReadLocal()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return null;
                return JObject.Parse(System.IO.File.ReadAllText(FilePath));
            }
            catch { return null; } // a broken file must never break the boot
        }

        /// <summary>No marker, no name, nothing reached — the local stores hold
        /// no progress worth protecting; a restore cannot lose anything.</summary>
        public static bool IsVirgin(LvnManifest manifest)
        {
            if (!string.IsNullOrEmpty(LvnPrefs.PlayerName)) return false;
            if (manifest?.titles != null)
                foreach (var t in manifest.titles)
                    if (t != null && (LvnProgress.Current(t) != null || LvnProgress.Reached(t) > 0))
                        return false;
            return true;
        }

        /// <summary>Re-plant a bundle into the live stores. Additive and
        /// forward-only: reached maxes, current lands only where none exists,
        /// wardrobe/gallery/name merge in — live progress is never regressed.</summary>
        public static void Apply(JObject bundle, LvnManifest manifest)
        {
            if (bundle == null || manifest == null) return;
            try
            {
                var name = (string)bundle["name"];
                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(LvnPrefs.PlayerName))
                    LvnPrefs.PlayerName = name;

                if (bundle["titles"] is JObject titles && manifest.titles != null)
                    foreach (var t in manifest.titles)
                    {
                        if (t == null || !(titles[t.id] is JObject entry)) continue;
                        int reached = (int?)entry["reached"] ?? 0;
                        string curId = (string)entry["cur"];
                        int num = (int?)entry["num"] ?? 0;
                        // resolve against the LIVE manifest: id first, number as
                        // the rename-resilient fallback
                        LvnChapter resolved = null;
                        if (t.seasons != null)
                            foreach (var s in t.seasons)
                                if (s?.chapters != null)
                                    foreach (var c in s.chapters)
                                        if (c != null && (c.id == curId || (resolved == null && num > 0 && c.number == num)))
                                        { resolved = c; if (c.id == curId) break; }
                        bool hasLive = LvnProgress.Current(t) != null;
                        LvnProgress.RestoreMarker(t.id,
                            hasLive ? null : resolved?.id,
                            resolved?.number ?? num, reached);
                        if (entry["gallery"] is JArray cg)
                            foreach (var g in cg)
                            { var id = (string)g; if (!string.IsNullOrEmpty(id)) LvnGalleryStore.Unlock(t.id, id); }
                    }

                if (bundle["wardrobe"] is JObject wardrobe)
                    foreach (var prop in wardrobe.Properties())
                    {
                        var ent = prop.Value as JObject;
                        if (ent == null) continue;
                        if (ent["worn"] is JObject worn)
                            foreach (var a in worn.Properties())
                                if (!LvnWardrobe.Equipped(prop.Name).ContainsKey(a.Name))
                                    LvnWardrobe.Equip(prop.Name, a.Name, (string)a.Value);
                        if (ent["seen"] is JObject seen)
                            foreach (var a in seen.Properties())
                                if (a.Value is JArray vals)
                                    foreach (var v in vals)
                                        LvnWardrobe.MarkSeen(prop.Name, a.Name, (string)v);
                    }
            }
            catch (Exception e) { Debug.LogWarning("[vault] apply failed: " + e.Message); }
        }
    }
}
