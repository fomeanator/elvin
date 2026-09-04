using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Lvn;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// СЕРВЕР ПОГАШЕН — ИГРА ИДЁТ.
    ///
    /// <para>Соседние проверки (`OfflinePolicyTests`, `ContentFetchTests`)
    /// разбирают РЕШЕНИЕ: что делать, зная, что связи нет, и как не пойти на
    /// провод впустую. Обе честны и обе не касаются главного вопроса — а
    /// доиграет ли игрок главу, которая целиком лежит у него на диске.</para>
    ///
    /// <para>Здесь ставится само условие: поднимается НАСТОЯЩИЙ сервер, глава и
    /// её арт скачиваются с него, сервер ГАСИТСЯ — и глава играется до конца.
    /// Ни один шаг не подменён заглушкой: подмена сети заглушкой проверяла бы
    /// заглушку.</para>
    /// </summary>
    public class OfflinePlaythroughTests
    {
        // Крошечный сервер на свободном порту. Гасится совсем, а не «отвечает
        // ошибкой»: разница между «сервер сказал нет» и «сервера нет» — это
        // разные ветки в загрузчике, и офлайн живёт во второй.
        private sealed class MiniServer : IDisposable
        {
            private readonly HttpListener _l = new HttpListener();
            private readonly Dictionary<string, byte[]> _files;
            public readonly string Root;

            public MiniServer(Dictionary<string, byte[]> files)
            {
                _files = files;
                int port = FreePort();
                Root = $"http://127.0.0.1:{port}/";
                _l.Prefixes.Add(Root);
                _l.Start();
                Serve();
            }

            private static int FreePort()
            {
                var s = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                s.Start();
                int p = ((IPEndPoint)s.LocalEndpoint).Port;
                s.Stop();
                return p;
            }

            private async void Serve()
            {
                while (_l.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _l.GetContextAsync(); }
                    catch { return; }   // погашен — это конец работы, не ошибка
                    var path = ctx.Request.Url.AbsolutePath.TrimStart('/');
                    if (_files.TryGetValue(path, out var body))
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body, 0, body.Length);
                    }
                    else ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }

            public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
        }

        private const string ScriptPath = "content/ch.lvn";
        private const string AssetPath = "content/bg/room.jpg";
        private const string Chapter = @"{""scene"":""offline"",""script"":[
            {""op"":""bg"",""id"":""room"",""sprite_url"":""/content/bg/room.jpg""},
            {""op"":""say"",""text"":""первая""},
            {""op"":""say"",""text"":""вторая""}
        ]}";

        private string _cache;
        private bool _wasOffline;

        [SetUp]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "lvn-offline-" + Guid.NewGuid().ToString("N"));
            _wasOffline = LvnNetworkStatus.ForceOffline;
        }

        [TearDown]
        public void TearDown()
        {
            LvnNetworkStatus.ForceOffline = _wasOffline;
            if (!_wasOffline) LvnNetworkStatus.MarkOnline("конец теста");
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        private sealed class Screen : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public string Bg;
            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> o) { }
            public void ApplyStage(JObject c, LvnSender s) => ApplyStage(c);
            public void ApplyStage(JObject c)
            {
                if ((string)c["op"] == "bg") Bg = (string)c["sprite_url"];
            }
            public void OnEnd() { }
        }

        [UnityTest]
        public IEnumerator ГлаваИграетсяПослеТогоКакСерверПогас()
        {
            var files = new Dictionary<string, byte[]>
            {
                [ScriptPath] = Encoding.UTF8.GetBytes(Chapter),
                [AssetPath] = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            };

            string root;
            using (var srv = new MiniServer(files))
            {
                root = srv.Root;
                using var online = new ContentLoader(root, _cache);

                // ИМЕННО DownloadScriptCached, а не DownloadScriptText: второй
                // объявлен «always-fresh, no disk cache» и офлайну не помогает.
                // Глава при игре ходит через кэширующий — им и проверяем.
                var script = online.DownloadScriptCached("/" + ScriptPath);
                yield return Await(script);
                Assert.AreEqual(Chapter, script.Result, "живой сервер отдал не то, что положили");
                Assert.IsTrue(online.IsScriptCached("/" + ScriptPath), "глава не легла в кэш");

                var bytes = online.DownloadAssetBytes("/" + AssetPath);
                yield return Await(bytes);
                Assert.AreEqual(8, bytes.Result.Length, "арт не доехал с живого сервера");

                Assert.IsTrue(online.IsAssetCached("/" + AssetPath), "скачанное не легло в кэш");
            }
            // Сервер погашен. Дальше — только диск.

            LvnNetworkStatus.ForceOffline = true;
            using var offline = new ContentLoader(root, _cache);

            var cachedScript = offline.DownloadScriptCached("/" + ScriptPath);
            yield return Await(cachedScript);
            Assert.AreEqual(Chapter, cachedScript.Result,
                "глава не поднялась с диска при погашенном сервере — офлайна нет");

            var cachedBytes = offline.DownloadAssetBytes("/" + AssetPath);
            yield return Await(cachedBytes);
            Assert.AreEqual(8, cachedBytes.Result.Length,
                "арт не поднялся с диска при погашенном сервере");

            // И главное: глава ИГРАЕТСЯ, а не просто «файлы читаются».
            var screen = new Screen();
            var player = new LvnPlayer(LvnDocument.Parse(cachedScript.Result), screen);
            for (int i = 0; i < 8 && !player.Finished; i++) player.Advance();

            CollectionAssert.AreEqual(new[] { "первая", "вторая" }, screen.Lines,
                "глава не доиграла до конца без сервера");
            Assert.AreEqual("/content/bg/room.jpg", screen.Bg, "фон не поставился");
        }

        // Стенд обязан быть настоящим: если бы сервер не гас, проверка ничего
        // не значила бы — она бы просто скачивала заново.
        [UnityTest]
        public IEnumerator ПогашенныйСерверДействительноНедоступен()
        {
            var files = new Dictionary<string, byte[]> { [ScriptPath] = Encoding.UTF8.GetBytes(Chapter) };
            string root;
            using (var srv = new MiniServer(files)) root = srv.Root;

            using var loader = new ContentLoader(root, _cache);
            var t = loader.DownloadScriptText("/" + ScriptPath);   // без кэша: чистая проверка провода
            bool failed = false;
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted || t.Result == null) failed = true;

            Assert.IsTrue(failed,
                "погашенный сервер всё ещё отвечает — стенд не гасит его, и офлайн не проверяется");
        }
    }
}
