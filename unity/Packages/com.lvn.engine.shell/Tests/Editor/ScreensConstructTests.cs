using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// КАЖДЫЙ ЭКРАН ОБОЛОЧКИ СТРОИТСЯ, НЕ БРОСАЯ.
    ///
    /// <para>Пробел, который стоил живого бута (28.08). Кружок загрузок начал
    /// сам следить за вырезом камеры, подписка применяла отступ немедленно — а
    /// поле, которому она его применяла, конструктор создавал десятью строками
    /// ниже. Исключение из конструктора ловить некому: упал не кружок, а ВЕСЬ
    /// бут, и игра не дошла до меню.</para>
    ///
    /// <para>975 тестов при этом были зелёными: они проверяли поведение домов, а
    /// экраны никто не строил. Самая дешёвая проверка на свете — «просто создай
    /// его» — не была написана, потому что казалась ничего не проверяющей.</para>
    ///
    /// <para>Конфигурация — null: так экраны и создаются, пока новелла не дала
    /// своих настроек, и именно этот путь проходит бут первым.</para>
    /// </summary>
    public sealed class ScreensConstructTests
    {
        // Ассеты-пустышка: экраны при сборке ничего не грузят, а тесты движка
        // живут в другой сборке — тащить их сюда ради двух методов незачем.
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
                => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
                => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private static IEnumerable<TestCaseData> Screens()
        {
            var a = new NoAssets();
            yield return Case("BootScreen", () => new BootScreen(null, a));
            yield return Case("AuthScreen", () => new AuthScreen(null, a));
            yield return Case("BrowseHub", () => new BrowseHub(null, a));
            yield return Case("CgGalleryScreen", () => new CgGalleryScreen(a));
            yield return Case("ChapterEndScreen", () => new ChapterEndScreen(null, a));
            yield return Case("DailyRewardsScreen", () => new DailyRewardsScreen(a));
            yield return Case("DownloadHud", () => new DownloadHud());
            yield return Case("GameHud", () => new GameHud(null, a));
            yield return Case("LeaderboardScreen", () => new LeaderboardScreen(a));
            yield return Case("LoadingScreen", () => new LoadingScreen(null, a));
            yield return Case("LvnTopBar", () => new LvnTopBar());
            yield return Case("PackShopScreen", () => new PackShopScreen(a));
            yield return Case("PackShopScreen (модаль)", () => new PackShopScreen(a, modal: true));
            yield return Case("PopupScreen", () => new PopupScreen(null));
            yield return Case("ProfileScreen", () => new ProfileScreen(a));
            yield return Case("SettingsScreen", () => new SettingsScreen(null, a));
            yield return Case("SkinShopScreen", () => new SkinShopScreen(a));
            yield return Case("TitleCard", () => new TitleCard(null, a));
            yield return Case("TitleDetailScreen", () => new TitleDetailScreen(a));
            yield return Case("WardrobeTabScreen", () => new WardrobeTabScreen(new LvnManifest(), a));
        }

        private static TestCaseData Case(string name, Func<object> make)
            => new TestCaseData(make).SetName("Строится: " + name);

        [TestCaseSource(nameof(Screens))]
        public void ScreenConstructsWithoutThrowing(Func<object> make)
        {
            object screen = null;
            Assert.DoesNotThrow(() => screen = make(),
                "конструктор экрана бросил — в живой игре это роняет ВЕСЬ бут, " +
                "потому что перехватить исключение из конструктора некому");
            Assert.IsNotNull(screen);
        }
    }
}
