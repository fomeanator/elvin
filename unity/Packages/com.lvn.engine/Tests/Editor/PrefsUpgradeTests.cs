using Lvn;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// НАСТРОЙКИ ЧУЖОЙ СБОРКИ НЕ ЛОМАЮТ ЭКРАН.
    ///
    /// <para>Настройки пишутся через ручки, а ручки зажимают значение пределами
    /// каталога. Но пределы принадлежат СБОРКЕ и меняются вместе с ней, а число
    /// лежит на устройстве с прошлого раза — и читалось как есть.</para>
    ///
    /// <para>Замер 05.09: прозрачность окна 0 (окно диалога исчезает, текст
    /// висит на голом фоне), масштаб текста 100 (интерфейс нечитаем), громкость
    /// −5, скорость печати 99 при пределе 3. Из такого состояния игрок не
    /// выберется: настройки открываются тем же интерфейсом, который сломан.
    /// Теперь зажим стоит и на чтении — одно правило, две двери.</para>
    /// </summary>
    public class PrefsUpgradeTests
    {
        private const string P = "lvn_pref_";

        private static void Забыть()
        {
            foreach (var k in new[] { "text_speed", "vol_music", "text_scale", "ui_scale",
                                      "dialog_opacity", "locale", "art_quality", "target_fps" })
                LvnKeep.Drop(P + k);
            LvnPrefs.Reload();
        }

        [SetUp]
        public void Подготовка() => Забыть();

        [TearDown]
        public void Уборка() => Забыть();

        /// Здоровые значения починка трогать не смеет.
        [Test]
        public void СвоиЗначенияОстаютсяКакБыли()
        {
            LvnKeep.Put(P + "text_speed", 2f);
            LvnKeep.Put(P + "vol_music", 0.5f);
            LvnKeep.Put(P + "dialog_opacity", 0.7f);
            LvnKeep.Put(P + "art_quality", "1440");
            LvnPrefs.Reload();

            Assert.AreEqual(2f, LvnPrefs.TextSpeed, 0.001f);
            Assert.AreEqual(0.5f, LvnPrefs.VolMusic, 0.001f);
            Assert.AreEqual(0.7f, LvnPrefs.DialogOpacity, 0.001f);
            Assert.AreEqual("1440", LvnPrefs.ArtQuality);
        }

        [Test]
        public void ЗначенияЧужойСборкиПриводятсяКПределам()
        {
            // Сборка сменила шкалы (это происходит: пределы живут в каталоге
            // настроек), а на устройстве лежат прежние числа. Плюс язык и
            // ступень качества, которых в этой сборке уже нет.
            LvnKeep.Put(P + "text_speed", 99f);
            LvnKeep.Put(P + "vol_music", -5f);
            LvnKeep.Put(P + "text_scale", 100f);
            LvnKeep.Put(P + "dialog_opacity", 0f);
            LvnKeep.Put(P + "locale", "эльфийский");
            LvnKeep.Put(P + "art_quality", "8k");
            LvnPrefs.Reload();

            TestContext.WriteLine($"скорость печати {LvnPrefs.TextSpeed}, громкость {LvnPrefs.VolMusic}, "
                                + $"масштаб текста {LvnPrefs.TextScale}, прозрачность окна {LvnPrefs.DialogOpacity}, "
                                + $"язык «{LvnPrefs.Locale}», ступень «{LvnPrefs.ArtQuality}»");

            Assert.LessOrEqual(LvnPrefs.TextSpeed, LvnSettingsCatalog.TextSpeedMax,
                "скорость печати из прежней сборки выше предела этой");
            Assert.GreaterOrEqual(LvnPrefs.TextSpeed, LvnSettingsCatalog.TextSpeedMin);
            Assert.GreaterOrEqual(LvnPrefs.VolMusic, 0f, "отрицательная громкость");
            Assert.LessOrEqual(LvnPrefs.VolMusic, 1f);
            Assert.LessOrEqual(LvnPrefs.TextScale, 2f,
                "масштаб текста вне ступеней — интерфейс нечитаем, а настройки открываются им же");
            Assert.GreaterOrEqual(LvnPrefs.DialogOpacity, LvnSettingsCatalog.BoxOpacityMin,
                "окно диалога стало прозрачным насквозь — текст висит на голом фоне");
            Assert.AreEqual("", LvnPrefs.ArtQuality,
                "ступень качества, которой в этой сборке нет, — это промах мимо варианта на каждом ассете");

            // Язык, которого в сборке нет, безвреден: каталог не найдётся и
            // строки покажутся как написаны в сценарии. Оставляем как есть —
            // выбор игрока переживёт возвращение языка в следующей сборке.
            Assert.AreEqual("эльфийский", LvnPrefs.Locale,
                "выбор языка стёрли — вернётся язык в сборку, а игрок уже переключен");
        }
    }
}
