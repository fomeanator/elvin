using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// CS-1.6-style server pick, drawn over the boot veil before any content
    /// loads. Unchecked (default): the known servers race a <c>/healthz</c>
    /// ping and the first live one wins — invisible to the player unless
    /// nothing answers in time. Checked ("Выбрать сервер вручную", persisted
    /// via <see cref="LvnPrefs.ManualServerSelect"/>): a small browser lists
    /// the baked-in servers plus a free-text field for the player's own host
    /// (any URL up to its <c>/api</c> root) and waits for an explicit Connect.
    /// </summary>
    internal static class ServerSelectScreen
    {
        private const float ProbeTimeoutSeconds = 2.5f;

        public static async Task<string> ResolveAsync(string defaultUrl, (string Name, string Url)[] knownServers, CancellationToken ct)
        {
            var candidates = BuildCandidates(defaultUrl, knownServers);
            var savedCustom = LvnPrefs.ServerUrlOverride;
            bool manual = LvnPrefs.ManualServerSelect;

            GameObject go = null;
            try
            {
                VisualElement root;
                (go, root) = LvnFloor.Open("LvnServerSelect", LvnFloor.ServerSelect);
                root.pickingMode = PickingMode.Position;

                var checkRow = new VisualElement();
                checkRow.style.position = Position.Absolute;
                checkRow.style.left = 16;
                checkRow.style.bottom = 16;
                ScreenUi.Row(checkRow);
                var check = new Toggle { value = manual };
                var checkLabel = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("server.manual", "Choose the server manually"));
                checkLabel.style.color = new Color(0.65f, 0.65f, 0.65f);
                checkLabel.style.fontSize = LvnTokens.TextMicro;
                checkLabel.style.marginLeft = LvnTokens.Space1;
                checkRow.Add(check);
                checkRow.Add(checkLabel);
                root.Add(checkRow);
                check.RegisterValueChangedCallback(e => LvnPrefs.ManualServerSelect = e.newValue);

                if (!manual)
                {
                    // Auto lane: race the probes, but let the player interrupt
                    // into the picker at any moment by ticking the box.
                    var switched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnToggle(ChangeEvent<bool> e) { if (e.newValue) switched.TrySetResult(true); }
                    check.RegisterValueChangedCallback(OnToggle);

                    var raceTask = RaceAsync(candidates, ct);
                    var first = await Task.WhenAny(raceTask, switched.Task);
                    if (first != switched.Task)
                        return await raceTask; // auto-pick won — no picker needed
                    check.UnregisterValueChangedCallback(OnToggle);
                }

                return await ShowPickerAsync(root, candidates, savedCustom, defaultUrl, ct);
            }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }
        }

        private static List<(string Name, string Url)> BuildCandidates(string defaultUrl, (string Name, string Url)[] knownServers)
        {
            var list = new List<(string Name, string Url)>();
            void AddUnique(string name, string url)
            {
                url = Lvn.LvnUrl.Base(url);
                if (string.IsNullOrEmpty(url)) return;
                foreach (var c in list) if (c.Url == url) return;
                list.Add((name, url));
            }
            AddUnique(LvnWords.Of("server.default", "Default"), defaultUrl);
            if (knownServers != null)
                foreach (var s in knownServers) AddUnique(s.Name, s.Url);
            return list;
        }

        private static async Task<string> RaceAsync(List<(string Name, string Url)> candidates, CancellationToken ct)
        {
            if (candidates.Count == 0) return null;
            var pending = new List<Task<(bool ok, string url)>>();
            foreach (var c in candidates) pending.Add(ProbeAsync(c.Url, ct));
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var result = await done;
                if (result.ok) return result.url;
            }
            return candidates[0].Url; // nobody answered — fall through to the build default
        }

        private static async Task<(bool ok, string url)> ProbeAsync(string url, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
                // using: загрузчик здесь ОДНОРАЗОВЫЙ — живёт ради одного
                // healthz. Без отпускания каждая проба навсегда оставалась бы
                // подписчиком Application.lowMemory: адресов в списке пятеро,
                // экран открывают не раз, и на нехватку памяти отзывался бы
                // хвост мёртвых загрузчиков.
                using var loader = new ContentLoader(url);
                bool ok = await loader.HealthzAsync(ct: cts.Token);
                return (ok, url);
            }
            catch { return (false, url); }
        }

        private static async Task<string> ShowPickerAsync(VisualElement root, List<(string Name, string Url)> candidates,
            string savedCustom, string defaultUrl, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => tcs.TrySetResult(defaultUrl));

            void Confirm(string url)
            {
                url = Lvn.LvnUrl.Base(url);
                if (string.IsNullOrEmpty(url)) return;
                LvnPrefs.ServerUrlOverride = url == defaultUrl ? "" : url;
                tcs.TrySetResult(url);
            }

            var panel = Lvn.UI.LvnChrome.Sheet(new VisualElement());
            panel.style.top = Length.Percent(18f);
            LvnAir.PadX(panel, LvnTokens.Space4);
            LvnAir.PadY(panel, LvnTokens.Space3);
            panel.style.backgroundColor = LvnTokens.Scrim;
            LvnChrome.Round(panel, LvnTokens.Radius);
            root.Add(panel);

            var title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("server.title", "Server"));
            title.style.fontSize = LvnTokens.TextSm;
            title.style.color = new Color(0.96f, 0.93f, 0.85f);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.marginBottom = LvnTokens.Space2;
            panel.Add(title);

            foreach (var c in candidates)
            {
                var row = ScreenUi.Row();
                row.style.marginBottom = LvnTokens.Space1;

                var dot = new Label("•");
                dot.style.color = new Color(0.5f, 0.5f, 0.5f);
                dot.style.fontSize = LvnTokens.TextSm;
                dot.style.marginRight = LvnTokens.Space1;
                row.Add(dot);

                var btn = new Button(() => Confirm(c.Url)) { text = $"{c.Name}\n{c.Url}" };
                btn.style.flexGrow = 1;
                btn.style.unityTextAlign = TextAnchor.MiddleLeft;
                btn.style.fontSize = LvnTokens.TextMicro;
                LvnAir.PadY(btn, LvnTokens.Space1);
                btn.style.paddingLeft = LvnTokens.Space2;
                btn.style.backgroundColor = LvnTokens.Faint;
                btn.style.color = new Color(0.9f, 0.9f, 0.9f);
                row.Add(btn);
                panel.Add(row);

                Lvn.LvnAsync.Fire(ProbeAsync(c.Url, ct).ContinueWith(t =>
                {
                    if (dot.panel == null) return; // screen already gone
                    dot.style.color = t.Result.ok ? new Color(0.4f, 0.85f, 0.4f) : new Color(0.85f, 0.35f, 0.35f);
                }, TaskScheduler.FromCurrentSynchronizationContext()), "ProbeServer");
            }

            var customLabel = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("server.custom", "Your own server (URL up to /api)"));
            customLabel.style.color = new Color(0.80f, 0.72f, 0.56f);
            customLabel.style.fontSize = LvnTokens.TextMicro;
            customLabel.style.marginTop = LvnTokens.Space2;
            customLabel.style.marginBottom = LvnTokens.Space1;
            panel.Add(customLabel);

            bool customKnown = candidates.Exists(c => c.Url == savedCustom);
            var field = new TextField { value = !customKnown ? savedCustom : "" };
            field.style.fontSize = LvnTokens.TextXs;
            var input = field.Q(TextField.textInputUssName);
            if (input != null)
            {
                input.style.backgroundColor = new Color(0.11f, 0.11f, 0.13f);
                input.style.color = new Color(0.9f, 0.9f, 0.9f);
                LvnAir.Pad(input, LvnTokens.Space2);
            }
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Confirm(field.value);
            });
            panel.Add(field);

            var connect = Lvn.UI.LvnRedress.Bind(new Button(() => Confirm(field.value)), () => LvnWords.Of("server.connect", "Connect"));
            connect.style.marginTop = LvnTokens.Space2;
            connect.style.fontSize = LvnTokens.TextXs;
            LvnAir.PadY(connect, LvnTokens.Space2);
            connect.style.backgroundColor = new Color(0.78f, 0.63f, 0.31f);
            connect.style.color = new Color(0.08f, 0.08f, 0.10f);
            panel.Add(connect);

            return await tcs.Task;
        }
    }
}
