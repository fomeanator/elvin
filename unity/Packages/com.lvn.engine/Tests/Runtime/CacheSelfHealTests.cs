using System;
using System.Collections;
using System.IO;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// БИТЫЙ ФАЙЛ В КЭШЕ ЛЕЧИТСЯ САМ.
    ///
    /// <para>Кэш ассетов — это файл на диске игрока, и он бывает битым: обрыв
    /// на записи, склейка двух редакций у докачки (жила до 05.09), порча
    /// файловой системы. Ключ кэша у производного варианта постоянный, версии
    /// у него нет, — значит однажды испорченный файл будет читаться вечно, и
    /// у игрока на этом месте навсегда останется пустота. Единственным
    /// лечением была переустановка приложения.</para>
    ///
    /// <para>Условие проверки поэтому не «декодер умеет вернуть null», а
    /// «положили в кэш мусор — картинка ВСЁ РАВНО показывается»: загрузчик
    /// обязан заметить, что байты не читаются, выбросить их и сходить за
    /// целыми. У кодированного пути (ktx2) это правило было с 27.08; у
    /// растрового — нет, и проверка ниже его и ловит.</para>
    ///
    /// <para>Сервер — НАСТОЯЩИЙ (тот же бинарь, что и у смоука): подделка
    /// проверяла бы подделку, а вопрос ровно в том, сходит ли клиент по
    /// проводу заново.</para>
    /// </summary>
    public class CacheSelfHealTests
    {
        private static string RepoRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", ".."));

        private static string FindServerBin()
        {
            var env = Environment.GetEnvironmentVariable("LVN_SERVER_BIN");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var built = Path.Combine(RepoRoot, "qa", "bin", "lvnserver-test");
            return File.Exists(built) ? built : null;
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static byte[] MakePng(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(40, 90, 140, 255);
            tex.SetPixels32(px);
            tex.Apply();
            var bytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return bytes;
        }

        private bool _wasOffline;

        /// <summary>СЕТЬ — ГЛОБАЛЬНОЕ СОСТОЯНИЕ, И ЕГО ОСТАВЛЯЮТ СОСЕДИ.
        ///
        /// <para>Проверка офлайна выставляет общий флаг «связи нет», и тест,
        /// идущий следом, видит его же. В одиночку этот тест был зелёным, а в
        /// общем прогоне падал с «offline (global status)» — порядок тестов
        /// решал исход. Объявляем условие явно и возвращаем как было.</para></summary>
        [SetUp]
        public void SetUp()
        {
            _wasOffline = Lvn.LvnNetworkStatus.ForceOffline;
            Lvn.LvnNetworkStatus.ForceOffline = false;
            Lvn.LvnNetworkStatus.MarkOnline("проверка самолечения кэша");
        }

        [TearDown]
        public void TearDown() => Lvn.LvnNetworkStatus.ForceOffline = _wasOffline;

        [UnityTest]
        public IEnumerator БитыйФайлВКэшеНеОстаётсяНавсегда()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-heal-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            var cache = Path.Combine(stand, "cache");
            Directory.CreateDirectory(Path.Combine(content, "art"));
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(content, "manifest.json"), "{\"titles\":[]}");
            File.WriteAllBytes(Path.Combine(content, "art", "pic.png"), MakePng(64, 64));

            var port = FreePort();
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            ContentLoader loader = null;
            try
            {
                var healthDeadline = Time.realtimeSinceStartup + 10f;
                var healthy = false;
                while (!healthy && Time.realtimeSinceStartup < healthDeadline)
                {
                    using (var probe = UnityEngine.Networking.UnityWebRequest.Get($"http://127.0.0.1:{port}/healthz"))
                    {
                        probe.timeout = 2;
                        yield return probe.SendWebRequest();
                        healthy = probe.result == UnityEngine.Networking.UnityWebRequest.Result.Success;
                    }
                }
                Assert.IsTrue(healthy, "локальный сервер не ответил на /healthz за 10с");
                // Ещё раз, уже после проверки связи: между SetUp и этой строкой
                // сеть мог пометить офлайном чужой доживающий тест.
                LvnNetworkStatus.MarkOnline("сервер стенда отвечает");

                loader = new ContentLoader($"http://127.0.0.1:{port}", cache);
                const string url = "/content/art/pic.png";

                var first = loader.DownloadSpriteAsync(url);
                var deadline = Time.realtimeSinceStartup + 20f;
                while (!first.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(first.IsCompleted, "первая загрузка не завершилась за 20с");
                Assert.IsNotNull(first.Result, "здоровая картинка не загрузилась — стенду не на чем стоять");

                // ПОРЧА. Ровно то, что оставляла склейка двух редакций: файл на
                // месте, размер похож, байты не читаются.
                var assets = Path.Combine(cache, "assets");
                var files = Directory.GetFiles(assets, "*.bin");
                Assert.IsNotEmpty(files, "после загрузки в кэше нет файла — проверять нечего");
                foreach (var f in files) File.WriteAllBytes(f, new byte[] { 0x4D, 0x55, 0x53, 0x4F, 0x52 });

                loader.UnloadAll();   // из памяти ушло, на диске — мусор

                var second = loader.DownloadSpriteAsync(url);
                deadline = Time.realtimeSinceStartup + 25f;
                while (!second.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(second.IsCompleted, "вторая загрузка не завершилась за 25с");
                Assert.IsNotNull(second.Result,
                    "битые байты в кэше не выброшены: картинки на этом месте не будет НИКОГДА — "
                    + "у варианта нет версии, значит ключ кэша постоянный, и лечит только переустановка");
            }
            finally
            {
                loader?.Dispose();
                try { if (!proc.HasExited) proc.Kill(); } catch { /* уже умер */ }
                try { Directory.Delete(stand, true); } catch { /* временный каталог */ }
            }
        }
    }
}
