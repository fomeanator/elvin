using System;
using System.Collections.Generic;
using System.Net;

namespace Lvn.Tests
{
    /// <summary>
    /// КРОШЕЧНЫЙ НАСТОЯЩИЙ СЕРВЕР ДЛЯ ПРОВЕРОК.
    ///
    /// <para>Заглушка сети проверяла бы заглушку. Здесь поднимается настоящий
    /// <see cref="HttpListener"/> на свободном порту: загрузчик ходит по
    /// проводу, а тест видит, ЧТО ИМЕННО у него спросили — иначе «клиент
    /// попросил ту ступень качества, которую выбрал игрок» проверить нечем.</para>
    ///
    /// <para>Гасится совсем, а не «отвечает ошибкой»: «сервер сказал нет» и
    /// «сервера нет» — разные ветки в загрузчике, и офлайн живёт во второй.</para>
    /// </summary>
    internal sealed class TestHttpServer : IDisposable
    {
        private readonly HttpListener _l = new HttpListener();
        private readonly Dictionary<string, byte[]> _files;

        /// <summary>Пути, которые у сервера спросили, по порядку.</summary>
        public readonly List<string> Asked = new List<string>();
        public readonly string Root;

        public TestHttpServer(Dictionary<string, byte[]> files)
        {
            _files = files;
            Root = $"http://127.0.0.1:{FreePort()}/";
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
                catch { return; }   // погашен — это конец работы, а не ошибка
                var path = ctx.Request.Url.AbsolutePath.TrimStart('/');
                lock (Asked) Asked.Add(path);
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

        /// <summary>Спрашивали ли этот путь (без учёта строки запроса).</summary>
        public bool WasAsked(string path)
        {
            lock (Asked) return Asked.Contains(path.TrimStart('/'));
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }
}
