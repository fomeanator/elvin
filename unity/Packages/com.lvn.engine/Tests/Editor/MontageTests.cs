using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// Монтажёр: список сверяется поштучно, а не пересобирается. Главное, что
    /// здесь проверяется, — ЭКЗЕМПЛЯР ПЕРЕЖИВАЕТ ОБНОВЛЕНИЕ: вместе с ним
    /// переживают фокус, скролл, начатая анимация и загруженная картинка.
    public class MontageTests
    {
        private VisualElement _host;
        private int _created;

        [SetUp]
        public void Setup()
        {
            _host = new VisualElement();
            _created = 0;
        }

        private VisualElement Create(string item)
        {
            _created++;
            return new Label(item);
        }

        private void Sync(params string[] model)
            => LvnMontage.Sync(_host, model, k => k, Create,
                (el, item) => ((Label)el).text = item);

        private List<string> Texts()
        {
            var outp = new List<string>();
            foreach (var c in _host.Children()) outp.Add(((Label)c).text);
            return outp;
        }

        [Test]
        public void WhatStaysIsTheSameElement_NotARebuiltOne()
        {
            Sync("a", "b", "c");
            var b = _host[1];
            Assert.AreEqual(3, _created);

            Sync("a", "b", "c");   // модель не изменилась

            Assert.AreEqual(3, _created, "ничего не изменилось — создавать нечего");
            Assert.AreSame(b, _host[1],
                "элемент пересоздан: с ним пропали бы фокус, скролл и загруженная картинка");
        }

        [Test]
        public void OnlyTheNewOneIsCreated()
        {
            Sync("a", "b");
            var a = _host[0];

            Sync("a", "b", "c");

            Assert.AreEqual(3, _created, "создан ровно один — третий");
            Assert.AreSame(a, _host[0]);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Texts());
        }

        [Test]
        public void WhatLeftTheModelLeavesTheScreen()
        {
            Sync("a", "b", "c");
            Sync("a", "c");

            CollectionAssert.AreEqual(new[] { "a", "c" }, Texts());
            Assert.AreEqual(2, _host.childCount);
        }

        [Test]
        public void OrderFollowsTheModel_WithoutRecreating()
        {
            Sync("a", "b", "c");
            var a = _host[0];
            var c = _host[2];

            Sync("c", "a", "b");

            Assert.AreEqual(3, _created, "перестановка — не повод рождать заново");
            CollectionAssert.AreEqual(new[] { "c", "a", "b" }, Texts());
            Assert.AreSame(c, _host[0]);
            Assert.AreSame(a, _host[1]);
        }

        [Test]
        public void UpdateReachesTheSurvivors()
        {
            LvnMontage.Sync(_host, new[] { "x" }, _ => "single", Create,
                (el, item) => ((Label)el).text = item);
            LvnMontage.Sync(_host, new[] { "y" }, _ => "single", Create,
                (el, item) => ((Label)el).text = item);

            Assert.AreEqual(1, _created, "тот же ключ — тот же элемент");
            CollectionAssert.AreEqual(new[] { "y" }, Texts(), "но содержимое обновилось");
        }

        // Воздух, разделители, служебные слои экран ставит сам. Монтажёр их не
        // заводил — и не имеет права уносить.
        [Test]
        public void ElementsTheScreenPutThereItselfAreLeftAlone()
        {
            var air = new VisualElement { name = "air" };
            _host.Add(air);

            Sync("a", "b");
            Sync("b");

            Assert.IsTrue(_host.Contains(air), "чужой элемент унесён вместе со своими");
        }

        [Test]
        public void AnEmptyModelClearsOnlyItsOwn()
        {
            var air = new VisualElement { name = "air" };
            _host.Add(air);
            Sync("a", "b");

            Sync();

            Assert.AreEqual(1, _host.childCount);
            Assert.IsTrue(_host.Contains(air));
        }

        [Test]
        public void NothingIsNotAnError()
        {
            Assert.DoesNotThrow(() => LvnMontage.Sync<string>(null, new[] { "a" }, k => k, Create));
            Assert.DoesNotThrow(() => LvnMontage.Sync(_host, (IReadOnlyList<string>)null, k => k, Create));
            Assert.DoesNotThrow(() => LvnMontage.Sync(_host, new[] { "a" }, null, Create));
            Assert.DoesNotThrow(() => LvnMontage.RevealWhenLaidOut(null));
        }

        // Элемент без ключа модель описать не может — молча пропускаем, иначе
        // один безымянный пункт уносил бы с экрана всё остальное.
        [Test]
        public void ItemsWithoutAKeyAreSkipped()
        {
            LvnMontage.Sync(_host, new[] { "a", "", "b" }, k => k, Create);

            CollectionAssert.AreEqual(new[] { "a", "b" }, Texts());
        }

        [Test]
        public void ADuplicateKeyDoesNotSplitIntoTwoElements()
        {
            LvnMontage.Sync(_host, new[] { "a", "a", "b" }, k => k, Create);

            Assert.AreEqual(2, _host.childCount, "первый победил, второй — тот же ключ");
            CollectionAssert.AreEqual(new[] { "a", "b" }, Texts());
        }
    }
}
