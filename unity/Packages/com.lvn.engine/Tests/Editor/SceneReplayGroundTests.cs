using System.Collections.Generic;
using System.Linq;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ФУНДАМЕНТ ПОД РЕПЛЕЙ СЦЕНЫ — что уже есть, и чего не хватает.
    ///
    /// <para>Сравнение с цехом назвало две дыры обвязки: музыкальная комната и
    /// реплей сцен («пересмотреть понравившееся из меню»). Я оценил обе как
    /// «стоят экрана, а не движка» — и эта проверка существует затем, чтобы
    /// оценка не осталась оценкой.</para>
    ///
    /// <para>Замер показал: для реплея готово ТРИ части из четырёх — метки
    /// известны плееру, вход с произвольной позиции есть, кадр этой позиции
    /// восстанавливается. Не хватает ЧЕТВЁРТОЙ, и она движковая: холостого
    /// режима, в котором прохождение не записывается. Сейчас автосохранение
    /// пишется всегда, пока идёт игра, — значит «пересмотреть сцену» сдвинуло
    /// бы игроку его настоящую позицию в главе.</para>
    ///
    /// <para>Тест закрепляет готовые три части: если они сломаются, реплей
    /// станет дороже, и знать об этом надо до того, как за него возьмутся.</para>
    /// </summary>
    public class SceneReplayGroundTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public readonly List<JObject> Applied = new List<JObject>();
            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> o) { }
            public void ApplyStage(JObject c, LvnSender s) => ApplyStage(c);
            public void ApplyStage(JObject c) => Applied.Add(c);
            public void OnEnd() { }
        }

        private const string Doc = @"{""script"":[
            {""op"":""bg"",""sprite_url"":""bg/пролог.jpg""},
            {""op"":""say"",""text"":""пролог""},
            {""op"":""label"",""id"":""встреча""},
            {""op"":""bg"",""sprite_url"":""bg/встреча.jpg""},
            {""op"":""actor"",""id"":""она"",""sprite_url"":""art/она.png"",""show"":true},
            {""op"":""say"",""text"":""сцена встречи""},
            {""op"":""label"",""id"":""финал""},
            {""op"":""say"",""text"":""финал""}
        ]}";

        private static (LvnPlayer p, RecStage s) Собрать()
        {
            var s = new RecStage();
            return (new LvnPlayer(LvnDocument.Parse(Doc), s), s);
        }

        // Экран реплея ищет метку так же: по самому документу, а не по внутренностям
        // плеера — иначе список сцен пришлось бы вести автору вручную.
        private static int ИндексМетки(string id)
        {
            var doc = LvnDocument.Parse(Doc);
            for (int i = 0; i < doc.Script.Count; i++)
                if (doc.Script[i] is JObject c && (string)c["op"] == "label" && (string)c["id"] == id)
                    return i;
            return -1;
        }

        [Test]
        public void СценыГлавыВидныПоМеткам()
        {
            Assert.AreEqual(2, ИндексМетки("встреча"), "метка сцены не нашлась в документе");
            Assert.AreEqual(6, ИндексМетки("финал"));
            Assert.AreEqual(-1, ИндексМетки("нет-такой"), "выдуманная метка обязана не находиться");
        }

        [Test]
        public void ИграСМеткиНачинаетсяИменноТам()
        {
            var (p, s) = Собрать();
            p.ContinueFrom(ИндексМетки("встреча"));

            CollectionAssert.DoesNotContain(s.Lines, "пролог",
                "вход с метки протащил за собой предыдущую сцену");
            CollectionAssert.Contains(s.Lines, "сцена встречи",
                "вход с метки не доиграл до её реплики");
        }

        [Test]
        public void КадрСценыВосстанавливаетсяБезПрохожденияГлавы()
        {
            var (p, s) = Собрать();
            int at = ИндексМетки("финал");
            p.ReplayVisuals(at);

            var фоны = s.Applied.Where(c => (string)c["op"] == "bg")
                                .Select(c => (string)c["sprite_url"]).ToList();
            Assert.AreEqual(1, фоны.Count,
                "кадр сцены собрался несколькими фонами — реплей платил бы за каждый");
            Assert.AreEqual("bg/встреча.jpg", фоны[0],
                "восстановлен не тот фон, в котором игрок оказался бы");

            var актёры = s.Applied.Where(c => (string)c["op"] == "actor")
                                  .Select(c => (string)c["id"]).ToList();
            CollectionAssert.Contains(актёры, "она", "в восстановленном кадре нет того, кто в нём был");
        }

        // ЧЕГО НЕ ХВАТАЕТ, названо здесь же — чтобы стоимость реплея не пришлось
        // выяснять заново. Автосохранение пишется всегда, пока идёт игра: без
        // холостого режима «пересмотреть сцену» сдвинуло бы игроку его настоящую
        // позицию в главе.
        [Test]
        public void ХолостогоРежимаПокаНет_ИЭтоЗаписано()
        {
            var поле = typeof(Lvn.UI.VnStage).GetField("ReplayOnly",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Assert.IsNull(поле,
                "холостой режим появился — значит реплей сцены стал дешевле: "
              + "перепишите этот тест и вердикт в docs/world-position.md");
        }
    }
}
