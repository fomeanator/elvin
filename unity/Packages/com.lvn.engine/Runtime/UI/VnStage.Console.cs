using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Отладочная консоль прямо в игре: где мы сейчас, что в переменных, куда
    /// прыгнуть.
    ///
    /// <para>Есть у всех движков жанра (Ren'Py открывает свою на Shift+O), и по
    /// одной причине: отлаживать сюжет с ветвлениями, проходя его заново до
    /// нужного места, невозможно. Автору нужно попасть в двадцатую сцену за
    /// секунду и посмотреть, чему равна переменная.</para>
    ///
    /// <para>Показывает: текущую позицию в скрипте, последние выполненные
    /// команды, все переменные и список меток. Метку можно нажать — плеер
    /// прыгнет туда. Открывается из бургер-меню, а на устройстве — тройным
    /// касанием верхнего левого угла (клавиатуры там нет).</para>
    /// </summary>
    public sealed partial class VnStage
    {
        private VisualElement _console;
        private Label _consoleState;
        private ScrollView _consoleVars, _consoleLabels;
        private readonly List<string> _consoleTrace = new List<string>();

        /// <summary>Пункт меню появляется САМ в отладочной сборке: в релизной
        /// он не нужен, а автору не должно приходиться его включать.</summary>
        internal void RegisterConsoleMenuItem()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            StageMenu.AddMenuItem("Консоль", stage => stage?.ToggleConsole());
#endif
        }

        /// <summary>Открыть или закрыть консоль.</summary>
        public void ToggleConsole()
        {
            if (_console == null) BuildConsole();
            if (_console == null) return;
            bool show = _console.style.display == DisplayStyle.None;
            _console.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show) RefreshConsole();
        }

        /// <summary>Запомнить выполненную команду — консоль показывает хвост
        /// истории, и по нему видно, что сцена делала перед тем, как сломаться.</summary>
        internal void TraceForConsole(string line)
        {
            _consoleTrace.Add(line);
            if (_consoleTrace.Count > 40) _consoleTrace.RemoveAt(0);
        }

        private void BuildConsole()
        {
            if (_labelLayer == null) return;

            _console = new VisualElement { name = "vn-console" };
            _console.style.position = Position.Absolute;
            _console.style.left = 0; _console.style.top = 0;
            _console.style.right = 0; _console.style.bottom = 0;
            _console.style.backgroundColor = new Color(0.04f, 0.05f, 0.07f, 0.94f);
            _console.style.paddingLeft = 18; _console.style.paddingRight = 18;
            _console.style.paddingTop = 24; _console.style.paddingBottom = 18;

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = 10;

            _consoleState = new Label { name = "vn-console-state" };
            _consoleState.style.color = new Color(0.75f, 0.85f, 1f);
            _consoleState.style.fontSize = 22;
            _consoleState.style.whiteSpace = WhiteSpace.Normal;
            _consoleState.style.flexGrow = 1;

            var close = new Button(() => ToggleConsole()) { text = "закрыть" };
            close.style.height = 44;
            close.style.paddingLeft = 16; close.style.paddingRight = 16;

            head.Add(_consoleState);
            head.Add(close);
            _console.Add(head);

            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1;

            _consoleVars = MakeColumn(body, "переменные");
            _consoleLabels = MakeColumn(body, "метки — нажмите, чтобы прыгнуть");
            _console.Add(body);

            _labelLayer.Add(_console);
        }

        private ScrollView MakeColumn(VisualElement parent, string title)
        {
            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.marginRight = 12;
            var head = new Label { text = title };
            head.style.color = new Color(0.6f, 0.68f, 0.78f);
            head.style.fontSize = 16;
            head.style.marginBottom = 6;
            col.Add(head);
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            col.Add(scroll);
            parent.Add(col);
            return scroll;
        }

        private void RefreshConsole()
        {
            if (_player == null) return;

            _consoleState.text =
                $"команда #{_player.Position} из {_player.Count}" +
                (_consoleTrace.Count > 0 ? "\nпоследнее: " + _consoleTrace[_consoleTrace.Count - 1] : "");

            _consoleVars.Clear();
            foreach (var kv in _player.Vars.OrderBy(k => k.Key, System.StringComparer.Ordinal))
            {
                var text = kv.Value?.ToString(Newtonsoft.Json.Formatting.None) ?? "null";
                if (text.Length > 120) text = text.Substring(0, 117) + "…";
                var row = new Label { text = kv.Key + " = " + text };
                row.style.color = Color.white;
                row.style.fontSize = 17;
                row.style.whiteSpace = WhiteSpace.Normal;
                row.style.marginBottom = 3;
                _consoleVars.Add(row);
            }

            _consoleLabels.Clear();
            foreach (var name in _player.LabelNames())
            {
                // Служебные метки компилятора (`__fn_…`) автору не нужны: их
                // сотни, и они прячут те, что он писал сам.
                if (name.StartsWith("__")) continue;
                var btn = new Button(() =>
                {
                    ToggleConsole();
                    _player.GoTo(name);
                    _player.Advance();
                })
                { text = name };
                btn.style.height = 38;
                btn.style.marginBottom = 3;
                btn.style.unityTextAlign = TextAnchor.MiddleLeft;
                _consoleLabels.Add(btn);
            }
        }
    }
}
