using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ВЫБОР ЯЗЫКА: «авто», оригинал, переводы.
    ///
    /// <para>Главное, что здесь стережётся, — обратимость. Раньше язык
    /// устройства подставлял хост и ЗАПИСЫВАЛ его в выбор игрока: «не выбирал»
    /// становилось неотличимо от «выбрал», и вернуться к языку системы было
    /// нечем. Тесты держат «авто» полноправным вариантом.</para>
    /// </summary>
    public sealed class LvnLocaleTests
    {
        private IReadOnlyList<string> _langs;

        [SetUp]
        public void Save() => _langs = LvnPrefs.AvailableLocales;

        [TearDown]
        public void Restore()
        {
            LvnPrefs.AvailableLocales = _langs;
            LvnPrefs.Locale = "";
        }

        // Язык системы известен и у новеллы есть такой каталог — «авто»
        // предлагается и действует. Тест не знает, на каком языке машина
        // прогона, поэтому спрашивает паспорт устройства.
        [Test]
        public void Auto_ResolvesToTheDeviceLanguageWhenTheNovelHasIt()
        {
            var sys = LvnDeviceProfile.SystemLocale;
            if (string.IsNullOrEmpty(sys)) Assert.Ignore("язык системы не определён");

            LvnPrefs.AvailableLocales = new[] { sys, "xx" };
            LvnLocale.Chosen = LvnLocale.Auto;

            Assert.AreEqual(sys, LvnLocale.Effective, "«авто» — это язык устройства");
            CollectionAssert.Contains(LvnLocale.Options(), LvnLocale.Auto,
                "вариант «авто» показывается, когда новелле есть что предложить системе");
        }

        // Каталога на языке системы нет — «авто» не обещает того, чего не будет:
        // ни в ряду вариантов, ни в действующем языке.
        [Test]
        public void Auto_FallsBackToTheOriginalAndHidesWhenNothingMatches()
        {
            LvnPrefs.AvailableLocales = new[] { "xx" };
            LvnLocale.Chosen = LvnLocale.Auto;

            Assert.AreEqual(LvnLocale.Original, LvnLocale.Effective,
                "нечего предложить системе — остаётся авторский текст");
            CollectionAssert.DoesNotContain(LvnLocale.Options(), LvnLocale.Auto,
                "вариант, который ничего не меняет, игроку не показывают");
        }

        // Выбор игрока сильнее устройства — и возвращается обратно в «авто».
        [Test]
        public void ChoiceWinsOverTheDeviceAndIsReversible()
        {
            LvnPrefs.AvailableLocales = new[] { "en", "ru" };

            LvnLocale.Chosen = "ru";
            Assert.AreEqual("ru", LvnLocale.Effective, "выбранный язык сильнее системного");

            LvnLocale.Chosen = LvnLocale.Auto;
            Assert.AreEqual(LvnLocale.Auto, LvnLocale.Chosen,
                "в «авто» можно вернуться — иначе подстановка работала бы один раз в жизни");
        }

        // Каталог убрали из манифеста, а выбор игрока остался — показываем
        // авторский текст, а не ключи вместо реплик.
        [Test]
        public void StaleChoiceFallsBackToTheOriginal()
        {
            LvnPrefs.AvailableLocales = new[] { "en" };
            LvnLocale.Chosen = "de";
            Assert.AreEqual(LvnLocale.Original, LvnLocale.Effective);
        }

        // Круг вариантов один на оба экрана: меню главы перебирает то же, что
        // показывает ряд в настройках оболочки.
        [Test]
        public void NextWalksTheSameRingTheSettingsRowShows()
        {
            LvnPrefs.AvailableLocales = new[] { "en", "ru" };
            var ring = LvnLocale.Options();

            var at = ring[0];
            for (int i = 0; i < ring.Count; i++) at = LvnLocale.Next(at);
            Assert.AreEqual(ring[0], at, "полный круг возвращается к началу");

            Assert.AreEqual(ring[0], LvnLocale.Next("zz"),
                "забытый код не выкидывает игрока из круга");
        }
    }
}
