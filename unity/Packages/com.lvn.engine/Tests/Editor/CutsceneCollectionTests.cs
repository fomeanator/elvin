using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// КОЛЛЕКЦИЯ ПРОЖИТЫХ СЦЕН — правило «запись это прохождение, а не сцена».
    ///
    /// <para>Держим его стражем, потому что склеить два прохождения в одну
    /// запись — ровно то, что хранилище делало раньше и что выглядит
    /// «оптимизацией»: id один, имя одно, зачем плодить. Затем, что в галерею
    /// смотрят не за списком сцен, а за тем, КАК они прошли: наряд, выбор и
    /// кадр у второго раза другие.</para>
    /// </summary>
    public class CutsceneCollectionTests
    {
        private const string Title = "test-cutscene-title";

        [SetUp]
        [TearDown]
        public void Clean() => LvnCutsceneStore.Clear(Title);

        [Test]
        public void ДваПроходаОднойСценыДаютДвеКарточки()
        {
            var first = LvnCutsceneStore.Lived(Title, "meet", "Знакомство", "ch0");
            var second = LvnCutsceneStore.Lived(Title, "meet", "Знакомство", "ch0");

            Assert.AreNotEqual(first, second, "у каждого прохождения свой адрес");
            var seens = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(2, seens.Count, "второй проход обязан лечь рядом, а не поверх");
            foreach (var seen in seens)
                Assert.AreEqual("meet", seen.Id, "метка сцены у обеих карточек одна — с неё их играют");
        }

        [Test]
        public void СвежееПрохождениеИдётПервым()
        {
            LvnCutsceneStore.Lived(Title, "a", "Первая", "ch0");
            LvnCutsceneStore.Lived(Title, "b", "Вторая", "ch0");

            var seens = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(2, seens.Count);
            Assert.GreaterOrEqual(seens[0].At, seens[1].At, "только что прожитое стоит на виду");
        }

        [Test]
        public void ПревьеЛожитсяВСвоюКарточку()
        {
            var first = LvnCutsceneStore.Lived(Title, "meet", "Знакомство", "ch0");
            var second = LvnCutsceneStore.Lived(Title, "meet", "Знакомство", "ch0");
            LvnCutsceneStore.Dress(Title, second, "/content/bg/second.jpg");

            var byKey = new Dictionary<string, LvnCutsceneStore.Seen>();
            foreach (var seen in LvnCutsceneStore.Seens(Title)) byKey[seen.Key] = seen;
            Assert.AreEqual("/content/bg/second.jpg", byKey[second].Poster);
            Assert.IsTrue(string.IsNullOrEmpty(byKey[first].Poster),
                          "кадр второго показа не смеет попасть в карточку первого");
        }

        [Test]
        public void КорзинаУбираетТолькоСвоюКарточку()
        {
            var first = LvnCutsceneStore.Lived(Title, "meet", "Знакомство", "ch0");
            var second = LvnCutsceneStore.Lived(Title, "meet", "Знакомство", "ch0");

            Assert.IsTrue(LvnCutsceneStore.Drop(Title, first));
            var seens = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(1, seens.Count);
            Assert.AreEqual(second, seens[0].Key, "уйти должно ровно то прохождение, где нажали");
            Assert.IsFalse(LvnCutsceneStore.Drop(Title, first), "выброшенного второй раз нет");
        }

        [Test]
        public void КоллекцияНеРастётБезКонца()
        {
            for (int i = 0; i < LvnCutsceneStore.Keep + 5; i++)
                LvnCutsceneStore.Lived(Title, "meet", "Знакомство", "ch0");

            Assert.AreEqual(LvnCutsceneStore.Keep, LvnCutsceneStore.Seens(Title).Count,
                            "предел держит галерею и место на диске");
        }

        [Test]
        public void ЗаписьПрежнегоОбразцаЧитаетсяКакПрохождение()
        {
            // Так выглядела коллекция до 09.09: ключ словаря = id сцены,
            // адреса прохождения в записи нет. Такие карточки обязаны
            // открываться и показываться, а не пропадать при обновлении.
            // Clean() в [SetUp] уже сбросил разобранное в памяти — читать
            // хранилище будет с диска.
            LvnKeep.Put(LvnKeep.Scoped("lvn.cutscenes.", Title),
                        "{\"meet\":{\"Id\":\"meet\",\"Name\":\"Знакомство\",\"Chapter\":\"ch0\"}}");

            var seens = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(1, seens.Count);
            Assert.AreEqual("meet", seens[0].Key, "адресом старой записи служит её ключ");
            Assert.AreEqual("meet", seens[0].Id);
        }
    }
}
