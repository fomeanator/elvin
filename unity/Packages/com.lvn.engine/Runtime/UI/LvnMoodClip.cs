using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ПРОИГРЫВАТЕЛЬ РЕАКЦИЙ ВИТРИНЫ (TR-66) — короткий клип с метки до конца.
    ///
    /// <para>Реакции героини написаны обычным сценарием: метка
    /// <c>on_purchase</c>, дальше смены лица и паузы. Играть его полным
    /// плеером главы нельзя и не нужно: тот ведёт переменные, выборы,
    /// сохранения и историю — целую жизнь, которой у витрины нет. Здесь
    /// исполняется ровно то, что нужно клипу: команды сцены подряд и
    /// <c>wait</c> между ними.</para>
    ///
    /// <para>ВСЁ ИДЁТ ОТПРАВИТЕЛЕМ <see cref="LvnSender.Menu"/>: слой меню
    /// эксклюзивен, и кадр главы клип не трогает. Новый клип обрывает
    /// прежний — событие важнее того, что героиня доигрывает.</para>
    ///
    /// <para>Реплики и выборы в клипе ИГНОРИРУЮТСЯ с предупреждением: витрина
    /// — не сцена, диалогу тут негде встать (фаза 2 — облачко над героиней,
    /// см. docs/heroine-reactions.md).</para>
    /// </summary>
    public sealed class LvnMoodClip
    {
        private readonly JArray _script;
        private readonly Func<JObject, bool> _play;
        private readonly MonoBehaviour _host;
        private Coroutine _running;

        /// <param name="script">скомпилированный сценарий витрины</param>
        /// <param name="host">кто крутит корутину — сцена</param>
        /// <param name="play">исполнить команду; false — команда не для витрины</param>
        public LvnMoodClip(JArray script, MonoBehaviour host, Func<JObject, bool> play)
        {
            _script = script; _host = host; _play = play;
        }

        /// <summary>Метки, которые есть в сценарии, — их спрашивает дом
        /// настроения, чтобы не обещать реакций, которых не написали.</summary>
        public HashSet<string> Labels()
        {
            var set = new HashSet<string>();
            if (_script == null) return set;
            foreach (var t in _script)
                if (t is JObject c && (string)c["op"] == "label")
                {
                    var id = (string)c["id"] ?? (string)c["name"];
                    if (!string.IsNullOrEmpty(id)) set.Add(id);
                }
            return set;
        }

        /// <summary>Играет ли что-то прямо сейчас.</summary>
        public bool Busy => _running != null;

        /// <summary>Оборвать клип: событие важнее того, что доигрывается.</summary>
        public void Stop()
        {
            if (_running != null && _host != null) _host.StopCoroutine(_running);
            _running = null;
        }

        /// <summary>
        /// Сыграть клип с метки. <paramref name="onEnd"/> зовётся, когда клип
        /// дошёл до конца САМ — оборванный конца не объявляет: иначе дом
        /// настроения вернул бы комнату поверх нового клипа.
        /// </summary>
        public bool Play(string label, Action onEnd)
        {
            int at = IndexOf(label);
            if (at < 0 || _host == null) return false;
            Stop();
            _running = _host.StartCoroutine(Run(at + 1, onEnd));
            return true;
        }

        private int IndexOf(string label)
        {
            if (_script == null || string.IsNullOrEmpty(label)) return -1;
            for (int i = 0; i < _script.Count; i++)
                if (_script[i] is JObject c && (string)c["op"] == "label"
                    && ((string)c["id"] == label || (string)c["name"] == label)) return i;
            return -1;
        }

        private IEnumerator Run(int from, Action onEnd)
        {
            for (int i = from; i < _script.Count; i++)
            {
                if (!(_script[i] is JObject c)) continue;
                var op = (string)c["op"];
                // КЛИП КОНЧАЕТСЯ СЛЕДУЮЩЕЙ МЕТКОЙ. Отдельного «конца» писать не
                // надо: реакции идут в файле подряд, и метка — их граница.
                if (op == "label" || op == "end") break;
                if (op == "wait")
                {
                    float sec = Seconds(c);
                    if (sec > 0f) yield return new WaitForSeconds(sec);
                    continue;
                }
                if (op == "say" || op == "choice")
                {
                    LvnLog.Warn($"[lvn-mood] «{op}» в реакции витрины пропущен: диалогу тут негде встать");
                    continue;
                }
                _play?.Invoke(c);
            }
            _running = null;
            onEnd?.Invoke();
        }

        /// <summary>Пауза клипа: <c>ms=</c> или <c>seconds=</c> — как пишет
        /// автор в сценарии главы, теми же словами.</summary>
        private static float Seconds(JObject c)
        {
            var ms = c["ms"] ?? c["millis"];
            if (ms != null) { try { return (float)ms / 1000f; } catch { return 0f; } }
            var sec = c["seconds"] ?? c["sec"] ?? c["duration"];
            if (sec != null) { try { return (float)sec; } catch { return 0f; } }
            return 0f;
        }
    }
}
