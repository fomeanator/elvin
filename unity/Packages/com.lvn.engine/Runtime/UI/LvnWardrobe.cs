using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// What the player has EQUIPPED, per character: entity → axis → value,
    /// device-level in PlayerPrefs (like <see cref="LvnPrefs"/> — the player's
    /// choice, not per-title story state). Ownership is NOT here: bought items
    /// live in the server wallet's sku inventory (<see cref="Sku"/> is the
    /// bridge). The stage overlays these values onto any axis the script left
    /// unset, so an explicit <c>actor hero armor=chain</c> still wins — the
    /// writer can force a story costume.
    /// </summary>
    public static class LvnWardrobe
    {
        /// <summary>Raised after an equip/unequip, with the entity id — the
        /// stage re-applies that actor live if it's on screen.</summary>
        public static event Action<string> Changed;

        private const string P = "lvn_wardrobe_";

        // entity → axis → value, loaded lazily per entity.
        private static readonly Dictionary<string, Dictionary<string, string>> _cache
            = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>The wallet inventory sku for a wardrobe item — deterministic,
        /// so ownership survives reinstalls via the server wallet.</summary>
        public static string Sku(string entity, string axis, string value)
            => $"wardrobe:{entity}:{axis}:{value}";

        /// <summary>The equipped values for an entity (axis → value). Never
        /// null; empty when nothing is equipped.</summary>
        public static IReadOnlyDictionary<string, string> Equipped(string entity)
            => Load(entity);

        /// <summary>Equip an axis value (null/empty value = take the slot off).
        /// Persists and raises <see cref="Changed"/>.</summary>
        public static void Equip(string entity, string axis, string value)
        {
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(axis)) return;
            var map = Load(entity);
            bool remove = string.IsNullOrEmpty(value);
            if (remove ? !map.Remove(axis) : (map.TryGetValue(axis, out var cur) && cur == value))
                return; // no change
            if (!remove) map[axis] = value;
            Persist(entity, map);
            Debug.Log($"[lvn-wardrobe] equip {entity}.{axis} = {(remove ? "(off)" : value)}");
            Changed?.Invoke(entity);
        }

        // ── try-on preview: not persisted, wins over equipped ────────────────
        // The in-story wardrobe sheet dresses the LIVE actor while the player
        // browses: previewed values overlay the committed ones until the sheet
        // confirms (→ Equip) or cancels (→ ClearPreview).
        private static readonly Dictionary<string, Dictionary<string, string>> _previews
            = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>Try a value on (preview only; null value clears that axis'
        /// preview). Raises <see cref="Changed"/> so the stage re-dresses.</summary>
        public static void Preview(string entity, string axis, string value)
        {
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(axis)) return;
            if (!_previews.TryGetValue(entity, out var map))
                _previews[entity] = map = new Dictionary<string, string>();
            bool remove = string.IsNullOrEmpty(value);
            if (remove ? !map.Remove(axis) : (map.TryGetValue(axis, out var cur) && cur == value))
                return;
            if (!remove) map[axis] = value;
            Changed?.Invoke(entity);
        }

        /// <summary>Current previewed values for an entity (axis → value).</summary>
        public static IReadOnlyDictionary<string, string> Previewed(string entity)
            => !string.IsNullOrEmpty(entity) && _previews.TryGetValue(entity, out var map)
                ? map : (IReadOnlyDictionary<string, string>)_empty;
        private static readonly Dictionary<string, string> _empty = new Dictionary<string, string>();

        /// <summary>Drop every preview for an entity (sheet closed) — the actor
        /// snaps back to what's actually equipped.</summary>
        public static void ClearPreview(string entity)
        {
            if (string.IsNullOrEmpty(entity) || !_previews.Remove(entity)) return;
            Changed?.Invoke(entity);
        }

        /// <summary>Overlay the previewed + equipped values onto a command's axes
        /// IN PLACE. Committed EQUIPPED only fills axes the command left unset. A live
        /// PREVIEW fills an unset axis too, and overrides an axis the script pinned
        /// ONLY when that axis is <paramref name="previewOverridable"/> — i.e. it was
        /// itself driven by a story variable (a template like
        /// <c>{Wardrobe.mainCh_Clothes}</c>). So a story-forced literal costume
        /// (<c>actor hero armor=chain</c>) still wins over a try-on, but the imported
        /// protagonist's variable-driven outfit updates live while she's being dressed.
        /// Pure; the stage calls it from its axis resolve.</summary>
        public static void MergeInto(Dictionary<string, string> axes, string entity,
            ICollection<string> previewOverridable = null)
        {
            if (axes == null) return;
            foreach (var kv in Previewed(entity))
                if (!string.IsNullOrEmpty(kv.Value)
                    && (!axes.ContainsKey(kv.Key)
                        || (previewOverridable != null && previewOverridable.Contains(kv.Key))))
                    axes[kv.Key] = kv.Value;
            foreach (var kv in Load(entity))
                if (!axes.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    axes[kv.Key] = kv.Value;
        }

        // ── encountered along the way ────────────────────────────────────────
        // The always-open wardrobe shows what the story has ACTUALLY put in the
        // player's path: every axis value an actor has been staged with, plus
        // everything an in-story wardrobe moment offered. Device-persisted like
        // the equips — progression through outfits survives restarts.
        private static readonly Dictionary<string, Dictionary<string, HashSet<string>>> _seen
            = new Dictionary<string, Dictionary<string, HashSet<string>>>();
        private const string PSeen = "lvn_wardrobe_seen_";

        /// <summary>Record that an outfit value crossed the player's path
        /// (an actor wore it, or a story wardrobe offered it).</summary>
        public static void MarkSeen(string entity, string axis, string value)
        {
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(axis) || string.IsNullOrEmpty(value))
                return;
            var map = LoadSeen(entity);
            if (!map.TryGetValue(axis, out var set)) map[axis] = set = new HashSet<string>();
            if (!set.Add(value)) return; // known — no prefs churn
            PersistSeen(entity, map);
        }

        /// <summary>Everything this entity has encountered, axis → values —
        /// the progress vault serialises it for the server-side backup.</summary>
        public static Dictionary<string, List<string>> SeenDump(string entity)
        {
            var outp = new Dictionary<string, List<string>>();
            foreach (var kv in LoadSeen(entity))
                outp[kv.Key] = new List<string>(kv.Value);
            return outp;
        }

        /// <summary>Has this outfit value crossed the player's path?</summary>
        public static bool IsSeen(string entity, string axis, string value)
            => !string.IsNullOrEmpty(entity) && !string.IsNullOrEmpty(axis) && !string.IsNullOrEmpty(value)
               && LoadSeen(entity).TryGetValue(axis, out var set) && set.Contains(value);

        private static Dictionary<string, HashSet<string>> LoadSeen(string entity)
        {
            if (_seen.TryGetValue(entity, out var map)) return map;
            map = new Dictionary<string, HashSet<string>>();
            var json = PlayerPrefs.GetString(PSeen + entity, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var doc = Newtonsoft.Json.Linq.JObject.Parse(json);
                    foreach (var p in doc.Properties())
                    {
                        var set = new HashSet<string>();
                        if (p.Value is Newtonsoft.Json.Linq.JArray arr)
                            foreach (var v in arr) { var s = (string)v; if (!string.IsNullOrEmpty(s)) set.Add(s); }
                        if (set.Count > 0) map[p.Name] = set;
                    }
                }
                catch { /* corrupt prefs → start empty */ }
            }
            _seen[entity] = map;
            return map;
        }

        private static void PersistSeen(string entity, Dictionary<string, HashSet<string>> map)
        {
            var doc = new Newtonsoft.Json.Linq.JObject();
            foreach (var kv in map)
            {
                var arr = new Newtonsoft.Json.Linq.JArray();
                foreach (var v in kv.Value) arr.Add(v);
                doc[kv.Key] = arr;
            }
            PlayerPrefs.SetString(PSeen + entity, doc.ToString(Newtonsoft.Json.Formatting.None));
            PlayerPrefs.Save();
        }

        /// <summary>Forget an entity's equipment (tests / profile reset).</summary>
        public static void Clear(string entity)
        {
            if (string.IsNullOrEmpty(entity)) return;
            _cache.Remove(entity);
            _seen.Remove(entity);
            PlayerPrefs.DeleteKey(P + entity);
            PlayerPrefs.DeleteKey(PSeen + entity);
            PlayerPrefs.Save();
            Changed?.Invoke(entity);
        }

        private static Dictionary<string, string> Load(string entity)
        {
            if (string.IsNullOrEmpty(entity)) return new Dictionary<string, string>();
            if (_cache.TryGetValue(entity, out var map)) return map;
            map = new Dictionary<string, string>();
            var json = PlayerPrefs.GetString(P + entity, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var doc = Newtonsoft.Json.Linq.JObject.Parse(json);
                    foreach (var p in doc.Properties())
                    {
                        var v = (string)p.Value;
                        if (!string.IsNullOrEmpty(v)) map[p.Name] = v;
                    }
                }
                catch { /* corrupt prefs → start empty */ }
            }
            _cache[entity] = map;
            return map;
        }

        private static void Persist(string entity, Dictionary<string, string> map)
        {
            var doc = new Newtonsoft.Json.Linq.JObject();
            foreach (var kv in map) doc[kv.Key] = kv.Value;
            PlayerPrefs.SetString(P + entity, doc.ToString(Newtonsoft.Json.Formatting.None));
            PlayerPrefs.Save();
        }
    }
}
