using System.Collections.Generic;
using System.Linq;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СЕЙВ В ЛЮБОЙ ТОЧКЕ — ТОТ ЖЕ КАДР.
    ///
    /// <para>Соседние проверки разбирают восстановление ПО ТЕМАМ: ветки,
    /// скрытия, оси, эффекты, частицы, камера, звук. Каждая сторожит свою
    /// грань. Общий же инвариант — «пройти вперёд до N» и «восстановиться из
    /// сейва, снятого на N», дают ОДИН кадр — не проверен нигде, а обещание
    /// звучит именно так: в любой точке.</para>
    ///
    /// <para>Разница между «покрыто по темам» и «покрыто целиком» здесь не
    /// теоретическая: живой репорт партнёра был как раз о грани, которой ни
    /// одна тема не касалась — персонаж пропадал после возврата в главу, и
    /// отличить «его убрали» от «его не поставили» было нечем.</para>
    ///
    /// <para>Кадром считается то, что видно: фон и состав актёров с их
    /// последними свойствами. Поток команд не сравнивается — восстановление
    /// НАРОЧНО не повторяет каждое затемнение главы, оно сворачивает их к
    /// последнему значению.</para>
    /// </summary>
    public class ReplayEqualsForwardTests
    {
        private sealed class Stage : ILvnStage
        {
            public readonly List<JObject> Applied = new List<JObject>();
            public void ShowSay(string who, string text, string style) { }
            public bool AwaitingChoice;
            public void ShowChoice(IReadOnlyList<LvnOption> options) => AwaitingChoice = true;
            public void ApplyStage(JObject c, LvnSender s) => Applied.Add(c);
            public void ApplyStage(JObject c) => Applied.Add(c);
            public void OnEnd() { }
        }

        // Глава с РАЗВИЛКОЙ — и это не украшение стенда. Без выбора след
        // исполнения совпадает с линейным проходом по скрипту, и проверка не
        // отличала бы правдивое восстановление от слепого. Ветка, которую не
        // выбрали, прячет актёра; пройди реплей линейно — он бы её исполнил.
        private const string Chapter = @"{""script"":[
            {""op"":""bg"",""id"":""hall"",""sprite_url"":""/bg/hall.jpg""},
            {""op"":""actor"",""id"":""one"",""show"":true},
            {""op"":""say"",""text"":""первая""},
            {""op"":""actor"",""id"":""two"",""show"":true},
            {""op"":""fx"",""kind"":""rain""},
            {""op"":""say"",""text"":""вторая""},
            {""op"":""actor"",""id"":""one"",""show"":false},
            {""op"":""bg"",""id"":""yard"",""sprite_url"":""/bg/yard.jpg""},
            {""op"":""audio"",""channel"":""music"",""url"":""/a/theme.ogg""},
            {""op"":""say"",""text"":""третья""},
            {""op"":""actor"",""id"":""one"",""show"":true},
            {""op"":""actor"",""id"":""two"",""axes"":{""mood"":""sad""}},
            {""op"":""say"",""text"":""четвёртая""},
            {""op"":""fx"",""kind"":""rain"",""on"":false},
            {""op"":""choice"",""options"":[
                {""text"":""остаться"",""goto"":""stay""},
                {""text"":""уйти"",""goto"":""leave""}]},
            {""op"":""label"",""id"":""leave""},
            {""op"":""actor"",""id"":""two"",""show"":false},
            {""op"":""say"",""text"":""ветка, которую не выбрали""},
            {""op"":""label"",""id"":""stay""},
            {""op"":""say"",""text"":""пятая""}
        ]}";

        private static (LvnPlayer p, Stage s) Open()
        {
            var s = new Stage();
            return (new LvnPlayer(LvnDocument.Parse(Chapter), s), s);
        }

        /// <summary>Кадр: фон и актёры с последними свойствами. Именно это
        /// игрок видит — и именно это обязано совпасть.</summary>
        private static string Frame(Stage s)
        {
            string bg = null;
            var actors = new SortedDictionary<string, string>();
            foreach (var c in s.Applied)
            {
                var op = (string)c["op"];
                if (op == "bg") bg = (string)c["sprite_url"] ?? (string)c["id"];
                else if (op == "actor" || op == "obj")
                {
                    var id = (string)c["id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    // show отсутствует — команда меняет облик, не присутствие.
                    // Читаем СЛОВАРЁМ, а не приведением: `show=no` доезжает
                    // строкой, и приведение молча оставило бы скрытого в кадре
                    // (страж TestNobodyCastsShowToBool ловит именно это).
                    bool? show = LvnBool.Parse(c["show"]);
                    actors.TryGetValue(id, out var prev);
                    bool visible = show ?? (prev != null && prev.StartsWith("+"));
                    var axes = c["axes"]?.ToString(Newtonsoft.Json.Formatting.None)
                               ?? (prev != null && prev.Length > 1 ? prev.Substring(1) : "");
                    actors[id] = (visible ? "+" : "-") + axes;
                }
            }
            var shown = actors.Where(kv => kv.Value.StartsWith("+"))
                              .Select(kv => kv.Key + kv.Value);
            return "bg=" + (bg ?? "-") + " | " + string.Join(", ", shown);
        }

        /// <summary>Довести главу до N-го шага, выбирая на развилке ПЕРВЫЙ
        /// вариант. Выбор здесь — часть условия: он уводит с линейного пути, и
        /// именно поэтому след исполнения перестаёт совпадать с прямым
        /// проходом по скрипту.</summary>
        private static void PlayTo(LvnPlayer p, Stage s, int n)
        {
            for (int i = 0; i < n && !p.Finished; i++)
            {
                if (s.AwaitingChoice) { s.AwaitingChoice = false; p.Choose(0); }
                else p.Advance();
            }
        }

        private static int Steps()
        {
            var (p, s) = Open();
            int n = 0;
            while (!p.Finished && n < 64)
            {
                if (s.AwaitingChoice) { s.AwaitingChoice = false; p.Choose(0); }
                else p.Advance();
                n++;
            }
            return n;
        }

        // ГЛАВНОЕ: для КАЖДОЙ точки главы кадр после восстановления совпадает с
        // кадром, до которого дошли обычным чтением.
        [Test]
        public void ВосстановлениеДаётТотЖеКадрВЛюбойТочке()
        {
            int total = Steps();
            Assert.Greater(total, 5, "стенд слишком короткий, чтобы что-то проверять");

            for (int n = 1; n <= total; n++)
            {
                var (fwd, fwdStage) = Open();
                PlayTo(fwd, fwdStage, n);
                var snap = fwd.Save();

                var (back, backStage) = Open();
                back.Restore(snap);
                back.ReplayVisuals(back.Index);

                Assert.AreEqual(Frame(fwdStage), Frame(backStage),
                    $"на шаге {n} восстановленный кадр разошёлся с прочитанным");
            }
        }

        // Стенд обязан ставить настоящую задачу: если кадр везде одинаковый,
        // совпадение ничего не значит.
        [Test]
        public void СтендДействительноМеняетКадр()
        {
            int total = Steps();
            var seen = new HashSet<string>();
            for (int n = 1; n <= total; n++)
            {
                var (p, s) = Open();
                PlayTo(p, s, n);
                seen.Add(Frame(s));
            }
            Assert.Greater(seen.Count, 3,
                $"кадр принимает всего {seen.Count} различных состояний — стенд ничего не проверяет");
        }

        // СЛЕД ИСПОЛНЕНИЯ — НЕ УКРАШЕНИЕ. Без него восстановление идёт линейно
        // по скрипту и исполняет команды из ветки, которую игрок НЕ выбирал.
        // Проверка сравнения кадров имеет смысл ровно постольку, поскольку
        // умеет это заметить.
        [Test]
        public void БезСледаВосстановлениеБерётЧужуюВетку()
        {
            int total = Steps();
            bool diverged = false;
            for (int n = 1; n <= total && !diverged; n++)
            {
                var (fwd, fwdStage) = Open();
                PlayTo(fwd, fwdStage, n);
                var snap = fwd.Save();
                snap.Trace = null;                 // старый сейв: следа нет

                var (back, backStage) = Open();
                back.Restore(snap);
                back.ReplayVisuals(back.Index);
                if (Frame(fwdStage) != Frame(backStage)) diverged = true;
            }
            Assert.IsTrue(diverged,
                "без следа кадр совпал везде — значит стенд не проходит через развилку, "
                + "и главная проверка не отличает правдивое восстановление от слепого");
        }
    }
}
