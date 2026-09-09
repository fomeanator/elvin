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
        /// <summary>Одна открытая катсцена: адрес переигровки и чем показать её
        /// в галерее. Имя — АВТОРСКОЕ (запасное): на экране оно проходит через
        /// каталог перевода, поэтому английская версия покажет своё.</summary>
        public sealed class Seen
        {
            public string Id;
            public string Name;
            public string Chapter;
            public string Poster;
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

        /// <summary>Открытые катсцены новеллы — копия, в порядке открытия.</summary>
        public static List<Seen> Seens(string titleId)
        {
            var list = new List<Seen>(Live(titleId).Values);
            return list;
        }

        /// <summary>Отметить катсцену увиденной. Возвращает true, когда она
        /// открылась впервые. Превью и глава ДОПИСЫВАЮТСЯ и при повторном
        /// показе: первый проход мог случиться до того, как фон доехал, и
        /// карточка осталась бы без картинки навсегда.</summary>
        public static bool Mark(string titleId, string cutsceneId, string name,
                                string chapter = null, string poster = null)
        {
            if (string.IsNullOrEmpty(cutsceneId)) return false;
            var map = Live(titleId);
            bool fresh = !map.TryGetValue(cutsceneId, out var seen) || seen == null;
            if (fresh) seen = new Seen { Id = cutsceneId };
            if (!string.IsNullOrEmpty(name)) seen.Name = name;
            if (!string.IsNullOrEmpty(chapter)) seen.Chapter = chapter;
            if (!string.IsNullOrEmpty(poster)) seen.Poster = poster;
            map[cutsceneId] = seen;
            LvnKeep.Put(Key(titleId), JsonConvert.SerializeObject(map));
            return fresh;
        }

        /// <summary>Забыть всё открытое у новеллы (сброс прогресса, отладка).</summary>
        public static void Clear(string titleId)
            => LvnKeep.DropScoped(Key(titleId), ref _cachedKey, ref _cached);
    }
}
