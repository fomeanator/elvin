using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests.Runtime
{
    public class OfflineReconnectTests
    {
        private HttpListener _server;
        private Task _serve;
        private string _url, _title, _user, _backend, _token, _uid, _owner, _walletOwner;
        private bool _forcedOffline, _wasOffline;
        private volatile bool _available;
        private int _gets, _statePuts, _walletPuts;
        private JObject _state;
        private readonly object _gate = new object();
        private HttpStateStore _store;

        [SetUp] public void SetUp()
        {
            _forcedOffline = LvnNetworkStatus.ForceOffline;
            _wasOffline = LvnNetworkStatus.IsOffline;
            LvnNetworkStatus.ForceOffline = false;
            _backend = LvnBackend.BaseUrl;
            _token = LvnKeep.Get("lvn.svc.token", "");
            _uid = LvnKeep.Get("lvn.svc.user", "");
            _owner = LvnKeep.Owner;
            _walletOwner = LvnKeep.Get("lvn.wallet.owner", "");
            _title = "reconnect-" + Guid.NewGuid().ToString("N");
            _user = Guid.NewGuid().ToString("N");
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            _url = "http://127.0.0.1:" + port;
            _server = new HttpListener(); _server.Prefixes.Add(_url + "/"); _server.Start();
            _serve = Task.Run(ServeAsync);
            LvnBackend.BaseUrl = _url;
            LvnKeep.Put("lvn.svc.token", "reconnect-test");
            LvnKeep.Put("lvn.svc.user", _user);
            LvnWallet.NoteUser(_user);
            LvnWallet.ResetLocal();
            _store = new HttpStateStore(_url, _user, "test-key");
        }

        private async Task ServeAsync()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await _server.GetContextAsync(); } catch { break; }
                try
                {
                    string body = "{}";
                    int code = 503;
                    if (ctx.Request.HttpMethod == "GET") Interlocked.Increment(ref _gets);
                    if (_available)
                    {
                        if (ctx.Request.Url.AbsolutePath == "/v1/state")
                        {
                            if (ctx.Request.HttpMethod == "PUT")
                            {
                                using var reader = new StreamReader(ctx.Request.InputStream);
                                var doc = JObject.Parse(await reader.ReadToEndAsync());
                                doc["_version"] = 1;
                                lock (_gate) _state = doc;
                                Interlocked.Increment(ref _statePuts);
                                // The commit lands but its acknowledgement is
                                // lost behind a failing gateway. Retry must GET
                                // and recognise it, not apply another write.
                                code = 503;
                            }
                            else lock (_gate)
                            {
                                code = _state == null ? 404 : 200;
                                body = _state?.ToString() ?? "{}";
                            }
                        }
                        else if (ctx.Request.Url.AbsolutePath == "/v1/wallet/earn")
                        {
                            Interlocked.Increment(ref _walletPuts);
                            code = 200; body = "{\"balances\":{\"gold\":12},\"inventory\":{}}";
                        }
                    }
                    ctx.Response.StatusCode = code;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        [UnityTearDown] public IEnumerator TearDown()
        {
            _store?.Dispose();
            LvnWallet.ResetLocal();
            _server.Stop(); _server.Close();
            float until = Time.realtimeSinceStartup + 2;
            while (!_serve.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            LvnBackend.BaseUrl = _backend;
            LvnKeep.Put("lvn.svc.token", _token);
            LvnKeep.Put("lvn.svc.user", _uid);
            LvnKeep.Put("lvn.wallet.owner", _walletOwner);
            LvnKeep.NoteOwner(_owner);
            LocalStateStore.Forget(_title);
            LvnKeep.Drop(_store.IndexKey); LvnKeep.Drop(_store.IndexKey + ".bak");
            LvnNetworkStatus.ForceOffline = _forcedOffline;
            if (_wasOffline) LvnNetworkStatus.MarkOffline("restore test state");
            else LvnNetworkStatus.MarkOnline("restore test state");
        }

        [UnityTest] public IEnumerator OutboxesRecoverAfterRestartWithoutAnyUiOrManualFlush()
        {
            var save = _store.SaveVarsAsync(_title, new JObject { ["chapter"] = 6 }, default);
            var earn = LvnWallet.EarnAsync("gold", 12, "chapter");
            Assert.IsTrue(save.IsCompleted);
            Assert.IsTrue(earn.IsCompleted);
            float until = Time.realtimeSinceStartup + 8;
            while (Volatile.Read(ref _gets) == 0 && Time.realtimeSinceStartup < until) yield return null;
            Assert.Greater(Volatile.Read(ref _gets), 0, "actual HTTP request must reach the failing server");
            Assert.AreEqual(1, _store.PendingCount);
            Assert.AreEqual(1, LvnWallet.PendingCount);

            // Simulate a cold client: discard memory, retain only persisted data.
            _store.Dispose();
            _store = new HttpStateStore(_url, _user, "test-key");
            LvnWallet.ReloadLocal();
            LvnNetworkStatus.MarkOffline("train entered the forest");
            _available = true; // no MarkOnline, Load, Save, Refresh or Flush call
            until = Time.realtimeSinceStartup + 15;
            while ((_store.PendingCount != 0 || LvnWallet.PendingCount != 0)
                && Time.realtimeSinceStartup < until) yield return null;
            Assert.AreEqual(0, _store.PendingCount, "background probing must recover without player action");
            Assert.AreEqual(0, LvnWallet.PendingCount);
            Assert.AreEqual(1, Volatile.Read(ref _statePuts), "lost acknowledgement must not duplicate a commit");
            Assert.AreEqual(1, Volatile.Read(ref _walletPuts));
            Assert.AreEqual(12, LvnWallet.Balance("gold"));
            var local = _store.LoadVarsAsync(_title, default);
            Assert.IsTrue(local.IsCompleted);
            Assert.AreEqual(6, (int)local.Result["chapter"]);
        }
    }
}
