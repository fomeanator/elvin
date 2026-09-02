using System.Collections.Generic;
using System.Reflection;
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
    /// ЖИВЫЕ СТРОКИ ТАБЛИЦЫ НЕ ПОДМЕНЯЮТСЯ ВЫДУМАННЫМИ.
    ///
    /// <para>Экран заводит демо-строки в конструкторе — вымышленные имена,
    /// чтобы в редакторе была видна вёрстка. Переключатель периода
    /// безусловно пересоздавал их: хост подставил настоящую доску, игрок
    /// нажал «за неделю» — и увидел выдуманных людей, ничем от настоящих не
    /// отличимых. Отличить их не может и он, и мы: это просто имена.</para>
    ///
    /// <para>Отдельно про сам переключатель: сервер знает ИМЕНОВАННЫЕ доски и
    /// не знает периодов. «Неделя» и «всё время» — две разные доски, назвать
    /// их может только хост. Пока он не сказал как, живой доске переключать
    /// нечего, и кнопки нет: кнопка, которая ничего не делает, читается как
    /// поломка.</para>
    /// </summary>
    public sealed class LeaderboardLiveTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private static Button Вкладка(LeaderboardScreen s, string имя)
        {
            var f = typeof(LeaderboardScreen).GetField(имя, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(f, "поле " + имя + " переименовали — тест не о том");
            return (Button)f.GetValue(s);
        }

        private static List<LeaderboardScreen.Entry> Живые() => new List<LeaderboardScreen.Entry>
        {
            new LeaderboardScreen.Entry { Rank = 1, Name = "Настоящий", Score = 100 },
        };

        [Test]
        public void ДемоЖивётДоПриходаХоста()
        {
            var s = new LeaderboardScreen(new NoAssets());
            Assert.IsNotNull(s.Entries);
            Assert.Greater(s.Entries.Count, 1, "без хоста экран показывает демо-набор");
            Assert.AreEqual(DisplayStyle.Flex, Вкладка(s, "_tabWeek").style.display.value,
                "у демо переключатель есть — он показывает, что вёрстка живая");
        }

        [Test]
        public void ЖивуюДоскуПереключательНеПодменяет()
        {
            var s = new LeaderboardScreen(new NoAssets());
            s.Entries = Живые();

            Нажать(Вкладка(s, "_tabAll"));

            Assert.AreEqual(1, s.Entries.Count, "живые строки заменили демо-набором");
            Assert.AreEqual("Настоящий", s.Entries[0].Name,
                "игрок увидел бы выдуманные имена, неотличимые от настоящих");
        }

        [Test]
        public void БезСпособаДостатьПериодПереключателяНет()
        {
            var s = new LeaderboardScreen(new NoAssets());
            s.Entries = Живые();
            s.Rebuild();

            Assert.AreEqual(DisplayStyle.None, Вкладка(s, "_tabWeek").style.display.value,
                "кнопка, которой нечего переключать, читается как поломка");
            Assert.AreEqual(DisplayStyle.None, Вкладка(s, "_tabAll").style.display.value);
        }

        [Test]
        public void ХостСказалКакДостать_ПереключательВернулся()
        {
            var s = new LeaderboardScreen(new NoAssets());
            bool спросили = false;
            s.PeriodChanged = _ => спросили = true;
            s.Entries = Живые();
            s.Rebuild();

            Assert.AreEqual(DisplayStyle.Flex, Вкладка(s, "_tabWeek").style.display.value);
            Нажать(Вкладка(s, "_tabAll"));
            Assert.IsTrue(спросили, "экран не попросил хоста достать доску за другой период");
            Assert.AreEqual(1, s.Entries.Count, "и всё равно не выдумал строки сам");
        }

        // Нажатие без панели. Приём тот же, что у Нажатия в тестах движка
        // (сборки разные, тащить помощник через границу дороже десяти строк):
        // сперва подписка полем, потом приватный Invoke — он зовёт ВСЕХ
        // подписчиков, а поле отдаёт одного.
        private static void Нажать(Button b)
        {
            Assert.NotNull(b, "кнопки нет");
            const BindingFlags любое = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            foreach (var f in typeof(Clickable).GetFields(любое))
                if (f.GetValue(b.clickable) is System.Action a) { a(); return; }

            var invoke = typeof(Clickable).GetMethod("Invoke",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(EventBase) }, null);
            Assert.NotNull(invoke, "до обработчика кнопки не дотянуться — нажатие проверять нечем");
            invoke.Invoke(b.clickable, new object[] { null });
        }
    }
}
