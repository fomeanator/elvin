using System;
using NUnit.Framework;
using Lvn.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// ЕСТЬ ЛИ ИЗ ЧЕГО ВЫБИРАТЬ ЯЗЫК — <see cref="LvnLocale.Offered"/>.
    ///
    /// <para>Правило видимости строки языка стояло двумя написаниями: игровое
    /// меню проверяло длину списка, настройки оболочки — длину И <c>null</c>.
    /// Различала их не мысль, а внимательность, и держались обе на том, что
    /// список никогда не бывает пустым по-настоящему. Здесь закрепляется, что
    /// ответ один и что пустоту он переживает.</para>
    /// </summary>
    public class LocaleOfferedTests
    {
        private System.Collections.Generic.IReadOnlyList<string> _было;

        [SetUp] public void Setup() => _было = LvnPrefs.AvailableLocales;
        [TearDown] public void Teardown() => LvnPrefs.AvailableLocales = _было;

        [Test]
        public void Без_каталогов_выбора_нет()
        {
            LvnPrefs.AvailableLocales = Array.Empty<string>();
            Assert.IsFalse(LvnLocale.Offered);
        }

        [Test]
        public void Пустая_ссылка_это_тоже_нет_а_не_падение()
        {
            LvnPrefs.AvailableLocales = null;
            Assert.DoesNotThrow(() => { var _ = LvnLocale.Offered; },
                "игровое меню спрашивало длину напрямую и упало бы здесь");
            Assert.IsFalse(LvnLocale.Offered);
        }

        [Test]
        public void Один_каталог_уже_выбор()
        {
            LvnPrefs.AvailableLocales = new[] { "en" };
            Assert.IsTrue(LvnLocale.Offered,
                "оригинал плюс один перевод — это уже два варианта, и строку показать надо");
        }
    }
}
