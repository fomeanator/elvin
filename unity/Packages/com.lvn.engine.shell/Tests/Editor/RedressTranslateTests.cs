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
    /// СМЕНА ЯЗЫКА ДОХОДИТ ДО КАЖДОГО ЭКРАНА, А НЕ ТОЛЬКО ДО ТЕКСТА ГЛАВЫ.
    ///
    /// <para>Пробел, который Илья находил трижды подряд (28.08): реплики и
    /// списки переключались, а «Профиль», «Магазин», название полосы «Новеллы»
    /// и кнопка «Играть» оставались на прежнем языке. Каждый раз это выглядело
    /// как новая мелочь, а причина была одна на всех.</para>
    ///
    /// <para>Причин, если точнее, две, и обе системные. Первая: обход дома
    /// переодевания ОСТАНАВЛИВАЛСЯ на экране, умеющем переодеться («он
    /// пересобрал детей сам»), — а тот пересобирает тело и не трогает шапку, где
    /// и живут привязанные подписи. Вторая: заголовок раздела принимал готовую
    /// СТРОКУ, и связь со словарём обрывалась в момент сборки.</para>
    ///
    /// <para>Тесты ниже проверяют не механику дома, а обещание игроку: положил
    /// перевод — увидел его на открытом экране. Поэтому они строят настоящие
    /// экраны и ищут слово в дереве, а не спрашивают у словаря.</para>
    /// </summary>
    public sealed class RedressTranslateTests
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

        [TearDown]
        public void Clear()
        {
            // Словарь и перевод глобальны: не сняв их, следующий тест читал бы
            // чужой язык.
            LvnWords.Translate(null);
            LvnWords.Learn(null, null);
        }

        // Есть ли ГДЕ-НИБУДЬ под этим корнем подпись с таким текстом.
        private static bool HasText(VisualElement root, string text)
        {
            var pending = new Stack<VisualElement>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var el = pending.Pop();
                if (el is TextElement t && t.text == text) return true;
                for (int i = 0; i < el.childCount; i++) pending.Push(el[i]);
            }
            return false;
        }

        private static VisualElement Rooted(VisualElement screen)
        {
            var root = new VisualElement();
            root.Add(screen);
            return root;
        }

        [Test]
        public void ЗаголовокМагазинаПереводитсяНаОткрытомЭкране()
        {
            var root = Rooted(new PackShopScreen(new NoAssets()));
            LvnWords.Translate(new Dictionary<string, string> { { "shop.title", "ЛАВКА" } });
            LvnRedress.All(root);
            Assert.IsTrue(HasText(root, "ЛАВКА"),
                "заголовок магазина собран в шапке и остался на прежнем языке — "
                + "ровно та жалоба, с которой начался этот тест");
        }

        [Test]
        public void ЗаголовокПрофиляПереводитсяНаОткрытомЭкране()
        {
            var root = Rooted(new ProfileScreen(new NoAssets()));
            LvnWords.Translate(new Dictionary<string, string> { { "profile.title", "ЛИЧНОЕ" } });
            LvnRedress.All(root);
            Assert.IsTrue(HasText(root, "ЛИЧНОЕ"));
        }

        [Test]
        public void ПолосаНеразложенныхНовеллПереводится()
        {
            var hub = new BrowseHub(null, new NoAssets());
            // Новелла, не попавшая ни в один сборник, — та самая полоса
            // «Новеллы», чьё имя раньше застывало в модели при сборке ленты.
            hub.SetData(null, new List<LvnTitle> { new LvnTitle { id = "t1", name = "История" } });
            var root = Rooted(hub);
            LvnWords.Translate(new Dictionary<string, string> { { "hub.library", "БИБЛИОТЕКА" } });
            LvnRedress.All(root);
            Assert.IsTrue(HasText(root, "БИБЛИОТЕКА"),
                "название служебной полосы вычислялось строкой и уезжало в данные, "
                + "а данные переодевание не пересобирает");
        }

        [Test]
        public void КнопкаИгратьВВитринномКадреПереводится()
        {
            var hub = new BrowseHub(null, new NoAssets());
            hub.SetData(null, new List<LvnTitle> { new LvnTitle { id = "t1", name = "История" } });
            var root = Rooted(hub);
            LvnWords.Translate(new Dictionary<string, string> { { "hub.play", "НАЧАТЬ" } });
            LvnRedress.All(root);
            Assert.IsTrue(HasText(root, "НАЧАТЬ"),
                "кнопка привязана к словарю, но обход не заходил внутрь экрана, "
                + "умеющего переодеваться");
        }

        [Test]
        public void ВкладкиОсейГардеробаПереводятсяБезПерезахода()
        {
            // Живая жалоба (Илья, 28.08): «гардероб переводится, только если
            // уйти на другую страницу и вернуться». Вкладки осей собирает
            // BuildFor — он зовётся при смене персонажа, а не при смене языка.
            var sheet = new WardrobeSheet(null, new NoAssets());
            sheet.SetContent(new LvnManifest
            {
                sprites = new Dictionary<string, LvnSpriteEntity>
                {
                    ["hero"] = new LvnSpriteEntity
                    {
                        wardrobe = new Dictionary<string, LvnWardrobeSlot>
                        {
                            ["outfit"] = new LvnWardrobeSlot
                            {
                                name = "Наряд",
                                items = new List<LvnWardrobeItem> { new LvnWardrobeItem { value = "dress" } }
                            }
                        }
                    }
                }
            });
            sheet.BuildFor("hero");
            var root = Rooted(sheet);
            LvnWords.Translate(new Dictionary<string, string> { { "axis.outfit", "OUTFIT" } });
            LvnRedress.All(root);
            Assert.IsTrue(HasText(root, "OUTFIT"),
                "подпись вкладки оси осталась на прежнем языке — её пересобирает "
                + "только перезаход в гардероб");
        }

        [Test]
        public void ПривязаннаяПодписьВнутриПереодевающегосяЭкранаПеречитывается()
        {
            // Суть первой причины, отдельно от любого экрана: раньше обход
            // доходил до умеющего переодеться и разворачивался.
            var screen = new SelfDressing();
            var root = Rooted(screen);
            var label = LvnRedress.Bind(new Label(), () => LvnWords.Of("k", "по умолчанию"));
            screen.Add(label);
            LvnWords.Translate(new Dictionary<string, string> { { "k", "переведено" } });
            LvnRedress.All(root);
            Assert.AreEqual("переведено", label.text);
        }

        private sealed class SelfDressing : VisualElement, ILvnRedress
        {
            public void Redress() { }   // тело пересобирает, шапку — нет
        }
    }
}
