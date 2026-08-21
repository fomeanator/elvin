using System;
using UnityEngine;
using UnityEngine.UI;

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
    ///
    /// <para><b>СОСТАВНОЙ ГЕРОЙ ГАСНЕТ НЕ АЛЬФОЙ.</b> Альфа группы применяется
    /// К КАЖДОМУ слою отдельно, поэтому на середине перехода полупрозрачная
    /// одежда начинает пропускать тело — со стороны это читается как «одежда
    /// исчезает быстрее тела». Дело не в скорости: два полупрозрачных слоя друг
    /// над другом дают не то же, что один полупрозрачный композит, и никакой
    /// подбор кривых этого не исправит. Тот же вывод однажды уже был сделан для
    /// приглушения не-говорящего — там яркость ведут ЦВЕТОМ, а не альфой (см.
    /// <c>WorldStage.SetFocus</c>).</para>
    ///
    /// <para>Поэтому многослойный герой гаснет ШЕЙДЕРОМ: порог решается для
    /// ПИКСЕЛЯ ЭКРАНА, и в одной точке все слои исчезают разом (см. <c>_Fade</c>
    /// в LvnSpriteFx). Рентгену взяться неоткуда, темнить ничего не нужно,
    /// прозрачная вуаль остаётся прозрачной. Одному изображению это не нужно —
    /// сквозь него ничего не просвечивает, и там достаточно обычной альфы,
    /// которая дешевле и не трогает материал.</para>
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class LvnFade : MonoBehaviour
    {
        private CanvasGroup _group;
        private float _from, _to, _start = -1f, _dur;
        private Action _done;
        private bool _viaShader;        // true у составного героя

        /// <summary>Нужен ли шейдерный путь: у одного изображения просвечивать
        /// нечему, у нескольких — есть. Чистая проверка, чтобы правило было
        /// видно и проверяемо, а не спрятано в середине перехода.</summary>
        public static bool NeedsCompositeFade(int graphicCount) => graphicCount > 1;

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
                f._viaShader = false;
                f.Release();
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
            f._viaShader = NeedsCompositeFade(group.GetComponentsInChildren<Graphic>(true).Length);
            f.Apply(from);
            f.enabled = true;
        }

        /// <summary>Поставить текущее значение перехода — тем путём, который
        /// этому герою подходит.</summary>
        private void Apply(float k)
        {
            if (_viaShader)
            {
                // Альфа группы остаётся хозяйской (размещение, приглушение
                // не-говорящего), гашением занимается шейдер.
                LvnSpriteFxDriver.SetFade(gameObject, k);
            }
            else if (_group != null) _group.alpha = k;
        }

        /// <summary>Отпустить гашение: материал возвращается к обычному виду,
        /// иначе следующий выход героя начнётся с чужого значения.</summary>
        private void Release()
        {
            if (_viaShader) LvnSpriteFxDriver.SetFade(gameObject, 1f);
            _viaShader = false;
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
            f.Release();
        }

        private void Update()
        {
            if (_start < 0f || _group == null) { enabled = false; return; }
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _start) / _dur);
            // Плавно на входе и на выходе: линейное проявление читается как
            // мигание подсветки, а не как появление человека.
            float k = Mathf.Lerp(_from, _to, t * t * (3f - 2f * t));
            Apply(k);
            if (t < 1f) return;
            _start = -1f;
            enabled = false;
            // ПОРЯДОК: сначала хвост (он прячет ушедшего), и только потом
            // отпустить гашение. Наоборот — и мы на один кадр покажем героя во
            // всей красе перед тем, как он исчезнет.
            var d = _done; _done = null; d?.Invoke();
            Release();
        }
    }
}
