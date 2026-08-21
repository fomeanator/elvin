using System;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Плавное проявление и уход на канвас-сцене.
    ///
    /// <para>Персонажи в канвас-пути включались и выключались мгновенно:
    /// <c>SetActive(true/false)</c>. Виды переходов (<c>enter=</c>/<c>exit=</c>) в
    /// движке были, но жили только в UITK-ветке — а продукт рисует сцену
    /// канвасом. Получалось «в языке есть, на экране нет»: автор пишет переход,
    /// игрок видит скачок.</para>
    ///
    /// <para>Компонент вешается на объект актёра и ведёт альфу его
    /// <see cref="CanvasGroup"/>. Живёт ровно столько, сколько идёт переход, и
    /// сам себя выключает — сцена не платит за движение, которого нет.</para>
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class LvnFade : MonoBehaviour
    {
        private CanvasGroup _group;
        private float _from, _to, _start = -1f, _dur;
        private Action _done;

        /// <summary>Вести альфу <paramref name="group"/> к <paramref name="to"/> за
        /// <paramref name="seconds"/>. Новый вызов отменяет предыдущий: показ
        /// посреди ухода не должен доигрывать чужое исчезновение.</summary>
        public static void Play(CanvasGroup group, float from, float to, float seconds, Action done = null)
        {
            if (group == null) { done?.Invoke(); return; }
            var f = group.GetComponent<LvnFade>() ?? group.gameObject.AddComponent<LvnFade>();
            f._group = group;
            f._done?.Invoke();      // прошлый переход отпускает свой хвост сам
            f._done = done;
            if (seconds <= 0.001f)
            {
                group.alpha = to;
                f._start = -1f;
                f.enabled = false;
                var d = f._done; f._done = null; d?.Invoke();
                return;
            }
            f._from = from;
            f._to = to;
            f._dur = seconds;
            f._start = Time.realtimeSinceStartup;
            group.alpha = from;
            f.enabled = true;
        }

        /// <summary>Оборвать переход, если он идёт: значение остаётся текущим, а
        /// хвост НЕ выполняется — иначе отменённый уход всё равно спрячет
        /// актёра, которого только что показали.</summary>
        public static void Cancel(CanvasGroup group)
        {
            var f = group != null ? group.GetComponent<LvnFade>() : null;
            if (f == null) return;
            f._done = null;
            f._start = -1f;
            f.enabled = false;
        }

        private void Update()
        {
            if (_start < 0f || _group == null) { enabled = false; return; }
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _start) / _dur);
            // Плавно на входе и на выходе: линейное проявление читается как
            // мигание подсветки, а не как появление человека.
            _group.alpha = Mathf.Lerp(_from, _to, t * t * (3f - 2f * t));
            if (t < 1f) return;
            _start = -1f;
            enabled = false;
            var d = _done; _done = null; d?.Invoke();
        }
    }
}
