using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЛКИ ЛЕНТЫ ГЛАВНОЙ — договор о виде: пустая полка не исчезает, у
    /// заголовка счётчик, плитка и полоса одного роста, ритм отступов один
    /// на всех, замок гасит обложку, а не плитку, активная плитка светится,
    /// волна проявления укладывается в один срок. Всё меряется по стилям,
    /// без панели: договор — о числах и именах, а не о пикселях.
    /// </summary>
    public sealed class BrowseHubShelfTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
                => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
                => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private static LvnCollection Полка(string id, params string[] titles)
            => new LvnCollection { id = id, name = id, titles = new List<string>(titles) };

        private static BrowseHub Хаб(List<LvnCollection> полки, List<LvnTitle> новеллы, BrowseConfig cfg = null)
        {
            var h = new BrowseHub(cfg, new NoAssets());
            h.SetData(полки, новеллы);
            return h;
        }

        private static List<LvnTitle> ДвеНовеллы() => new List<LvnTitle>
        {
            new LvnTitle { id = "a", name = "Первая" },
            new LvnTitle { id = "b", name = "Вторая" },
        };

        [Test]
        public void ПустаяПолкаОстаётсяНаМесте()
        {
            // Пустой сборник исчезал целиком: игрок не знал, что раздел есть.
            var хаб = Хаб(new List<LvnCollection> { Полка("dates", "ещё-не-импортирована") }, ДвеНовеллы());

            var полки = хаб.Query(name: BrowseHub.ShelfName).ToList();
            Assert.AreEqual(2, полки.Count, "пустой сборник исчез из хаба целиком (вторая полка — «Новеллы» из сирот)");
            Assert.NotNull(полки[0].Q(name: BrowseHub.ShelfEmptyName), "на пустой полке нет сообщения о пустоте");
            Assert.IsNull(полки[1].Q(name: BrowseHub.ShelfEmptyName), "сообщение о пустоте встало на полку, где есть новеллы");

            Assert.AreEqual("0", полки[0].Q<Label>(name: BrowseHub.ShelfCountName)?.text, "счётчик пустой полки молчит");
            Assert.AreEqual("2", полки[1].Q<Label>(name: BrowseHub.ShelfCountName)?.text, "счётчик полки с новеллами");
        }

        [Test]
        public void СчётчикСчитаетТолькоИзвестныеНовеллы()
        {
            var хаб = Хаб(new List<LvnCollection> { Полка("exp", "a", "призрак", "b") }, ДвеНовеллы());

            var полка = хаб.Q(name: BrowseHub.ShelfName);
            Assert.AreEqual("2", полка.Q<Label>(name: BrowseHub.ShelfCountName)?.text);
            Assert.AreEqual(2, полка.Query(name: BrowseHub.ShelfCardName).ToList().Count);
        }

        [Test]
        public void СловоПустойПолкиБерётсяУАвтора()
        {
            var хаб = Хаб(new List<LvnCollection> { Полка("dates", "нет") }, ДвеНовеллы(),
                          new BrowseConfig { empty_text = "Скоро" });

            var пусто = хаб.Q(name: BrowseHub.ShelfEmptyName);
            Assert.AreEqual("Скоро", пусто.Q<Label>()?.text);
        }

        [Test]
        public void ПлиткаИПолосаОдногоРоста()
        {
            // Полоса выше или ниже плитки — обрезанный низ или чужой ползунок.
            var хаб = Хаб(new List<LvnCollection> { Полка("exp", "a", "b") }, ДвеНовеллы());

            var полка = хаб.Q(name: BrowseHub.ShelfName);
            var плитка = полка.Q(name: BrowseHub.ShelfCardName);
            var полоса = полка.Q<ScrollView>();
            Assert.AreEqual(BrowseHub.PosterH + BrowseHub.CaptionH, BrowseHub.ShelfCardHeight, 0.01f,
                "рост плитки — постер плюс цоколь подписи, одним числом");
            Assert.AreEqual(BrowseHub.ShelfCardHeight, полоса.style.height.value.value, 0.01f, "полоса не того роста, что плитка");
            Assert.AreEqual(BrowseHub.CardW, плитка.style.width.value.value, 0.01f, "ширина плитки не из ритма");
        }

        [Test]
        public void РитмПолокОдинНаВсех()
        {
            // Отступы подбирались по месту; теперь их три числа на все полки.
            var хаб = Хаб(new List<LvnCollection> { Полка("exp", "a", "b"), Полка("dates") },
                          ДвеНовеллы());

            var полки = хаб.Query(name: BrowseHub.ShelfName).ToList();
            for (int i = 0; i < полки.Count; i++)
            {
                var полка = полки[i];
                // Последний ряд поднят над нижним меню на величину, подобранную
                // Ильёй на живом экране (LastRowLift), — не ступень ритма.
                if (i < полки.Count - 1)
                    Assert.AreEqual(BrowseHub.ShelfGap, полка.style.marginBottom.value.value, 0.01f, "зазор между полками не из ритма");
                else
                    Assert.GreaterOrEqual(полка.style.marginBottom.value.value, BrowseHub.ShelfGap, "последняя полка ниже ритма");
                var шапка = полка.Q(name: BrowseHub.ShelfHeadName);
                Assert.NotNull(шапка, "у полки нет названной шапки");
                Assert.AreEqual(BrowseHub.HeadGap, шапка.style.marginBottom.value.value, 0.01f, "зазор под шапкой не из ритма");
                foreach (var плитка in полка.Query(name: BrowseHub.ShelfCardName).ToList())
                    Assert.AreEqual(BrowseHub.CardGap, плитка.style.marginRight.value.value, 0.01f, "зазор между плитками не из ритма");
            }
        }

        [Test]
        public void ВолнаПроявленияУкладываетсяВОдинСрок()
        {
            // Плитки проступают со сдвигом, но все видимые — за один срок
            // проявления: на медленном устройстве это намерение, а не тормоз.
            var план = BrowseHub.RevealPlan(count: 12, visible: 4);

            Assert.AreEqual(12, план.Length);
            Assert.AreEqual(0, план[0], "первая плитка идёт сразу");
            Assert.LessOrEqual(план[3], LvnMotion.Reveal, "последняя видимая плитка ждёт дольше срока проявления");
            Assert.LessOrEqual(план[1] - план[0], LvnMotion.StaggerMs, "шаг волны шире хореографии движка");
            Assert.Greater(план[1], план[0], "видимые плитки идут разом — волны нет");
            for (int i = 4; i < 12; i++)
                Assert.AreEqual(-1, план[i], $"плитка {i} за кромкой ждёт волну — ей проявляться незачем");

            var широкая = BrowseHub.RevealPlan(count: 20, visible: 20);
            Assert.LessOrEqual(широкая[19], LvnMotion.Reveal, "длинная полка растянула волну");

            Assert.AreEqual(0, BrowseHub.RevealPlan(count: 1, visible: 4)[0]);
            Assert.AreEqual(0, BrowseHub.RevealPlan(count: 0, visible: 4).Length);
        }

        [Test]
        public void ВолнаСчитаетВидимыеПлиткиПоШиринеХаба()
        {
            Assert.AreEqual(3, BrowseHub.VisibleCardsAt(LvnPanel.ReferenceWidth), "на опорной ширине видны две плитки и край третьей");
            float альбом = LvnPanel.ReferenceHeight * 16f / 9f;
            Assert.Greater(BrowseHub.VisibleCardsAt(альбом), BrowseHub.VisibleCardsAt(LvnPanel.ReferenceWidth));
            Assert.AreEqual(1, BrowseHub.VisibleCardsAt(0f), "нулевая ширина — всё равно хотя бы одна плитка");
        }

        [Test]
        public void ЗамокГаситОбложкуАНеПлитку()
        {
            // Полупрозрачная плитка целиком гасила и подпись: название
            // закрытой новеллы не читалось. Гаснет обложка, слова остаются.
            var новеллы = new List<LvnTitle>
            {
                new LvnTitle { id = "a", name = "Открытая" },
                new LvnTitle { id = "b", name = "Закрытая", unlock = "false" },
            };
            var хаб = Хаб(new List<LvnCollection> { Полка("exp", "a", "b") }, новеллы);

            var плитки = хаб.Query(name: BrowseHub.ShelfCardName).ToList();
            Assert.IsNull(плитки[0].Q(name: BrowseHub.ShelfLockName), "открытая плитка под вуалью замка");
            Assert.NotNull(плитки[1].Q(name: BrowseHub.ShelfLockName), "закрытая плитка без вуали на обложке");
            // Проявление стартует с нуля у всех плиток разом; закрытая не темнее
            // открытой — гаснет обложка, а не плитка.
            Assert.AreEqual(плитки[0].style.opacity.value, плитки[1].style.opacity.value, 1e-4f,
                "закрытая плитка погашена целиком — подпись не читается");
        }

        [Test]
        public void АктивнаяПлиткаСветится()
        {
            // «Читаю сейчас» — единственная плитка с весом: свечение под
            // ней, а не у всех подряд.
            var новеллы = ДвеНовеллы();
            var глава = new LvnChapter { id = "ch1", number = 1 };
            новеллы[1].seasons = new List<LvnSeason> { new LvnSeason { chapters = new List<LvnChapter> { глава } } };
            try
            {
                LvnProgress.StartChapter(новеллы[1], глава);
                var хаб = Хаб(new List<LvnCollection> { Полка("exp", "a", "b") }, новеллы);

                var плитки = хаб.Query(name: BrowseHub.ShelfCardName).ToList();
                Assert.IsNull(плитки[0].Q(name: BrowseHub.ShelfGlowName), "непочатая новелла светится");
                Assert.NotNull(плитки[1].Q(name: BrowseHub.ShelfGlowName), "начатая новелла без свечения");
            }
            finally { LvnProgress.ClearCurrent(новеллы[1]); }
        }
    }
}
