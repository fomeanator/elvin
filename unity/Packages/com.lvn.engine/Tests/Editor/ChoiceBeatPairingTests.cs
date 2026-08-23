using System.Collections.Generic;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ВЫБОР ПОКАЗЫВАЕТСЯ СО СВОЕЙ РЕПЛИКОЙ — И НИ РЕПЛИКОЙ РАНЬШЕ.
    ///
    /// <para>Сцена «предложение → ответные кнопки» пишется двумя репликами и
    /// выбором следом. Игрок обязан увидеть: реплика 1 (тап) → реплика 2
    /// ВМЕСТЕ с вариантами. Кнопки, всплывшие под репликой 1 («Моё лицо
    /// засекречено» + «По рукам»), выглядят сломанно: игрок отвечает на
    /// предложение, которого ещё не слышал. Живой скрин такого состояния
    /// пришёл после горячей перезагрузки главы — этот тест закрепляет, что
    /// ЧИСТОЕ прохождение движка так не делает.</para>
    /// </summary>
    public class ChoiceBeatPairingTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<string> Events = new List<string>();
            public void ShowSay(string who, string text, string style) => Events.Add("say:" + text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) => Events.Add("choice");
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command) { }
            public void OnEnd() { }
        }

        [Test]
        public void OptionsShowWithTheirLine_NeverOneLineEarly()
        {
            var doc = LvnDocument.Parse(@"{""scene"":""t"",""script"":[
                {""op"":""say"",""who"":""Агент"",""text"":""Моё лицо засекречено.""},
                {""op"":""say"",""who"":""Агент"",""text"":""Предлагаю обмен.""},
                {""op"":""choice"",""options"":[
                    {""text"":""По рукам."",""goto"":""yes""},
                    {""text"":""Сначала заслужи."",""goto"":""no""}]},
                {""op"":""label"",""id"":""yes""},
                {""op"":""label"",""id"":""no""},
                {""op"":""say"",""text"":""финал""}
            ]}");
            var stage = new RecStage();
            var p = new LvnPlayer(doc, stage);

            p.Advance();
            CollectionAssert.AreEqual(new[] { "say:Моё лицо засекречено." }, stage.Events,
                "кнопки под первой репликой — игрок отвечает на не прозвучавшее предложение");
            Assert.IsFalse(p.AtChoice, "после первой реплики выбора ещё нет");

            p.Advance();
            CollectionAssert.AreEqual(
                new[] { "say:Моё лицо засекречено.", "say:Предлагаю обмен.", "choice" },
                stage.Events,
                "вторая реплика и её варианты обязаны выйти одним битом");
            Assert.IsTrue(p.AtChoice);
        }
    }
}
