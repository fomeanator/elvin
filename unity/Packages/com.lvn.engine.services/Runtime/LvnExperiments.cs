using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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
            var previous = Lvn.LvnExpression.HostFunction;
            Lvn.LvnExpression.HostFunction = (name, args) =>
            {
                if (name == "abtest")
                {
                    var test = args.Count > 0 ? args[0] as string ?? args[0]?.ToString() : null;
                    return Variant(test);
                }
                return previous != null ? previous(name, args) : Lvn.LvnExpression.NotHandled;
            };
        }
    }
}
