using System.Collections.Generic;
using System.Linq;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ФОН — ОДИН СЛОТ НА ДВЕ КОМАНДЫ, И В ВОССТАНОВЛЕНИИ ТОЖЕ.
    ///
    /// <para>Правило записано в самой сцене: объёмная декорация «replaces
    /// painted backgrounds until `bg3d off` (or the next ordinary `bg`)».
    /// То есть <c>bg</c> и <c>bg3d</c> делят одно место в кадре, и побеждает
    /// ТОТ, ЧТО ПОЗЖЕ.</para>
    ///
    /// <para>Восстановление кадра собирает его двумя проходами: полотно
    /// ставится первым (чтобы игрок не платил за девять комнат, из которых
    /// ушёл), состояния — последними. <c>bg3d</c> назван состоянием, и оттого
    /// при восстановлении он оказывался ПОСЛЕ полотна независимо от того, что
    /// автор написал позже. Сохранился в комнате, вышел из неё в нарисованный
    /// фон, сохранился снова — и вернулся в покинутую комнату.</para>
    ///
    /// <para>Проверяется поэтому не «применились ли обе команды», а КТО
    /// ПОСЛЕДНИЙ в собранном кадре — обе стороны, чтобы починка одной не
    /// сломала другую.</para>
    /// </summary>
    public class BackdropSlotReplayTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<JObject> Applied = new List<JObject>();
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(IReadOnlyList<LvnOption> o) { }
            public void ApplyStage(JObject c, LvnSender s) => ApplyStage(c);
            public void ApplyStage(JObject c) => Applied.Add(c);
            public void OnEnd() { }
        }

        private const string УшёлИзКомнаты = @"{""script"":[
            {""op"":""bg3d"",""set"":""комната""},
            {""op"":""say"",""text"":""в комнате""},
            {""op"":""bg"",""sprite_url"":""bg/улица.jpg""},
            {""op"":""say"",""text"":""на улице""},
            {""op"":""label"",""id"":""тут""},
            {""op"":""say"",""text"":""тут игрок и сохранился""}
        ]}";

        private const string ВошёлВКомнату = @"{""script"":[
            {""op"":""bg"",""sprite_url"":""bg/улица.jpg""},
            {""op"":""say"",""text"":""на улице""},
            {""op"":""bg3d"",""set"":""комната""},
            {""op"":""say"",""text"":""в комнате""},
            {""op"":""label"",""id"":""тут""},
            {""op"":""say"",""text"":""тут игрок и сохранился""}
        ]}";

        private static List<string> СобранныйКадр(string doc)
        {
            var s = new RecStage();
            var p = new LvnPlayer(LvnDocument.Parse(doc), s);
            var script = LvnDocument.Parse(doc).Script;
            int at = 0;
            for (int i = 0; i < script.Count; i++)
                if (script[i] is JObject c && (string)c["op"] == "label") { at = i; break; }
            p.ReplayVisuals(at);
            return s.Applied.Select(c => (string)c["op"])
                            .Where(op => op == "bg" || op == "bg3d").ToList();
        }

        [Test]
        public void ВышелИзОбъёмнойКомнаты_ВосстановлениеНеВозвращаетЕё()
        {
            var слот = СобранныйКадр(УшёлИзКомнаты);
            CollectionAssert.IsNotEmpty(слот, "стенд: в кадре не оказалось ни одной команды фона");
            Assert.AreEqual("bg", слот[слот.Count - 1],
                "последним в восстановленном кадре встал bg3d, хотя автор увёл игрока из комнаты "
                + "обычным bg — по контракту сцены объёмная декорация стоит до следующего bg, "
                + "и игрок возвращается в комнату, которую покинул. Собрано: "
                + string.Join(" → ", слот));
        }

        [Test]
        public void ВошёлВОбъёмнуюКомнату_ВосстановлениеОставляетЕё()
        {
            var слот = СобранныйКадр(ВошёлВКомнату);
            CollectionAssert.IsNotEmpty(слот, "стенд: в кадре не оказалось ни одной команды фона");
            Assert.AreEqual("bg3d", слот[слот.Count - 1],
                "последним встал нарисованный фон, хотя автор ввёл игрока в объёмную комнату — "
                + "восстановление потеряло декорацию. Собрано: " + string.Join(" → ", слот));
        }
    }
}
