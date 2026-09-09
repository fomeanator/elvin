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
        /// <summary>Одна прожитая катсцена: адрес переигровки и чем показать
        /// её в галерее. Имя — АВТОРСКОЕ (запасное): на экране оно проходит
        /// через каталог перевода, поэтому английская версия покажет своё.
        ///
        /// <para>ЗАПИСЬ — ЭТО ПРОХОЖДЕНИЕ, А НЕ СЦЕНА. Одна и та же сцена,
        /// прожитая заново, ложится РЯДОМ отдельной карточкой: игрок был в ней
        /// в другом наряде, с другим выбором и другим кадром, и склеивать это
        /// в одну запись — терять ровно то, ради чего галерею смотрят («я
        /// думал, новое прохождение будет новую катсцену создавать» — Илья
        /// 09.09). Поэтому у записи два имени: <see cref="Key"/> — адрес
        /// прохождения, <see cref="Id"/> — метка в сценарии, по которой сцену
        /// переигрывают.</para></summary>
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

        /// <summary>Прожитые катсцены новеллы — копия, СВЕЖИЕ ВПЕРЁД. Порядок
        /// открытия годился, пока запись была одна на сцену; теперь каждое
        /// прохождение добавляет карточку, и только что прожитое обязано быть
        /// на виду, а не в хвосте коллекции.</summary>
        public static List<Seen> Seens(string titleId)
        {
            var list = new List<Seen>();
            foreach (var pair in Live(titleId))
            {
                var seen = pair.Value;
                if (seen == null) continue;
                // Запись прежнего образца (одна на сцену) ключа в себе не
                // держала — адресом ей служит ключ словаря.
                if (string.IsNullOrEmpty(seen.Key)) seen.Key = pair.Key;
                if (string.IsNullOrEmpty(seen.Id)) seen.Id = pair.Key;
                list.Add(seen);
            }
            list.Sort((a, b) => b.At.CompareTo(a.At));
            return list;
        }

        /// <summary>Сколько прохождений храним у одной новеллы. Коллекция —
        /// память, а не журнал: без предела десятое переигрывание главы завалило
        /// бы галерею собой и унесло место снимками.</summary>
        public const int Keep = 60;

        /// <summary>ПРОЖИТЬ КАТСЦЕНУ — завести карточку этого прохождения и
        /// вернуть её адрес. Каждый живой проход метки заводит СВОЮ запись:
        /// сцена та же, а кадр, наряд и выборы — уже другие. Пересмотр из
        /// галереи сюда не заходит (см. VnStage.RememberCutscene).</summary>
        public static string Lived(string titleId, string cutsceneId, string name,
                                   string chapter = null, string poster = null)
        {
            if (string.IsNullOrEmpty(cutsceneId)) return null;
            var map = Live(titleId);
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Секунда — достаточная разница: две метки одной сцены за один
            // проход не случаются, а два прохождения подряд быстрее секунды
            // не проходятся. Совпало — сдвигаем, лишь бы адрес был свой.
            var key = cutsceneId + "#" + now;
            while (map.ContainsKey(key)) key = cutsceneId + "#" + (++now);
            map[key] = new Seen
            {
                Key = key,
                Id = cutsceneId,
                Name = name,
                Chapter = chapter,
                Poster = poster,
                At = now,
            };
            Trim(titleId, map);
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

        /// <summary>Убрать самые старые прохождения сверх предела вместе с их
        /// снимками: карточка без снимка ещё карточка, а снимок без карточки —
        /// просто занятое место.</summary>
        private static void Trim(string titleId, Dictionary<string, Seen> map)
        {
            if (map.Count <= Keep) return;
            var order = new List<Seen>(map.Values);
            order.Sort((a, b) => (a?.At ?? 0).CompareTo(b?.At ?? 0));
            for (int i = 0; i < order.Count - Keep; i++)
            {
                var old = order[i];
                var key = old?.Key;
                if (string.IsNullOrEmpty(key)) continue;
                map.Remove(key);
                try
                {
                    var path = PosterPath(titleId, key);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                catch { /* снимок — украшение: не убрался, и ладно */ }
            }
        }

        // ── КАДР КАРТОЧКИ ────────────────────────────────────────────────────
        // Превью берётся из сцены двумя путями: если внутри неё менялся фон —
        // это его адрес (лежит строкой в записи). Если нет — как у «Знакомства
        // с Агентом», где кадр стоит с прошлой сцены, — снимаем экран, как для
        // сохранения: у него уже есть свой снимок, и второй заводить незачем.

        /// <summary>Файл снимка ПРОХОЖДЕНИЯ (может не существовать).</summary>
        public static string PosterPath(string titleId, string cutsceneId) =>
            System.IO.Path.Combine(Application.persistentDataPath, "lvn", "cutscenes",
                string.IsNullOrEmpty(titleId) ? "default" : titleId, cutsceneId + ".png");

        /// <summary>Записать снимок катсцены. Никогда не бросает: карточка —
        /// украшение, и сцена не должна падать из-за неё.</summary>
        public static void WritePoster(string titleId, string cutsceneId, Texture2D shot)
        {
            if (string.IsNullOrEmpty(cutsceneId) || shot == null) return;
            try
            {
                Lvn.Content.ContentLoader.AtomicWriteAllBytes(
                    PosterPath(titleId, cutsceneId), shot.EncodeToPNG());
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
                var path = PosterPath(titleId, cutsceneId);
                if (!System.IO.File.Exists(path)) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                return tex.LoadImage(System.IO.File.ReadAllBytes(path)) ? tex : null;
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
            return System.IO.File.Exists(PosterPath(titleId, key));
        }

        /// <summary>ВЫБРОСИТЬ ОДНО ПРОХОЖДЕНИЕ вместе с его снимком. Игрок
        /// сам решает, какую карточку хранить: коллекция теперь набирается
        /// проходами, и неудачный дубль он вправе убрать, не снося остальное
        /// («в списке на карточке прям корзину» — Илья 09.09).</summary>
        public static bool Drop(string titleId, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var map = Live(titleId);
            if (!map.Remove(key)) return false;
            LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(map));
            try
            {
                var path = PosterPath(titleId, key);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch { /* снимок — украшение: не убрался, карточки всё равно нет */ }
            return true;
        }

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
