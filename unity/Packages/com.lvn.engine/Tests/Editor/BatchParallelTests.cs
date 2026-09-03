using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ОБОЗ ИДЁТ В НЕСКОЛЬКО ПОЛОС — правила ширины и счёта.
    ///
    /// <para>Пакет качался строго по одному файлу, а полоса сети шириной
    /// двенадцать держала одиннадцать мест пустыми. Набор первого кадра — это
    /// десятки МЕЛКИХ файлов, и их цена не в байтах, а в круговом рейсе на
    /// каждый: тридцать файлов по одному — тридцать задержек подряд, то есть
    /// весь видимый вход в приложение.</para>
    ///
    /// <para>ЧЕГО ЗДЕСЬ НЕТ И ПОЧЕМУ. Саму одновременность проверить в EditMode
    /// нечем: она случается на проводе, а <c>UnityWebRequest</c> без кадров не
    /// едет (см. <see cref="ContentFetchTests"/>). Проверяется то, что от
    /// провода не зависит и ломается тише всего: ПРАВИЛО ШИРИНЫ (бронь живому
    /// не отдаётся никогда) и СЧЁТ (полоса загрузки и центр загрузок читают
    /// ровно эти два числа).</para>
    /// </summary>
    public class BatchParallelTests
    {
        private string _root;
        private ContentLoader _loader;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "lvn-batch-" + Guid.NewGuid().ToString("N"));
            // Порт 9 (discard): если пакет всё-таки уйдёт на провод, это будет
            // видно зависанием, а не случайно удачным ответом.
            _loader = new ContentLoader("http://127.0.0.1:9/", _root);
        }

        [TearDown]
        public void TearDown()
        {
            _loader?.Dispose();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        /// <summary>Бронь для живого — не резерв «на всякий случай», а место,
        /// куда встаёт картинка, которую игрок УЖЕ ВИДИТ пустой. Пакет обязан
        /// оставить её свободной, даже когда файлов у него сотня.</summary>
        [Test]
        public void ПакетНеЗанимаетБроньЖивого()
        {
            int запас = LvnLanes.Wire.Width - LvnLanes.Wire.KeptForLive;
            Assert.That(ContentLoader.BatchWorkerCount(1000), Is.EqualTo(запас),
                "пакет забрал места, оставленные живому запросу: открытая глава будет ждать обоз");
            Assert.That(запас, Is.LessThan(LvnLanes.Wire.Width),
                "в полосе не осталось брони — живой запрос встанет в общую очередь");
        }

        /// <summary>Рабочих не бывает больше, чем работы: на трёх файлах десять
        /// рабочих — это девять пустых проходов по курсору.</summary>
        [Test]
        public void РабочихНеБольшеЧемФайлов()
        {
            Assert.That(ContentLoader.BatchWorkerCount(3), Is.EqualTo(3));
            Assert.That(ContentLoader.BatchWorkerCount(1), Is.EqualTo(1));
            // Пустой пакет сюда не доходит (StartPreloadBatch отсекает раньше),
            // но ноль рабочих означал бы задачу, которая никогда не завершится.
            Assert.That(ContentLoader.BatchWorkerCount(0), Is.EqualTo(1));
        }

        /// <summary>ВСЁ УЖЕ НА ДИСКЕ — СЕТИ НЕ БЫВАЕТ. Второй запуск приложения
        /// проходит этим путём целиком, и уход в сеть здесь стоил бы игроку
        /// полосы загрузки на пустом месте.</summary>
        [Test]
        public async Task КэшированныйПакетНеИдётВСеть()
        {
            var urls = new List<string>();
            for (int i = 0; i < 12; i++) urls.Add($"ui/icon-{i}.png");
            foreach (var u in urls) ПоложитьВКэш(u);

            var items = new List<PreloadItem>();
            foreach (var u in urls) items.Add(new PreloadItem { Url = u, Kind = "asset" });

            var работа = _loader.StartPreloadBatch(items, CancellationToken.None);
            // Порт 9 не отвечает: не уложились в пять секунд — значит пакет
            // всё-таки полез на провод за тем, что лежит на диске.
            var первым = await Task.WhenAny(работа, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.That(первым, Is.SameAs(работа),
                "пакет из полностью закэшированных файлов ушёл в сеть");
        }

        private void ПоложитьВКэш(string url)
        {
            var метод = typeof(ContentLoader).GetMethod("CachePath",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(метод, "CachePath переименован — тест кладёт файлы не туда, куда смотрит пакет");
            var путь = (string)метод.Invoke(_loader, new object[] { Path.Combine(_root, "assets"), url, ".bin" });
            Directory.CreateDirectory(Path.GetDirectoryName(путь));
            File.WriteAllBytes(путь, new byte[] { 1, 2, 3 });
        }
    }
}
