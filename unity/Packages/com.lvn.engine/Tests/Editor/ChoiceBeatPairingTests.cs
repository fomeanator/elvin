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
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command, Lvn.LvnSender sender)
                => ApplyStage(command);

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
        // ── СКОЛЬКО ИГРОК ДУМАЛ — число уезжает в телеметрию ─────────────────
        //
        // Мерилось это системным временем, мимо домашних часов, и потому не
        // проверялось ничем: подменить часы тест может, `DateTime.UtcNow` —
        // нет. Число при этом попадает в аналитику как «сколько думал над
        // выбором», и до сих пор никто не мог сказать, верное ли оно.

        [Test]
        public void ВремяРаздумьяМеряетсяДомашнимиЧасами()
        {
            var doc = LvnDocument.Parse(@"{""scene"":""t"",""script"":[
                {""op"":""choice"",""options"":[
                    {""text"":""Да"",""goto"":""a""},
                    {""text"":""Нет"",""goto"":""b""}]},
                {""op"":""label"",""id"":""a""},
                {""op"":""label"",""id"":""b""},
                {""op"":""say"",""text"":""финал""}
            ]}");
            var было = LvnClock.Now;
            float часы = 100f;
            LvnClock.Now = () => часы;
            try
            {
                var p = new LvnPlayer(doc, new RecStage());
                float думал = -1f;
                // Событие СТАТИЧЕСКОЕ — общее на все экземпляры плеера.
                // Отписка обязательна: забудь её, и следующий тест получит
                // нашего слушателя, а разбираться будет в своём файле.
                System.Action<int, string, float, int> слушатель = (i, text, seconds, ip) => думал = seconds;
                LvnPlayer.ChoicePicked += слушатель;
                try
                {
                    p.Advance();          // выбор показан на отметке 100
                    часы = 104.5f;        // игрок думал четыре с половиной секунды
                    p.Choose(0);
                }
                finally { LvnPlayer.ChoicePicked -= слушатель; }

                Assert.AreEqual(4.5f, думал, 0.001f,
                    "телеметрия получает не то время, которое прошло по часам движка");
            }
            finally { LvnClock.Now = было; }
        }

        [Test]
        public void БезПоказаВыбораРаздумьяНоль()
        {
            // Ноль — законная отметка сразу после запуска, поэтому «не
            // показывали» отмечено отдельным значением, а не нулём. Спутай их —
            // и первый же выбор на первом кадре отчитается о раздумье длиной в
            // весь сеанс.
            var doc = LvnDocument.Parse(@"{""scene"":""t"",""script"":[
                {""op"":""choice"",""options"":[{""text"":""Да"",""goto"":""a""}]},
                {""op"":""label"",""id"":""a""},
                {""op"":""say"",""text"":""финал""}
            ]}");
            var было = LvnClock.Now;
            LvnClock.Now = () => 0f;
            try
            {
                var p = new LvnPlayer(doc, new RecStage());
                float думал = -1f;
                System.Action<int, string, float, int> слушатель = (i, text, seconds, ip) => думал = seconds;
                LvnPlayer.ChoicePicked += слушатель;
                try { p.Advance(); p.Choose(0); }
                finally { LvnPlayer.ChoicePicked -= слушатель; }
                Assert.AreEqual(0f, думал, 0.001f);
            }
            finally { LvnClock.Now = было; }
        }
    }
}
