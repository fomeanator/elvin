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

        private const string SavedBeat = "Вопрос, на котором игрок остановился.";

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

        /// <summary>Доиграть до нужной реплики. По ТЕКСТУ, а не по индексу:
        /// индекс — это ровно то, что правка ломает, и опираться на него в
        /// проверке против правок значило бы проверять себя же.</summary>
        private static LvnPlayer.LvnSnapshot SaveAt(string beat)
        {
            var p = Play(Before, out var stage);
            for (int guard = 0; guard < 64 && stage.Last != beat; guard++) p.Advance();
            Assert.AreEqual(beat, stage.Last, "не дошли до такта, на котором снимаем сохранение");
            return p.Save();
        }

        // Автор дописал абзац ПЕРЕД меткой — все индексы за ней съехали.
        // Сохранение обязано попасть в тот же такт, а не в тот же индекс.
        [Test]
        public void СохранениеПереживаетПравкуГлавы()
        {
            var snap = SaveAt(SavedBeat);
            Assert.AreEqual("meeting", snap.AnchorStableLabel,
                "якорем должна стать авторская метка");

            var edited = Play(After, out var stage);
            edited.Restore(snap);
            Assert.AreEqual(LvnPlayer.RestoreFidelity.Relocated, edited.LastRestore,
                "индексы съехали — восстановление обязано это заметить");
            edited.ContinueFrom(edited.Index);
            Assert.AreEqual(SavedBeat, stage.Last,
                "попали в другой такт: якорь не пережил компиляцию правленой главы");
        }

        // Та же глава без правок — попадание точное, а не «переехавшее».
        [Test]
        public void БезПравокПопаданиеТочное()
        {
            var snap = SaveAt(SavedBeat);
            var same = Play(Before, out var stage);
            same.Restore(snap);
            Assert.AreEqual(LvnPlayer.RestoreFidelity.Exact, same.LastRestore,
                "глава не менялась — переезжать было незачем");
            same.ContinueFrom(same.Index);
            Assert.AreEqual(SavedBeat, stage.Last);
        }
    }
}
