using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Services
{
    /// <summary>
    /// ЧЕМ ПОЛЬЗУЮТСЯ, КУДА ЖМУТ И СКОЛЬКО ВРЕМЕНИ ПРОВОДЯТ — на каждом экране
    /// (TR-126, Илья 15.09: «хочу, чтобы мы знали, чем человек пользуется,
    /// куда кликает и сколько раз, сколько времени проводит — везде»).
    ///
    /// <para>Дёшево: не событие на тап, а СЧЁТЧИКИ. Один слушатель на корне
    /// дерева считает тапы по имени элемента в рамках экрана, тик раз в
    /// секунду копит время на экране; раз в минуту и при уходе в фон счётчики
    /// уезжают одним событием <c>ui_use</c> и обнуляются. Минута игрока — одна
    /// строка в несколько сотен байт, а не тысяча.</para>
    ///
    /// <para>Быстро: на тап — поиск имени по цепочке родителей и инкремент в
    /// словаре, без строк по кадру; ничего не выделяется, пока не пришла
    /// минута.</para>
    ///
    /// <para>Имя элемента — его <c>name</c> или имя ближайшего именованного
    /// родителя; у безымянной кнопки — её надпись с приставкой «txt:»; тап
    /// мимо всего — «tap» (в главе это и есть перелистывание).</para>
    /// </summary>
    public static class LvnUsage
    {
        private const int FlushEveryMs = 60_000;
        private const int Depth = 8;           // сколько родителей опрашиваем за именем
        private const int MaxKeys = 400;       // предохранитель: словарь не растёт без предела
        private const int LabelMax = 24;

        private static readonly Dictionary<string, int> _taps = new Dictionary<string, int>();
        // ЦЕПОЧКА «НАЖАЛ → СДЕЛАЛ» (Илья 16.09): последний тап помнится полминуты,
        // и первое значимое действие после него — событие аналитики или переход
        // на другой экран — засчитывается паре «экран/элемент>исход». Последний
        // тап побеждает: исход относится к тому, что нажали ближе всего к нему.
        private static readonly Dictionary<string, int> _chains = new Dictionary<string, int>();
        private static string _pendingTap;
        private static float _pendingAt = -1f;
        private const float ChainWindowSec = 30f;
        private static readonly Dictionary<string, float> _seconds = new Dictionary<string, float>();
        private static string _screen = "boot";
        private static float _lastTick = -1f;
        private static bool _booted;

        /// <summary>Текущий экран — ставит хост, когда экран сменился.</summary>
        public static string Screen
        {
            get => _screen;
            set
            {
                if (string.IsNullOrEmpty(value) || value == _screen) return;
                _screen = value;
                NoteOutcome("screen:" + value);   // тап, который привёл на экран
            }
        }

        /// <summary>Значимое действие — исход для последнего тапа, если он был
        /// не позже полминуты назад. Зовут LvnAnalytics.Track и смена экрана.</summary>
        internal static void NoteOutcome(string outcome)
        {
            if (_pendingTap == null || string.IsNullOrEmpty(outcome) || outcome == LvnEvents.UiUse) return;
            if (Time.realtimeSinceStartup - _pendingAt > ChainWindowSec) { _pendingTap = null; return; }
            Bump(_chains, _pendingTap + ">" + outcome);
            _pendingTap = null;
        }

        /// <summary>Повесить сборщик на корень дерева интерфейса.</summary>
        public static void Boot(VisualElement root)
        {
            if (_booted || root == null || string.IsNullOrEmpty(LvnBackend.BaseUrl)) return;
            _booted = true;
            _lastTick = Time.realtimeSinceStartup;
            // TrickleDown: тап считается до того, как кто-то остановит всплытие.
            root.RegisterCallback<PointerUpEvent>(OnTap, TrickleDown.TrickleDown);
            root.schedule.Execute(Tick).Every(1000);
            root.schedule.Execute(Flush).Every(FlushEveryMs);
        }

        private static void OnTap(PointerUpEvent e)
        {
            if (e.button != 0) return;
            string key = Identify(e.target as VisualElement);
            Bump(_taps, _screen + "/" + key);
            _pendingTap = _screen + "/" + key; _pendingAt = Time.realtimeSinceStartup;
        }

        /// <summary>Имя того, во что попали: своё, ближайшего именованного
        /// родителя, надпись кнопки или «tap».</summary>
        internal static string Identify(VisualElement el)
        {
            string label = null;
            for (int i = 0; el != null && i < Depth; i++, el = el.parent)
            {
                var name = el.name;
                if (!string.IsNullOrEmpty(name) && !name.StartsWith("unity-", StringComparison.Ordinal)) return name;
                if (label == null && el is TextElement t && !string.IsNullOrEmpty(t.text)) label = t.text;
            }
            if (label == null) return "tap";
            return "txt:" + LvnClip.Text(label.Trim(), LabelMax);
        }

        private static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (_lastTick >= 0f && Application.isFocused)
            {
                float dt = Mathf.Clamp(now - _lastTick, 0f, 5f);   // сон приложения не считается временем на экране
                _seconds.TryGetValue(_screen, out var had);
                if (_seconds.Count < MaxKeys || _seconds.ContainsKey(_screen)) _seconds[_screen] = had + dt;
            }
            _lastTick = now;
        }

        private static void Bump(Dictionary<string, int> m, string key)
        {
            if (m.TryGetValue(key, out var n)) { m[key] = n + 1; return; }
            if (m.Count >= MaxKeys) key = "…";   // переполнение видно как отдельная строка
            m.TryGetValue(key, out n);
            m[key] = n + 1;
        }

        /// <summary>Отдать накопленное одним событием и обнулить. Зовётся по
        /// таймеру и при уходе в фон.</summary>
        public static void Flush()
        {
            if (_taps.Count == 0 && _seconds.Count == 0 && _chains.Count == 0) return;
            var taps = new JObject();
            foreach (var kv in _taps) taps[kv.Key] = kv.Value;
            var chains = new JObject();
            foreach (var kv in _chains) chains[kv.Key] = kv.Value;
            var time = new JObject();
            foreach (var kv in _seconds)
            {
                int s = Mathf.RoundToInt(kv.Value);
                if (s > 0) time[kv.Key] = s;
            }
            _taps.Clear(); _seconds.Clear(); _chains.Clear();
            if (taps.Count == 0 && time.Count == 0 && chains.Count == 0) return;
            if (chains.Count > 0) LvnAnalytics.Track(LvnEvents.UiUse, ("taps", taps), ("time", time), ("chains", chains));
            else LvnAnalytics.Track(LvnEvents.UiUse, ("taps", taps), ("time", time));
        }
    }
}
