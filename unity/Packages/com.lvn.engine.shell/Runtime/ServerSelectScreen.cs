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
                go = new GameObject("LvnServerSelect");
                var doc = go.AddComponent<UIDocument>();
                doc.panelSettings = LvnPanel.Shared;
                doc.sortingOrder = 110; // above the boot veil (100)
                var root = doc.rootVisualElement;
                root.style.flexGrow = 1;
                root.pickingMode = PickingMode.Position;

                var checkRow = new VisualElement();
                checkRow.style.position = Position.Absolute;
                checkRow.style.left = 16;
                checkRow.style.bottom = 16;
                checkRow.style.flexDirection = FlexDirection.Row;
                checkRow.style.alignItems = Align.Center;
                var check = new Toggle { value = manual };
                var checkLabel = new Label("Выбрать сервер вручную");
                checkLabel.style.color = new Color(0.65f, 0.65f, 0.65f);
                checkLabel.style.fontSize = 14;
                checkLabel.style.marginLeft = 6;
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
                url = (url ?? "").TrimEnd('/');
                if (string.IsNullOrEmpty(url)) return;
                foreach (var c in list) if (c.Url == url) return;
                list.Add((name, url));
            }
            AddUnique("По умолчанию", defaultUrl);
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
                var loader = new ContentLoader(url);
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
                url = (url ?? "").TrimEnd('/');
                if (string.IsNullOrEmpty(url)) return;
                LvnPrefs.ServerUrlOverride = url == defaultUrl ? "" : url;
                tcs.TrySetResult(url);
            }

            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = Length.Percent(8f);
            panel.style.right = Length.Percent(8f);
            panel.style.top = Length.Percent(18f);
            panel.style.paddingTop = 20;
            panel.style.paddingBottom = 20;
            panel.style.paddingLeft = 22;
            panel.style.paddingRight = 22;
            panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.72f);
            panel.style.borderTopLeftRadius = 14;
            panel.style.borderTopRightRadius = 14;
            panel.style.borderBottomLeftRadius = 14;
            panel.style.borderBottomRightRadius = 14;
            root.Add(panel);

            var title = new Label("Выбор сервера");
            title.style.fontSize = 26;
            title.style.color = new Color(0.96f, 0.93f, 0.85f);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.marginBottom = 14;
            panel.Add(title);

            foreach (var c in candidates)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 8;

                var dot = new Label("•");
                dot.style.color = new Color(0.5f, 0.5f, 0.5f);
                dot.style.fontSize = 22;
                dot.style.marginRight = 8;
                row.Add(dot);

                var btn = new Button(() => Confirm(c.Url)) { text = $"{c.Name}\n{c.Url}" };
                btn.style.flexGrow = 1;
                btn.style.unityTextAlign = TextAnchor.MiddleLeft;
                btn.style.fontSize = 16;
                btn.style.paddingTop = 8;
                btn.style.paddingBottom = 8;
                btn.style.paddingLeft = 14;
                btn.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
                btn.style.color = new Color(0.9f, 0.9f, 0.9f);
                row.Add(btn);
                panel.Add(row);

                _ = ProbeAsync(c.Url, ct).ContinueWith(t =>
                {
                    if (dot.panel == null) return; // screen already gone
                    dot.style.color = t.Result.ok ? new Color(0.4f, 0.85f, 0.4f) : new Color(0.85f, 0.35f, 0.35f);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            var customLabel = new Label("Свой сервер (URL до /api)");
            customLabel.style.color = new Color(0.80f, 0.72f, 0.56f);
            customLabel.style.fontSize = 15;
            customLabel.style.marginTop = 12;
            customLabel.style.marginBottom = 6;
            panel.Add(customLabel);

            bool customKnown = candidates.Exists(c => c.Url == savedCustom);
            var field = new TextField { value = !customKnown ? savedCustom : "" };
            field.style.fontSize = 18;
            var input = field.Q(TextField.textInputUssName);
            if (input != null)
            {
                input.style.backgroundColor = new Color(0.11f, 0.11f, 0.13f);
                input.style.color = new Color(0.9f, 0.9f, 0.9f);
                input.style.paddingTop = 10;
                input.style.paddingBottom = 10;
                input.style.paddingLeft = 12;
                input.style.paddingRight = 12;
            }
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Confirm(field.value);
            });
            panel.Add(field);

            var connect = new Button(() => Confirm(field.value)) { text = "Подключиться" };
            connect.style.marginTop = 12;
            connect.style.fontSize = 18;
            connect.style.paddingTop = 10;
            connect.style.paddingBottom = 10;
            connect.style.backgroundColor = new Color(0.78f, 0.63f, 0.31f);
            connect.style.color = new Color(0.08f, 0.08f, 0.10f);
            panel.Add(connect);

            return await tcs.Task;
        }
    }
}
