using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// СКАЧАННОЕ НЕ КАЧАЕТСЯ СНОВА.
    ///
    /// <para>Обещание центра загрузок — «скачай один раз и играй». У игрока
    /// оно про деньги: мобильный трафик считают, а игра весит сотни мегабайт.
    /// Проверять его чтением кода бесполезно: кэш есть у всех, вопрос в том,
    /// СПРОСИТ ЛИ клиент сервер во второй раз.</para>
    ///
    /// <para>Поэтому здесь настоящий сервер, который ЗАПОМИНАЕТ каждый
    /// спрошенный путь, и два «запуска игры» подряд: второй обязан не
    /// притронуться к сети за уже скачанным. Обратная сторона того же
    /// обещания — «качается только изменившееся»: подменяем один файл и его
    /// запись в индексе версий, и требуем ровно один новый запрос.</para>
    /// </summary>
    public class CacheReuseTests
    {
        private const string A = "content/bg/a.jpg";
        private const string B = "content/bg/b.jpg";
        private string _cache;
        private bool _wasOffline;

        [SetUp]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "lvn-reuse-" + Guid.NewGuid().ToString("N"));
            // Флаг «связи нет» — общий на прогон, и его выставляет проверка
            // офлайна по соседству. Без явного объявления исход теста решал бы
            // порядок запуска, а не поведение загрузчика.
            _wasOffline = Lvn.LvnNetworkStatus.ForceOffline;
            Lvn.LvnNetworkStatus.ForceOffline = false;
            Lvn.LvnNetworkStatus.MarkOnline("проверка повторного использования кэша");
        }

        [TearDown]
        public void TearDown()
        {
            Lvn.LvnNetworkStatus.ForceOffline = _wasOffline;
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        private static string Sha(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Индекс версий — тот же, что отдаёт настоящий сервер:
        /// путь без «content/» → sha256 содержимого.</summary>
        private static byte[] Index(Dictionary<string, byte[]> files)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in files)
            {
                if (!kv.Key.StartsWith("content/") || kv.Key.EndsWith("asset-versions.json")) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key.Substring("content/".Length)).Append("\":\"").Append(Sha(kv.Value)).Append('"');
            }
            return Encoding.UTF8.GetBytes(sb.Append('}').ToString());
        }

        private static int AskedFor(TestHttpServer srv, string path)
        {
            int n = 0;
            lock (srv.Asked) foreach (var p in srv.Asked) if (p == path) n++;
            return n;
        }

        [UnityTest]
        public IEnumerator ВторойЗапускНеТратитСетьНаСкачанное()
        {
            var files = new Dictionary<string, byte[]>
            {
                [A] = new byte[] { 1, 2, 3, 4 },
                [B] = new byte[] { 5, 6, 7, 8, 9 },
            };
            files["content/asset-versions.json"] = Index(files);

            using var srv = new TestHttpServer(files);

            using (var first = new ContentLoader(srv.Root, _cache))
            {
                yield return Await(first.LoadAssetVersionsAsync());
                yield return Await(first.DownloadAssetBytes("/" + A));
                yield return Await(first.DownloadAssetBytes("/" + B));
            }
            Assert.AreEqual(1, AskedFor(srv, A), "первый запуск должен спросить файл ровно раз");
            Assert.AreEqual(1, AskedFor(srv, B), "первый запуск должен спросить файл ровно раз");

            // ВТОРОЙ ЗАПУСК ИГРЫ: новый загрузчик, тот же кэш на диске.
            using (var second = new ContentLoader(srv.Root, _cache))
            {
                yield return Await(second.LoadAssetVersionsAsync());
                var a = second.DownloadAssetBytes("/" + A);
                yield return Await(a);
                var b = second.DownloadAssetBytes("/" + B);
                yield return Await(b);
                Assert.AreEqual(4, a.Result.Length, "второй запуск отдал не те байты");
                Assert.AreEqual(5, b.Result.Length, "второй запуск отдал не те байты");
            }

            Assert.AreEqual(1, AskedFor(srv, A),
                "скачанное спрошено во второй раз — обещание «скачай один раз» стоит игроку трафика");
            Assert.AreEqual(1, AskedFor(srv, B),
                "скачанное спрошено во второй раз — обещание «скачай один раз» стоит игроку трафика");
        }

        /// <summary>УКУС ДЛЯ САМОЙ МЕРКИ. Оба теста выше зелёные с первого
        /// раза, а зелёный, который не умеет краснеть, ничего не стоит: если
        /// счётчик запросов молчит всегда, «сеть не тронута» доказывает не
        /// поведение загрузчика, а сломанный стенд. Здесь второй запуск идёт с
        /// ПУСТЫМ кэшем — и обязан спросить сервер заново.</summary>
        [UnityTest]
        public IEnumerator СтендЗамечаетПерекачку()
        {
            var files = new Dictionary<string, byte[]> { [A] = new byte[] { 1, 2, 3, 4 } };
            files["content/asset-versions.json"] = Index(files);
            using var srv = new TestHttpServer(files);

            using (var first = new ContentLoader(srv.Root, _cache))
            {
                yield return Await(first.LoadAssetVersionsAsync());
                yield return Await(first.DownloadAssetBytes("/" + A));
            }
            var другойКэш = Path.Combine(Path.GetTempPath(), "lvn-reuse-bite-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var second = new ContentLoader(srv.Root, другойКэш);
                yield return Await(second.LoadAssetVersionsAsync());
                yield return Await(second.DownloadAssetBytes("/" + A));
            }
            finally
            {
                if (Directory.Exists(другойКэш)) Directory.Delete(другойКэш, true);
            }

            Assert.AreEqual(2, AskedFor(srv, A),
                "стенд не заметил перекачки с чистого кэша — значит и «сеть не тронута» он бы не заметил");
        }

        [UnityTest]
        public IEnumerator КачаетсяТолькоИзменившийсяФайл()
        {
            var files = new Dictionary<string, byte[]>
            {
                [A] = new byte[] { 1, 2, 3, 4 },
                [B] = new byte[] { 5, 6, 7, 8, 9 },
            };
            files["content/asset-versions.json"] = Index(files);

            using var srv = new TestHttpServer(files);

            using (var first = new ContentLoader(srv.Root, _cache))
            {
                yield return Await(first.LoadAssetVersionsAsync());
                yield return Await(first.DownloadAssetBytes("/" + A));
                yield return Await(first.DownloadAssetBytes("/" + B));
            }

            // Автор перезалил ОДИН файл: содержимое другое, индекс это назвал.
            files[A] = new byte[] { 9, 9, 9, 9, 9, 9 };
            files["content/asset-versions.json"] = Index(files);

            using (var third = new ContentLoader(srv.Root, _cache))
            {
                yield return Await(third.LoadAssetVersionsAsync());
                var a = third.DownloadAssetBytes("/" + A);
                yield return Await(a);
                var b = third.DownloadAssetBytes("/" + B);
                yield return Await(b);
                Assert.AreEqual(6, a.Result.Length, "изменившийся файл приехал старым — правка не доедет до игрока");
                Assert.AreEqual(5, b.Result.Length, "нетронутый файл приехал не тем");
            }

            Assert.AreEqual(2, AskedFor(srv, A), "изменившийся файл обязан быть перекачан ровно раз");
            Assert.AreEqual(1, AskedFor(srv, B),
                "нетронутый файл перекачан заново — значит правка одного файла стоит игроку всей игры");
        }
    }
}
