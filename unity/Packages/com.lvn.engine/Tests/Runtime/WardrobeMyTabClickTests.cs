using System.Collections;
using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ЛЕНТА «МОЁ» ОТЗЫВАЕТСЯ НА ТАП — всегда, а не только сразу после
    /// открытия листа.
    ///
    /// <para>Монтажёр сверяет карточки по ключу «ось/значение», и ключ у «Моё»
    /// и у раздела ОДИН И ТОТ ЖЕ: один элемент служит обеим лентам. Пока
    /// обработчиков было два — свой рождался только при СОЗДАНИИ карточки, —
    /// переиспользованная карточка приходила с чужим, и «Моё» немело: игрок
    /// заходил в «Фон», возвращался и тыкал в пустоту (Илья 08.09,
    /// «после переключения перса строка в моём становится некликабельной»).</para>
    ///
    /// <para>Тап здесь НАСТОЯЩИЙ, поэтому проверка живёт в PlayMode: доставка
    /// событий UITK идёт через панель, и без неё <c>SendEvent</c> молча
    /// не делает ничего — то есть зелёный тест не значил бы ровно ничего.</para>
    /// </summary>
    public class WardrobeMyTabClickTests
    {
        private const string Hero = "test_mytab_hero";
        private GameObject _go;
        private VisualElement _root;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("wardrobe-stage", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _root = doc.rootVisualElement;
        }

        [TearDown]
        public void TearDown()
        {
            LvnWardrobe.ClearPreview(Hero);
            LvnWardrobe.Clear(Hero);
            Lvn.Services.LvnWallet.ResetLocal();
            Object.Destroy(_go);
        }

        [UnityTest]
        public IEnumerator MyTab_CardStaysClickable_AfterVisitingItsSection()
        {
            var prevUrl = Lvn.Services.LvnBackend.BaseUrl;
            Lvn.Services.LvnBackend.BaseUrl = ""; // кошелёк без сети
            Lvn.Services.LvnWallet.ResetLocal();

            var earn = Lvn.Services.LvnWallet.EarnAsync("crystals", 100, "test");
            while (!earn.IsCompleted) yield return null;
            var buy = Lvn.Services.LvnWallet.SpendAsync("crystals", 20, "wardrobe",
                LvnWardrobe.Sku("menu", "backdrop", "cloister_snow"));
            while (!buy.IsCompleted) yield return null;

            var sheet = new WardrobeSheet(new WardrobeConfig { confirm_text = "Выбрать" }, new NoAssets());
            sheet.SetContent(Manifest());
            _root.Add(sheet);
            yield return null;

            sheet.BuildFor(Hero);
            sheet.GoTab("backdrop");            // карточки рождаются в ленте раздела
            sheet.GoTab(WardrobeSheet.AllTab);  // «Моё» переиспользует ТЕ ЖЕ элементы
            yield return null;

            var card = CardNamed(sheet, "Снежная обитель");
            Assert.IsNotNull(card, "купленный фон стоит в «Моё»");
            LvnWardrobe.ClearPreview(Hero);      // тап обязан примерить САМ

            using (var click = ClickEvent.GetPooled())
            {
                click.target = card;
                card.SendEvent(click);
            }
            yield return null;

            Assert.IsTrue(LvnWardrobe.Previewed(Hero).TryGetValue("backdrop", out var got),
                "тап по карточке «Моё» примеряет — а не проваливается в никуда");
            Assert.AreEqual("cloister_snow", got);

            Lvn.Services.LvnBackend.BaseUrl = prevUrl;
        }

        // ── фикстура ──
        /// <summary>Ассетов нет и не надо: проверяется доставка тапа, а не арт.
        /// Соседи по PlayMode держат такую же пустышку у себя — общая живёт в
        /// редакторной сборке тестов, а сюда её не дотянуть.</summary>
        private sealed class NoAssets : ILvnAssets
        {
            public System.Threading.Tasks.Task<Sprite> LoadSpriteAsync(string url, System.Threading.CancellationToken ct)
                => System.Threading.Tasks.Task.FromResult<Sprite>(null);
            public System.Threading.Tasks.Task<AudioClip> LoadAudioAsync(string url, System.Threading.CancellationToken ct)
                => System.Threading.Tasks.Task.FromResult<AudioClip>(null);
            public System.Threading.Tasks.Task PreloadAsync(IReadOnlyList<string> urls, string kind, System.Threading.CancellationToken ct)
                => System.Threading.Tasks.Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private static VisualElement CardNamed(VisualElement root, string title)
        {
            VisualElement found = null;
            void Walk(VisualElement el)
            {
                if (found != null) return;
                if (el is Label l && el.name == "card-name" && l.text == title)
                { found = el.parent?.parent; return; }
                foreach (var c in el.Children()) Walk(c);
            }
            Walk(root);
            return found;
        }

        private static LvnManifest Manifest() => new LvnManifest
        {
            sprites = new Dictionary<string, LvnSpriteEntity>
            {
                [Hero] = new LvnSpriteEntity
                {
                    name = "Странница",
                    layers = new List<LvnLayer> { new LvnLayer { id = "body", url = "/x/body.png" } },
                    wardrobe = new Dictionary<string, LvnWardrobeSlot>
                    {
                        ["backdrop"] = new LvnWardrobeSlot
                        {
                            name = "Фон",
                            items = new List<LvnWardrobeItem>
                            {
                                new LvnWardrobeItem { value = "cloister_snow", name = "Снежная обитель",
                                                      currency = "crystals", price = 20 },
                                new LvnWardrobeItem { value = "dead_woods", name = "Туманная чаща",
                                                      currency = "crystals", price = 20 },
                            },
                        },
                        ["outfit"] = new LvnWardrobeSlot
                        {
                            name = "Платье",
                            items = new List<LvnWardrobeItem>
                            {
                                new LvnWardrobeItem { value = "plain", name = "Простое" },
                            },
                        },
                    },
                },
            },
        };
    }
}
