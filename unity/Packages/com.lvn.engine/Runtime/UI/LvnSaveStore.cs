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
        /// install must not misread them into corrupt state).
        ///
        /// <para>ИНИЦИАЛИЗАТОР — ЛИТЕРАЛЬНАЯ ЕДИНИЦА, И ЭТО НЕ СТИЛЬ. На
        /// устройстве игрока лежат сейвы прежних сборок, где поля Version нет
        /// вовсе: оно появилось позже самого хранилища. Такой блоб разбирается
        /// со значением ЭТОГО инициализатора. Стой здесь
        /// <see cref="CurrentVersion"/>, старые слоты объявляли бы себя
        /// новейшими — и <see cref="LvnSaveStore.Migrate"/> прошла бы мимо них
        /// молча, ровно в тот день, когда схему подняли и миграцию написали.
        /// Замерено: при <c>CurrentVersion = 2</c> сейв без версии читался как
        /// v2. Сторожит <c>SaveWrittenBeforeVersioningLoadsAsTheFirstSchema</c>,
        /// сверяясь с единицей, а не с текущей схемой.</para></summary>
        public int Version = 1;
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
            LvnKeep.Scoped("lvn_slots_", titleId);

        // ── thumbnails ───────────────────────────────────────────────────────
        // A small scene screenshot per manual slot, stored as a PNG FILE (images
        // don't belong in PlayerPrefs). Convention-addressed by title+slot, so
        // the slot schema stays untouched.

        /// <summary>The thumbnail file for a slot (may not exist).</summary>
        public static string ThumbPath(string titleId, string slot) =>
            System.IO.Path.Combine(Application.persistentDataPath, "lvn", "thumbs",
                string.IsNullOrEmpty(titleId) ? "default" : titleId, slot + ".png");

        /// <summary>Write (or, when <paramref name="thumb"/> is null, delete) a
        /// slot's thumbnail. A save with no fresh capture must not keep showing
        /// the previous save's scene. Never throws — a thumbnail is decoration.</summary>
        public static void WriteThumb(string titleId, string slot, Texture2D thumb)
        {
            try
            {
                var path = ThumbPath(titleId, slot);
                if (thumb == null)
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    return;
                }
                // Тоже атомарно: оборванная запись оставляла бы обрезанный PNG,
                // и слот показывал бы половину картинки вместо прежней целой.
                Lvn.Content.ContentLoader.AtomicWriteAllBytes(path, thumb.EncodeToPNG());
            }
            catch (Exception e) { Debug.LogWarning("[lvn] thumb write failed: " + e.Message); }
        }

        /// <summary>Load a slot's thumbnail, or null when absent/unreadable.
        /// The caller owns the returned texture (destroy it when the UI closes).</summary>
        public static Texture2D LoadThumb(string titleId, string slot)
        {
            try
            {
                var path = ThumbPath(titleId, slot);
                if (!System.IO.File.Exists(path)) return null;
                return Lvn.Content.AssetMemory.Decode(System.IO.File.ReadAllBytes(path));
            }
            catch { return null; }
        }

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
        /// <summary>Ключ запасной копии блока — рядом с основным.
        ///
        /// <para>Все слоты новеллы лежат ОДНОЙ строкой: один испорченный символ
        /// — и у игрока исчезают разом все сохранения, включая автосейв.
        /// Замерено прогоном (<c>ПорчаБлокаНеУноситВсеСохранения</c>): битый
        /// блок читался как пустой, и первая же следующая запись затирала его
        /// навсегда. Прогресс — докуда дошёл — живёт в трёх домах и это
        /// переживает; слоты жили в одном.</para></summary>
        private static string BackupKey(string titleId) => Key(titleId) + ".bak";

        private static Dictionary<string, LvnSaveSlot> Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonConvert.DeserializeObject<Dictionary<string, LvnSaveSlot>>(json); }
            catch { return null; }
        }

        private static Dictionary<string, LvnSaveSlot> Raw(string titleId)
        {
            var json = LvnKeep.Get(Key(titleId), "");
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, LvnSaveSlot>();
            var parsed = Parse(json);
            if (parsed != null) return parsed;

            // ОСНОВНОЙ БЛОК НЕ ЧИТАЕТСЯ — ПОДНИМАЕМ ЗАПАСНОЙ. Теряется при этом
            // максимум последняя запись, а не вся история прохождения.
            var spare = Parse(LvnKeep.Get(BackupKey(titleId), ""));
            if (spare != null && spare.Count > 0)
            {
                Debug.LogWarning("[lvn] блок сохранений не читается — поднял запасную копию ("
                               + spare.Count + " слот(ов); потеряна максимум последняя запись)");
                return spare;
            }
            Debug.LogWarning("[lvn] блок сохранений не читается, запасной копии нет — начинаю с пустого");
            return new Dictionary<string, LvnSaveSlot>();
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
            // field initializer is a literal 1 for exactly this reason, see
            // LvnSaveSlot.Version) and need no transformation. Future steps:
            //   if (s.Version == 1) { …upgrade…; s.Version = 2; }
            return s;
        }

        /// <summary>A single slot, or null when empty/unreadable.</summary>
        public static LvnSaveSlot Get(string titleId, string slot)
        {
            return Slots(titleId).TryGetValue(slot ?? "", out var s) ? s : null;
        }

        /// <summary>Write a slot (stamps <see cref="LvnSaveSlot.SavedAtUnixMs"/>
        /// and the current schema version). Returns false when the write failed
        /// (storage full/serialization error) so callers can tell the player
        /// instead of pretending the save exists.</summary>
        public static bool Put(string titleId, string slot, LvnSaveSlot data)
        {
            if (string.IsNullOrEmpty(slot) || data == null) return false;
            data.Version = LvnSaveSlot.CurrentVersion;
            data.SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var all = Raw(titleId);
            all[slot] = data;
            return Write(titleId, all);
        }

        public static void Delete(string titleId, string slot)
        {
            // Миниатюра уходит ВСЕГДА, даже если записи слота уже нет: PNG живёт
            // отдельным файлом, и «слот снесли, картинка осталась» — это и мусор
            // на диске, и кадр чужой игры, всплывающий в следующем сохранении.
            WriteThumb(titleId, slot, null);
            var all = Raw(titleId);
            if (!all.Remove(slot ?? "")) return;
            Write(titleId, all);
        }

        /// <summary>Снести все слоты новеллы вместе с их миниатюрами.
        /// Для забвения (<c>LvnForget</c>): перечислять слоты снаружи значит
        /// каждый раз вспоминать, что у слота есть ещё и файл.</summary>
        public static void DeleteAll(string titleId)
        {
            foreach (var slot in new List<string>(Raw(titleId).Keys))
                Delete(titleId, slot);
            LvnKeep.Drop(Key(titleId));
            // Забвение уносит и запас: иначе «удалить всё» оставляет сохранения
            // лежать под соседним ключом.
            LvnKeep.Drop(BackupKey(titleId));
        }

        private static bool Write(string titleId, Dictionary<string, LvnSaveSlot> all)
        {
            try
            {
                var json = JsonConvert.SerializeObject(all);
                LvnKeep.Put(Key(titleId), json);
                // ЗАПАС ПИШЕТСЯ ПОСЛЕ ОСНОВНОГО, А НЕ ДО.
                //
                // Первая редакция клала в запас ПРЕЖНЕЕ состояние — и запас
                // всегда отставал на одну запись: замер показал один слот из
                // двух («Expected: 2, But was: 1»). Копия того, что только что
                // записано, отставать не может, и порча основного блока не
                // стоит игроку уже ничего. Цена — вторая запись того же JSON;
                // миниатюры лежат отдельными файлами, так что это килобайты.
                if (!string.IsNullOrEmpty(json)) LvnKeep.Put(BackupKey(titleId), json);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[lvn] save write failed: " + e.Message);
                return false;
            }
        }
    }
}
