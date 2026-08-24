using System.Reflection;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЭКРАН НАСТРОЕК ОБЯЗАН НАПОЛНЯТЬСЯ ПРИ ОТКРЫТИИ.
    ///
    /// <para>Rebuild() строит все строки (звук, громкости, аккаунт, версия…),
    /// но вызывается только из хука открытия. Живой случай: переопределение
    /// потерялось — партнёр открыл «Настройки» из хаба и увидел заголовок с
    /// кнопкой Close на пустом листе.</para>
    /// </summary>
    public class SettingsScreenTests
    {
        [Test]
        public void Rebuild_PopulatesTheRows()
        {
            var s = new SettingsScreen(null, null);
            s.Rebuild();
            // Звук + 4 громкости + Player ID + аккаунт + restore + версия — минимум 8.
            Assert.GreaterOrEqual(CountRows(s), 8,
                "настройки почти пусты — игрок увидит голый лист");
        }

        [Test]
        public void OnOpening_IsOverriddenToRebuild()
        {
            var m = typeof(SettingsScreen).GetMethod("OnOpening",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.AreEqual(typeof(SettingsScreen), m.DeclaringType,
                "OnOpening не переопределён — Rebuild никто не вызовет и экран будет пустым");
        }

        private static int CountRows(UnityEngine.UIElements.VisualElement root)
        {
            int n = 0;
            foreach (var child in root.Children()) n += 1 + CountRows(child);
            return n;
        }
    }
}
