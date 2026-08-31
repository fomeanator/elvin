using System.Collections.Generic;
using System.Linq;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// КАТАЛОГ НАСТРОЕК — один набор на два экрана.
    ///
    /// <para>Набор был записан дважды (меню сцены и экран оболочки), и имена уже
    /// разошлись: прозрачность окна звалась <c>settings.box_opacity</c> и
    /// <c>window_opacity</c>, «пропускать прочитанное» — <c>settings.skip_read</c>
    /// и <c>skip_read_only</c>. Переводчик переводил половину, и игрок видел
    /// разные слова в зависимости от того, откуда открыл настройки.</para>
    /// </summary>
    public class SettingsCatalogTests
    {
        [TearDown]
        public void Убрать() => LvnWords.Learn(null, null, null);

        [Test]
        public void УКаждойНастройкиЕстьКлючИмяИРучка()
        {
            var all = LvnSettingsCatalog.Reading().Concat(LvnSettingsCatalog.Audio(false)).ToList();
            Assert.IsNotEmpty(all);
            foreach (var d in all)
            {
                Assert.IsTrue(d.Key.StartsWith("settings."), $"{d.Key}: ключ настройки — из пространства settings.");
                Assert.IsNotEmpty(d.English, $"{d.Key}: английское умолчание обязательно");
                if (d.Kind == LvnSettingKind.Switch)
                {
                    Assert.IsNotNull(d.Flag, $"{d.Key}: читать нечем");
                    Assert.IsNotNull(d.SetFlag, $"{d.Key}: писать некуда");
                }
                else
                {
                    Assert.IsNotNull(d.Num, $"{d.Key}: читать нечем");
                    Assert.IsNotNull(d.SetNum, $"{d.Key}: писать некуда");
                    Assert.Less(d.Min, d.Max, $"{d.Key}: пределы вывернуты");
                }
            }
        }

        [Test]
        public void КлючиНеПовторяются()
        {
            var keys = LvnSettingsCatalog.Reading().Concat(LvnSettingsCatalog.Audio(false))
                .Select(d => d.Key).ToList();
            CollectionAssert.AllItemsAreUnique(keys, "две настройки под одним ключом — это одна настройка");
        }

        [Test]
        public void ПростойРежимЗвукаВедётВсёОднимДвижком()
        {
            var simple = LvnSettingsCatalog.Audio(true);
            Assert.AreEqual(2, simple.Count, "музыка и звук — два ползунка");

            LvnPrefs.VolAmbient = 0.1f; LvnPrefs.VolVoice = 0.1f;
            simple[1].SetNum(0.7f);
            Assert.AreEqual(0.7f, LvnPrefs.VolSfx, 0.001f);
            Assert.AreEqual(0.7f, LvnPrefs.VolAmbient, 0.001f, "эмбиент идёт следом");
            Assert.AreEqual(0.7f, LvnPrefs.VolVoice, 0.001f, "и голос тоже");
        }

        [Test]
        public void ПодписьБерётсяПоКанону()
        {
            var d = LvnSettingsCatalog.Reading().First(x => x.Key == "settings.box_opacity");
            Assert.AreEqual("Box opacity", LvnSettingsCatalog.Label(d), "без словаря — английское движка");

            LvnWords.Learn(new Dictionary<string, string> { ["settings.box_opacity"] = "Прозрачность окна" }, null);
            Assert.AreEqual("Прозрачность окна", LvnSettingsCatalog.Label(d));
        }

        [Test]
        public void ПрежнийКлючСценыПродолжаетРаботать()
        {
            // Словарь автора, переведший старое имя, не должен обнулиться
            // переездом на канонический ключ.
            var d = LvnSettingsCatalog.Reading().First(x => x.Key == "settings.box_opacity");
            LvnWords.Learn(new Dictionary<string, string> { ["window_opacity"] = "Прозрачность" }, null);
            Assert.AreEqual("Прозрачность", LvnSettingsCatalog.Label(d),
                "канон спрашивается первым, прежний ключ — вторым");
        }
    }
}
