using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Lvn;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЦЕНА ДЛИННОЙ ГЛАВЫ — ЗА ТАП И ЗА СОХРАНЕНИЕ.
    ///
    /// <para>Условие с натуры: игрок читает длинную главу — кинетическую
    /// новеллу или импортированный эпизод на тысячи реплик. Каждый тап это
    /// один бит: плеер делает снимок (переменные, стек, след реплея, ЯКОРЬ
    /// позиции) и кладёт его в историю отката. Вопрос не «работает ли»
    /// (работает), а СКОЛЬКО ЭТО СТОИТ ближе к концу главы.</para>
    ///
    /// <para>Замер 05.09 на главе БЕЗ МЕТОК (а метки ставит выбор, и в
    /// кинетической новелле их нет вовсе): 500 реплик — 67 мс, 2000 — 961 мс,
    /// 6000 — 8662 мс. Квадрат: 1,4 мс на каждый тап и растёт по ходу чтения,
    /// а на телефоне это в разы больше. Причина — якорь позиции искался
    /// сканированием НАЗАД до ближайшей метки, на каждом бите и не по одному
    /// разу. После карты якорей: 1 / 4 / 19 мс, и наличие меток больше ничего
    /// не меняет.</para>
    /// </summary>
    public class LongChapterCostTests
    {
        private sealed class NullStage : ILvnStage
        {
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command, Lvn.LvnSender sender) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command) { }
            public void OnEnd() { }
        }

        private static string ДлиннаяГлава(int реплик, int метокЧерез = 0)
        {
            var sb = new StringBuilder();
            sb.Append(@"{""scene"":""длинная"",""script"":[");
            for (int i = 0; i < реплик; i++)
            {
                if (i > 0) sb.Append(',');
                if (метокЧерез > 0 && i % метокЧерез == 0)
                    sb.Append(@"{""op"":""label"",""id"":""сцена").Append(i).Append(@"""},");
                if (i % 5 == 0)
                    sb.Append(@"{""op"":""bg"",""id"":""зал").Append(i % 7).Append(@""",""sprite_url"":""bg/зал")
                      .Append(i % 7).Append(@".jpg""},");
                if (i % 11 == 0)
                    sb.Append(@"{""op"":""actor"",""id"":""герой"",""emotion"":""e").Append(i % 13)
                      .Append(@""",""position"":""left""},");
                sb.Append(@"{""op"":""say"",""text"":""Реплика ").Append(i).Append(@".""}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static long ПрогонМс(int реплик, int метокЧерез)
        {
            // Берём ЛУЧШИЙ из трёх заходов: машина под прогоном шумит, а нас
            // интересует форма роста, а не абсолютное число.
            long best = long.MaxValue;
            for (int попытка = 0; попытка < 3; попытка++)
            {
                var doc = LvnDocument.Parse(ДлиннаяГлава(реплик, метокЧерез));
                var p = new LvnPlayer(doc, new NullStage());
                var sw = Stopwatch.StartNew();
                int шагов = 0;
                while (!p.Finished && шагов < реплик * 4) { p.Advance(); шагов++; }
                sw.Stop();
                best = Math.Min(best, sw.ElapsedMilliseconds);
            }
            return best;
        }

        /// Цена тапа не должна расти по мере чтения: втрое длиннее глава —
        /// втрое дольше прогон, а не вдевятеро.
        [Test]
        public void ЦенаТапаНеРастётБыстрееДлиныГлавы()
        {
            long малая = Math.Max(1, ПрогонМс(2000, 0));
            long большая = ПрогонМс(6000, 0);

            TestContext.WriteLine($"без меток: 2000 реплик {малая} мс, 6000 реплик {большая} мс, "
                                + $"отношение {(double)большая / малая:F1} (линейно ≈3, квадратично ≈9)");

            Assert.Less((double)большая / малая, 6.0,
                "втрое более длинная глава стала дороже больше чем втрое — цена тапа растёт по ходу чтения");
        }

        /// Наличие меток не должно влиять на цену: кинетическая новелла
        /// (глава без выборов, а значит без меток) — это целый жанр.
        [Test]
        public void ГлаваБезМетокНеДорожеГлавыСМетками()
        {
            long безМеток = ПрогонМс(6000, 0);
            long сМетками = Math.Max(1, ПрогонМс(6000, 50));

            TestContext.WriteLine($"6000 реплик: без меток {безМеток} мс, с метками через 50 {сМетками} мс");

            Assert.Less((double)безМеток / сМетками, 4.0,
                "глава без меток дороже той же главы с метками — якорь ищется сканированием");
        }

        /// Сохранение остаётся маленьким, а «продолжить» — мгновенным.
        [Test]
        public void СохранениеОстаётсяМаленькимИБыстрым()
        {
            var doc = LvnDocument.Parse(ДлиннаяГлава(6000));
            var p = new LvnPlayer(doc, new NullStage());
            int шагов = 0;
            while (!p.Finished && шагов < 24000) { p.Advance(); шагов++; }

            var snap = p.Save();
            int байт = JsonConvert.SerializeObject(snap).Length;

            var sw = Stopwatch.StartNew();
            var p2 = new LvnPlayer(doc, new NullStage());
            p2.Restore(snap);
            p2.ReplayVisuals(snap.Index);
            sw.Stop();

            TestContext.WriteLine($"глава на {doc.Script.Count} команд: след {snap.Trace?.Length ?? 0} шагов, "
                                + $"сейв {байт / 1024.0:F1} КБ, восстановление {sw.ElapsedMilliseconds} мс, "
                                + $"история {p.HistoryDepth} снимков");

            Assert.Less(байт, 200 * 1024,
                "сейв длинной главы перестал помещаться в разумные килобайты — слоты лежат в настройках устройства");
            Assert.Less(sw.ElapsedMilliseconds, 2000,
                "«продолжить» после длинной главы стало заметным ожиданием");
            Assert.LessOrEqual(p.HistoryDepth, LvnPlayer.MaxHistory,
                "история отката растёт без предела — это память на устройстве игрока");
        }

        /// Карта якорей обязана давать ТОТ ЖЕ ответ, что и поиск назад:
        /// ближайшую метку слева, а «прочный» якорь — ближайшую АВТОРСКУЮ.
        [Test]
        public void ЯкорьНаходитБлижайшуюМеткуИБлижайшуюАвторскую()
        {
            var doc = LvnDocument.Parse(@"{""scene"":""т"",""script"":[
                {""op"":""label"",""id"":""глава""},
                {""op"":""say"",""text"":""раз""},
                {""op"":""label"",""id"":""__минт1""},
                {""op"":""say"",""text"":""два""},
                {""op"":""say"",""text"":""три""}
            ]}");
            var p = new LvnPlayer(doc, new NullStage());
            p.Advance();        // «раз»
            p.Advance();        // «два» (за минтованной меткой)
            var snap = p.Save();

            Assert.AreEqual("__минт1", snap.AnchorLabel, "обычный якорь — ближайшая метка слева, любая");
            Assert.AreEqual("глава", snap.AnchorStableLabel, "прочный якорь минтованную метку берёт не должен");
            Assert.AreEqual(snap.Index - 2, snap.AnchorSteps, "смещение считается от найденной метки");
            Assert.AreEqual(snap.Index - 0, snap.AnchorStableSteps, "смещение прочного якоря — от авторской метки");
        }

        /// Текст сменился под игроком — карта якорей обязана смениться с ним.
        [Test]
        public void ГорячаяЗаменаПерестраиваетКартуЯкорей()
        {
            var p = new LvnPlayer(LvnDocument.Parse(@"{""scene"":""т"",""script"":[
                {""op"":""label"",""id"":""старт""},
                {""op"":""say"",""text"":""раз""},
                {""op"":""say"",""text"":""два""}
            ]}"), new NullStage());
            p.Advance();

            Assert.IsTrue(p.TryReplaceScript(LvnDocument.Parse(@"{""scene"":""т"",""script"":[
                {""op"":""label"",""id"":""старт""},
                {""op"":""say"",""text"":""раз""},
                {""op"":""label"",""id"":""вставка""},
                {""op"":""say"",""text"":""два""},
                {""op"":""say"",""text"":""три""}
            ]}")), "замена текста не прошла — проверять нечего");

            p.Advance();
            var snap = p.Save();
            Assert.AreEqual("вставка", snap.AnchorLabel,
                "после замены текста якорь считается по старой карте — сохранение уедет не туда");
        }
    }
}
