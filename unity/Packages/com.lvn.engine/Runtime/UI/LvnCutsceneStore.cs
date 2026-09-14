using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ОТКРЫТЫЕ КАТСЦЕНЫ — что игрок уже видел и куда за этим вернуться.
    ///
    /// <para>Катсцена — не картинка, а ОТРЕЗОК СЦЕНАРИЯ: «cutscene start Имя|id»
    /// … «cutscene end». Поэтому здесь не лежит ни кадра, ни видео — только
    /// адрес: какая новелла, какая глава, какой id и чем показать превью. На
    /// сервере по той же причине хранится ТОЛЬКО сценарий: галерея переигрывает
    /// главу с метки, а не проигрывает записанный ролик.</para>
    ///
    /// <para>Хранится рядом с открытиями CG-галереи и по тем же правилам:
    /// по новеллам порознь (две игры в одной оболочке не видят друг друга) и
    /// переживает удалённые сохранения — «видел однажды» значит «видел
    /// навсегда».</para>
    /// </summary>
    public static class LvnCutsceneStore
    {
        /// <summary>An unlocked scene. Key remains the existing storage/thumbnail
        /// address; Id and Chapter identify the scene independently of replays.</summary>
        public sealed class Seen
        {
            public string Key;       // адрес прохождения: «id#когда»
            public string Id;        // метка сцены в сценарии — с неё играют
            public string Name;
            public string Chapter;
            public string Poster;
            public long At;          // когда прожито, unix-секунды
        }

        static LvnCutsceneStore()
        {
            LvnKeep.Wiped += () => { _cached = null; _cachedKey = null; };
        }

        private static string Key(string titleId) => LvnKeep.Scoped("lvn.cutscenes.", titleId);

        private static string _cachedKey;
        private static Dictionary<string, Seen> _cached;

        private static Dictionary<string, Seen> Live(string titleId)
        {
            var key = Key(titleId);
            if (_cachedKey == key && _cached != null) return _cached;
            var map = new Dictionary<string, Seen>();
            var json = LvnKeep.Get(key, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    map = JsonConvert.DeserializeObject<Dictionary<string, Seen>>(json)
                          ?? new Dictionary<string, Seen>();
                }
                catch { map = new Dictionary<string, Seen>(); }
            }
            _cachedKey = key;
            _cached = map;
            return map;
        }

        /// <summary>One card per scene, newest first. Existing duplicate records
        /// and their screenshots stay on disk for compatibility; the collection
        /// displays the newest card and reuses it on every subsequent visit.</summary>
        public static List<Seen> Seens(string titleId)
        {
            var latest = new Dictionary<(string chapter, string id), Seen>();
            foreach (var pair in Live(titleId))
            {
                var seen = pair.Value;
                if (seen == null) continue;
                if (string.IsNullOrEmpty(seen.Key)) seen.Key = pair.Key;
                if (string.IsNullOrEmpty(seen.Id)) seen.Id = pair.Key;
                var identity = (seen.Chapter ?? "", seen.Id);
                if (!latest.TryGetValue(identity, out var previous)
                    || seen.At > previous.At
                    || (seen.At == previous.At && string.CompareOrdinal(seen.Key, previous.Key) < 0))
                    latest[identity] = seen;
            }
            var list = new List<Seen>(latest.Values);
            list.Sort((a, b) => a.At != b.At ? b.At.CompareTo(a.At)
                : string.CompareOrdinal(a.Key, b.Key));
            return list;
        }

        public static System.Func<long> Now =
            () => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Unlock a scene or update its existing card. Replaying a chapter,
        /// restarting the app and elapsed wall time never create another card.
        /// The collection has no age/count eviction: an unlock is permanent.</summary>
        public static string Lived(string titleId, string cutsceneId, string name,
                                   string chapter = null, string poster = null)
        {
            if (string.IsNullOrEmpty(cutsceneId)) return null;
            var map = Live(titleId);
            long now = Now();
            foreach (var seen in Seens(titleId))
            {
                if (seen.Id != cutsceneId || (seen.Chapter ?? "") != (chapter ?? "")) continue;
                if (!string.IsNullOrEmpty(name)) seen.Name = name;
                if (!string.IsNullOrEmpty(poster)) seen.Poster = poster;
                seen.At = System.Math.Max(seen.At, now);
                LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(map));
                return seen.Key;
            }
            // Keep the previous address format so existing thumbnail readers work.
            var key = cutsceneId + "#" + now;
            while (map.ContainsKey(key)) key = cutsceneId + "#" + (++now);
            map[key] = new Seen
            {
                Key = key, Id = cutsceneId, Name = name, Chapter = chapter,
                Poster = poster, At = now,
            };
            LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(map));
            return key;
        }

        /// <summary>Дописать карточке превью: первый проход мог случиться
        /// раньше, чем доехал фон, и без этого карточка осталась бы пустой
        /// навсегда. Пишем ПО АДРЕСУ ПРОХОЖДЕНИЯ, чтобы кадр не ушёл в чужую
        /// карточку той же сцены.</summary>
        public static void Dress(string titleId, string key, string poster)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(poster)) return;
            var map = Live(titleId);
            if (!map.TryGetValue(key, out var seen) || seen == null) return;
            seen.Poster = poster;
            LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(map));
        }

        // ── КАДР КАРТОЧКИ ────────────────────────────────────────────────────
        // Превью берётся из сцены двумя путями: если внутри неё менялся фон —
        // это его адрес (лежит строкой в записи). Если нет — как у «Знакомства
        // с Агентом», где кадр стоит с прошлой сцены, — снимаем экран, как для
        // сохранения: у него уже есть свой снимок, и второй заводить незачем.

        /// <summary>Файл снимка ПРОХОЖДЕНИЯ (может не существовать).
        ///
        /// <para>JPEG, а не PNG: кадр держим в размере экрана — его
        /// разглядывают во весь экран и щипком, — и без сжатия каждая карточка
        /// весила бы мегабайты. Фотографии кадра точность PNG не нужна.</para></summary>
        public static string PosterPath(string titleId, string cutsceneId) =>
            System.IO.Path.Combine(Application.persistentDataPath, "lvn", "cutscenes",
                string.IsNullOrEmpty(titleId) ? "default" : titleId, cutsceneId + ".jpg");

        /// <summary>Записать снимок катсцены. Никогда не бросает: карточка —
        /// украшение, и сцена не должна падать из-за неё.</summary>
        public static void WritePoster(string titleId, string cutsceneId, Texture2D shot)
        {
            if (string.IsNullOrEmpty(cutsceneId) || shot == null) return;
            try
            {
                Lvn.Content.ContentLoader.AtomicWriteAllBytes(
                    PosterPath(titleId, cutsceneId), shot.EncodeToJPG(92));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[lvn-cutscene] снимок не записан: " + e.Message);
            }
        }

        /// <summary>Снимок катсцены или null. Текстура принадлежит вызвавшему.</summary>
        public static Texture2D LoadPoster(string titleId, string cutsceneId)
        {
            try
            {
                var path = Poster(titleId, cutsceneId);
                if (path == null) return null;
                // Разбор картинки — у дома памяти: он убирает за собой, когда
                // файл побит, а расписанный здесь обряд оставлял бы пустую
                // текстуру при каждой попытке открыть галерею.
                return Lvn.Content.AssetMemory.Decode(System.IO.File.ReadAllBytes(path));
            }
            catch { return null; }
        }

        /// <summary>Есть ли у этого прохождения чем показаться — адресом фона
        /// или снимком кадра.</summary>
        public static bool HasPoster(string titleId, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (Live(titleId).TryGetValue(key, out var seen)
                && seen != null && !string.IsNullOrEmpty(seen.Poster)) return true;
            return Poster(titleId, key) != null;
        }

        /// <summary>Файл снимка, какой есть, или null. Карточки прежних
        /// сборок лежат в PNG — терять их из-за смены формата незачем.</summary>
        private static string Poster(string titleId, string key)
        {
            var jpg = PosterPath(titleId, key);
            if (System.IO.File.Exists(jpg)) return jpg;
            var png = System.IO.Path.ChangeExtension(jpg, ".png");
            return System.IO.File.Exists(png) ? png : null;
        }

        /// <summary>НЕ ПОДКЛЮЧЁН: compatibility hook for explicit maintenance.
        /// The player collection no longer exposes deletion; account reset uses Clear.</summary>
        public static bool Drop(string titleId, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var map = Live(titleId);
            if (!map.Remove(key)) return false;
            LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(map));
            DropPoster(titleId, key);
            return true;
        }

        /// <summary>Убрать снимок прохождения — в любом из форматов.</summary>
        private static void DropPoster(string titleId, string key)
        {
            try
            {
                var path = Poster(titleId, key);
                if (path != null) System.IO.File.Delete(path);
            }
            catch { /* снимок — украшение: не убрался, и ладно */ }
        }

        /// <summary>НЕ ПОДКЛЮЧЁН: compatibility hook for host-side migrations
        /// that mutate records in place. Normal unlocks persist through Lived/Dress.</summary>
        public static void Remember(string titleId)
            => LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(Live(titleId)));

        /// <summary>Забыть всё открытое у новеллы (сброс прогресса, отладка).</summary>
        public static void Clear(string titleId)
        {
            LvnKeep.DropScoped(Key(titleId), ref _cachedKey, ref _cached);
            // Снимки — те же личные данные: забвение новеллы уносит и их,
            // иначе прожитое возвращалось бы картинками после сброса.
            try
            {
                var dir = System.IO.Path.GetDirectoryName(PosterPath(titleId, "x"));
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, recursive: true);
            }
            catch { /* карточки — украшение: не смогли убрать, не мешаем сбросу */ }
        }
    }
}
