using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using System.Text;

namespace Lvn.Services
{
    /// <summary>
    /// A/B-эксперименты прямо в сценарии: <c>если abtest("первая_сцена") == "b"</c>.
    ///
    /// <para>Проверить гипотезу «какая первая сцена удерживает лучше» раньше
    /// можно было только собрав две сборки и сравнив их вручную. Это не
    /// эксперимент, а два разных релиза: аудитории разные, недели разные, и
    /// вывод из сравнения не следует.</para>
    ///
    /// <para>ДЕЛЕНИЕ ДЕТЕРМИНИРОВАННОЕ: группа — это хеш от «имя теста + id
    /// игрока», а не жребий. Поэтому игрок всегда в своей группе — после
    /// перезахода, перезапуска и переустановки, — и история не меняется у него
    /// под руками. Случайный выбор при старте пришлось бы хранить, а
    /// сохранённый жребий теряется вместе с устройством.</para>
    ///
    /// <para>Группа уезжает в props КАЖДОГО события (см. LvnAnalytics), иначе
    /// сравнивать нечего: знать деление и не знать, что случилось в каждой
    /// половине, бесполезно.</para>
    /// </summary>
    public static class LvnExperiments
    {
        /// <summary>
        /// Варианты по умолчанию. Два — это ответ на «работает ли», больше
        /// вариантов требуют кратно больше игроков на тот же вывод, и на нашем
        /// размере аудитории кончаются ничем.
        /// </summary>
        private static readonly string[] DefaultVariants = { "a", "b" };

        // Имя теста → варианты, если автор задал свои.
        private static readonly Dictionary<string, string[]> _variants =
            new Dictionary<string, string[]>();

        // Что уже посчитали за эту сессию: хеш дешёвый, но зовут его из
        // выражения, а выражения считаются на каждом кадре реактивного текста.
        private static readonly Dictionary<string, string> _cache =
            new Dictionary<string, string>();

        /// <summary>Все тесты, в которых игрок состоит, — уезжают в события.</summary>
        public static IReadOnlyDictionary<string, string> Assignments => _cache;

        // Группы, присланные сервером. Они ГЛАВНЕЕ локального деления: только
        // сервер знает долю трафика, таргет по кампании и выключатель. Локальный
        // хеш остаётся запасным путём — для игры без сервера и для первого
        // запуска, пока ответ не пришёл.
        private static readonly Dictionary<string, string> _server =
            new Dictionary<string, string>();

        private const string PServer = "lvn.svc.ab.assignments";

        /// <summary>
        /// Забирает группы с сервера. Вызывается после регистрации: без сессии
        /// сервер не знает, кому отвечать.
        ///
        /// <para>Ответ переживает перезапуск (PlayerPrefs): иначе первая сцена
        /// успевала бы сыграться на локальном делении раньше, чем придёт ответ,
        /// и игрок увидел бы не тот вариант, за который его посчитали.</para>
        /// </summary>
        public static async Task RefreshAsync()
        {
            LoadCached();
            if (string.IsNullOrEmpty(LvnBackend.BaseUrl)) return;
            var (code, body) = await LvnBackend.GetAsync("/v1/experiments");
            if (code != 200 || string.IsNullOrEmpty(body)) return;
            try
            {
                var obj = JObject.Parse(body)["assignments"] as JObject;
                if (obj == null) return;
                _server.Clear();
                foreach (var p in obj.Properties())
                    if (p.Value != null) _server[p.Name] = p.Value.ToString();
                // Локальный кэш сбрасываем: серверный ответ мог переставить
                // игрока (подняли версию), и держать старое значение значит
                // отправлять в аналитику одну группу, а показывать другую.
                _cache.Clear();
                foreach (var kv in _server) _cache[kv.Key] = kv.Value;
                LvnKeep.Put(PServer, obj.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { /* испорченный ответ — живём на локальном делении */ }
        }

        private static bool _loaded;

        private static void LoadCached()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var raw = LvnKeep.Get(PServer, "");
                if (string.IsNullOrEmpty(raw)) return;
                foreach (var p in JObject.Parse(raw).Properties())
                {
                    _server[p.Name] = p.Value.ToString();
                    _cache[p.Name] = p.Value.ToString();
                }
            }
            catch { }   // конфиг опытов не пришёл — играем без разбиения на группы
        }

        /// <summary>
        /// Объявить свои варианты: <c>Declare("первая_сцена", "тихая", "громкая")</c>.
        /// Нужно только когда имена вариантов важнее, чем «a» и «b».
        /// </summary>
        public static void Declare(string test, params string[] variants)
        {
            if (string.IsNullOrEmpty(test) || variants == null || variants.Length < 2) return;
            _variants[test] = variants;
            _cache.Remove(test);
        }

        /// <summary>
        /// Группа игрока в этом тесте. Пустая строка, если игрок ещё не
        /// зарегистрирован: без стабильного id делить нечем, и выдумывать
        /// временную группу нельзя — она сменится, когда id появится, и
        /// разобьёт статистику пополам.
        /// </summary>
        public static string Variant(string test)
        {
            if (string.IsNullOrEmpty(test)) return "";
            LoadCached();
            // Слово сервера главнее: доля трафика, таргет и выключатель живут
            // там, и локальный хеш про них ничего не знает.
            if (_server.TryGetValue(test, out var fromServer)) return fromServer;
            if (_cache.TryGetValue(test, out var known)) return known;

            var uid = LvnBackend.UserId;
            if (string.IsNullOrEmpty(uid)) return "";

            var variants = _variants.TryGetValue(test, out var v) ? v : DefaultVariants;
            var pick = variants[(int)(StableHash(test + ":" + uid) % (uint)variants.Length)];
            _cache[test] = pick;
            return pick;
        }

        /// <summary>
        /// Хеш, одинаковый на всех платформах и во всех версиях. Взять
        /// string.GetHashCode() нельзя: он не обещает стабильности между
        /// запусками, и деление поехало бы при обновлении рантайма — половина
        /// игроков сменила бы группу молча.
        /// </summary>
        private static uint StableHash(string s)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                return ((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3];
            }
        }

        private static bool _installed;

        /// <summary>Ставит abtest() в вычислитель выражений.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            // Цепочка — в LvnExpression: свои функции добавляются, чужие остаются.
            Lvn.LvnExpression.AddHostFunction((name, args) =>
            {
                if (name != "abtest") return Lvn.LvnExpression.NotHandled;
                var test = args.Count > 0 ? args[0] as string ?? args[0]?.ToString() : null;
                return Variant(test);
            });
        }
    }
}
