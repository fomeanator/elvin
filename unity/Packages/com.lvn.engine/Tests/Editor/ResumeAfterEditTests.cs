using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПРОДОЛЖЕНИЕ ПОСЛЕ ПРАВКИ НОВЕЛЛЫ — ВСЯ ЦЕПОЧКА, А НЕ ЕЁ СЕРЕДИНА.
    ///
    /// <para>Якорь позиции покрыт модульно (<c>SaveLoadTests</c>), но там скрипт
    /// написан руками прямо в тесте. Между рукописным JSON и живой главой лежат
    /// компилятор и формат контейнера — два места, где якорь может потеряться
    /// тихо: тест останется зелёным, а игрок после авторской правки окажется не
    /// в том такте.</para>
    ///
    /// <para>Здесь играется НАСТОЯЩИЙ вывод <c>lvnconv</c> по паре
    /// <c>qa/fixtures/resume/{before,after}.lvns</c> — одна глава до и после
    /// правки. Свежесть образцов стережёт Go-тест: он пересобирает исходники и
    /// сверяет с тем, что вложено сюда.</para>
    /// </summary>
    public class ResumeAfterEditTests
    {
        // Настоящий вывод компилятора. Не править руками — см. докблок.
        private const string Before = "{\"scene\":\"resume_probe\",\"script\":[{\"op\":\"say\",\"text\":\"Первая строка вступления.\",\"who\":\"Narrator\"},{\"op\":\"say\",\"text\":\"Вторая строка вступления.\",\"who\":\"Narrator\"},{\"id\":\"meeting\",\"op\":\"label\"},{\"op\":\"say\",\"text\":\"Здесь начинается встреча.\",\"who\":\"Guide\"},{\"op\":\"say\",\"text\":\"Вопрос, на котором игрок остановился.\",\"who\":\"Guide\"},{\"op\":\"say\",\"text\":\"Ответ, который он ещё не видел.\",\"who\":\"Guide\"},{\"id\":\"after\",\"op\":\"label\"},{\"op\":\"say\",\"text\":\"Сцена закончилась.\",\"who\":\"Narrator\"}]}";
        private const string After  = "{\"scene\":\"resume_probe\",\"script\":[{\"op\":\"say\",\"text\":\"Первая строка вступления.\",\"who\":\"Narrator\"},{\"op\":\"say\",\"text\":\"Автор дописал сюда абзац.\",\"who\":\"Narrator\"},{\"op\":\"say\",\"text\":\"И ещё один, для верности.\",\"who\":\"Narrator\"},{\"op\":\"say\",\"text\":\"Вторая строка вступления.\",\"who\":\"Narrator\"},{\"id\":\"meeting\",\"op\":\"label\"},{\"op\":\"say\",\"text\":\"Здесь начинается встреча.\",\"who\":\"Guide\"},{\"op\":\"say\",\"text\":\"Вопрос, на котором игрок остановился.\",\"who\":\"Guide\"},{\"op\":\"say\",\"text\":\"Ответ, который он ещё не видел.\",\"who\":\"Guide\"},{\"id\":\"after\",\"op\":\"label\"},{\"op\":\"say\",\"text\":\"Сцена закончилась.\",\"who\":\"Narrator\"}]}";

        // Такт, на котором игрок закрыл игру, и такт, который он увидит,
        // вернувшись. Save() записывает «прочитано досюда», ContinueFrom
        // играет ДАЛЬШЕ — поэтому это соседние реплики, а не одна.
        private const string ReadUpTo = "Вопрос, на котором игрок остановился.";
        private const string NextBeat = "Ответ, который он ещё не видел.";

        private sealed class Recorder : ILvnStage
        {
            public string Last;
            public void ShowSay(string who, string text, string style) => Last = text;
            public void ShowChoice(System.Collections.Generic.IReadOnlyList<LvnOption> o) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject c, LvnSender s) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject c) { }
            public void OnEnd() { }
        }

        private static LvnPlayer Play(string json, out Recorder stage)
        {
            stage = new Recorder();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        /// <summary>Доиграть до нужной реплики и сохраниться. Идём ПО ТЕКСТУ, а
        /// не по индексу: индекс — ровно то, что правка ломает, и опираться на
        /// него в проверке против правок значило бы проверять себя же.</summary>
        private static LvnPlayer.LvnSnapshot SaveHavingRead(string beat)
        {
            var p = Play(Before, out var stage);
            for (int guard = 0; guard < 64 && stage.Last != beat; guard++) p.Advance();
            Assert.AreEqual(beat, stage.Last, "не дошли до такта, на котором закрывают игру");
            return p.Save();
        }

        /// <summary>Куда попадёт игрок, вернувшись в эту версию главы.</summary>
        private static string ResumeInto(string chapter, LvnPlayer.LvnSnapshot snap,
                                         out LvnPlayer.RestoreFidelity fidelity)
        {
            var p = Play(chapter, out var stage);
            p.Restore(snap);
            fidelity = p.LastRestore;
            p.ContinueFrom(p.Index);
            return stage.Last;
        }

        // ГЛАВНОЕ УТВЕРЖДЕНИЕ: правка не меняет того, что увидит вернувшийся.
        // Автор дописал абзац ПЕРЕД меткой — индексы за ней съехали на два.
        [Test]
        public void ПравкаГлавыНеМеняетМестоВозврата()
        {
            var snap = SaveHavingRead(ReadUpTo);
            Assert.AreEqual("meeting", snap.AnchorStableLabel,
                "якорем должна стать авторская метка");

            var untouched = ResumeInto(Before, snap, out var exact);
            var edited    = ResumeInto(After,  snap, out var moved);

            Assert.AreEqual(NextBeat, untouched, "сломан сам стенд: в неправленой главе не тот такт");
            Assert.AreEqual(untouched, edited,
                "правка увела игрока в другое место главы: якорь не пережил компиляцию");

            Assert.AreEqual(LvnPlayer.RestoreFidelity.Exact, exact,
                "глава не менялась — переезжать было незачем");
            Assert.AreEqual(LvnPlayer.RestoreFidelity.Relocated, moved,
                "индексы съехали — восстановление обязано это заметить, а не молча попасть");
        }

        // Наивное восстановление «по индексу» на правленой главе попало бы не
        // туда. Проверка, что стенд ставит НАСТОЯЩУЮ задачу, а не пустую.
        [Test]
        public void СтендДействительноСдвигаетИндексы()
        {
            var snap = SaveHavingRead(ReadUpTo);
            var afterDoc = LvnDocument.Parse(After);
            var naive = afterDoc.Script[snap.Index];
            Assert.AreNotEqual(NextBeat, (string)naive["text"],
                "в правленой главе тот же индекс ведёт туда же — стенд ничего не проверяет");
        }
    }
}
