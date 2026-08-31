using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПРЕЖНЕЕ ИМЯ СЛОВА: перевод, написанный до переезда на приставки,
    /// продолжает работать.
    ///
    /// <para>Меню сцены спрашивало голые имена (<c>close</c>, <c>gallery</c>,
    /// <c>window_opacity</c>) — так они и попали в манифесты авторов. Экраны
    /// оболочки спрашивают с приставкой, не находили и показывали английские
    /// умолчания: «Закрыть» в меню главы и Close в оболочке — одна кнопка,
    /// названная дважды.</para>
    /// </summary>
    public class WordAliasTests
    {
        [TearDown]
        public void Убрать()
        {
            LvnWords.Learn(null, null, null);
            LvnWords.Translate(null);
        }

        private static Dictionary<string, string> Меню() => new Dictionary<string, string>
        {
            ["close"] = "Закрыть",
            ["gallery"] = "Галерея",
            ["window_opacity"] = "Прозрачность окна",
        };

        [Test]
        public void ОболочкаНаходитСловоПодПрежнимИменем()
        {
            LvnWords.Learn(null, Меню());
            Assert.AreEqual("Закрыть", LvnWords.Of("common.close", "Close"));
            Assert.AreEqual("Галерея", LvnWords.Of("nav.gallery", "Gallery"));
            Assert.AreEqual("Прозрачность окна", LvnWords.Of("settings.box_opacity", "Box opacity"));
        }

        [Test]
        public void КанонСильнееПрежнегоИмени()
        {
            LvnWords.Learn(new Dictionary<string, string> { ["common.close"] = "Свернуть" }, Меню());
            Assert.AreEqual("Свернуть", LvnWords.Of("common.close", "Close"),
                "автор, назвавший слово каноном, имел в виду именно его");
        }

        [Test]
        public void БезПереводаОстаётсяУмолчаниеВызывающего()
        {
            LvnWords.Learn(null, null);
            Assert.AreEqual("Close", LvnWords.Of("common.close", "Close"));
        }

        [Test]
        public void СовпадениеХвостовНеСчитаетсяПсевдонимом()
        {
            // saves.auto — автосохранение, голое auto — кнопка автопрокрутки.
            // Правило «взять часть после точки» подменило бы одно другим.
            Assert.AreEqual("autosave", LvnWordAliases.Legacy("saves.auto"));
            Assert.IsNull(LvnWordAliases.Legacy("shop.title"), "псевдоним — только названная пара");
        }

        [Test]
        public void ПсевдонимыНеЗацикленыИНеПовторяются()
        {
            var seen = new HashSet<string>();
            foreach (var kv in LvnWordAliases.All)
            {
                Assert.AreNotEqual(kv.Key, kv.Value, $"{kv.Key}: сам себе прежнее имя");
                Assert.IsNull(LvnWordAliases.Legacy(kv.Value), $"{kv.Value}: цепочка псевдонимов");
                Assert.IsTrue(seen.Add(kv.Value), $"{kv.Value}: два канона на одно прежнее имя");
            }
        }
    }
}
