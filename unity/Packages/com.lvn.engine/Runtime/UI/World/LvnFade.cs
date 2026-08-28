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
    ///
    /// <para><b>ГАШЕНИЕ ОДНО — АЛЬФА ГРУППЫ.</b> Их успело завестись несколько:
    /// альфа, «проявление яркостью» (герой темнел до силуэта) и шейдерная
    /// экранная маска. Каждое лечило чужую болячку и заводило свою: яркость
    /// давала чёрное затемнение на уходе и переписывала цвет КАЖДОГО слоя
    /// каждый кадр — канвас перестраивался на каждом кадре перехода, отсюда
    /// микрозадержки на показе и скрытии. Осталась альфа: без затемнения и без
    /// изменений материала каждый кадр.</para>
    ///
    /// <para>Составной герой на время обычного перехода заменяется одним
    /// <see cref="LvnActorComposite"/>. Поэтому эта альфа применяется уже к
    /// готовому силуэту, а не отдельно к телу и одежде: внутреннего «рентгена»
    /// нет. Однослойный герой остаётся на прямом пути без proxy.</para>
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class LvnFade : MonoBehaviour
    {
        // Opacity is only the soft edge of the move, not a translucent flight.
        // Ten percent keeps the figure materially present for almost the whole
        // gesture; SmoothStep below removes the single harsh alpha edge.
        private const float FadeWindow = 0.10f;

        private CanvasGroup _group;
        private float _from, _to, _start = -1f, _dur;
        private Action _done;
        /// <summary>Куда сносит персонажа вид drift: правый (x ≥ 0.5) — вправо,
        /// левый — влево. Правило вынесено отдельно, потому что это КОНТРАКТ
        /// постановки («левый уходит влево»), а не деталь анимации.</summary>
        public static float DriftSign(float x01) => x01 >= 0.5f ? 1f : -1f;

        /// <summary>Opacity progress inside a full actor-transition timeline.
        ///
        /// <para>ВХОД — ФЕЙД НА ВЕСЬ ХОД. Раньше входящий становился плотным в
        /// первые 10% пути и дальше ехал непрозрачным — читалось как резкое
        /// «вспыхнул и поехал» (решение Ильи 25.08: перс и диалог появляются
        /// фейдом на полное время и въезжают вместе).</para>
        ///
        /// <para>УХОД С ДВИЖЕНИЕМ держит старую кромку: уходящий обязан быть
        /// плотным почти весь путь — полупрозрачная фигура, ползущая через
        /// сцену, читается как призрак; гаснет он в конце. Уход без движения
        /// (`fade`) гасится весь ход — короткое окно превращало его в 38 мс
        /// мигания после трети секунды неподвижного стояния.</para></summary>
        public static float OpacityProgress(float time01, bool appearing, bool withMotion)
        {
            float local = appearing || !withMotion
                ? Mathf.Clamp01(time01)
                : Mathf.Clamp01((time01 - (1f - FadeWindow)) / FadeWindow);
            return local * local * (3f - 2f * local);
        }

        private RectTransform _slot;    // кого сносим (отдельный transition-root)
        private Vector2 _slotBase;      // его место до перехода
        private Vector2 _drift;         // полный снос в пикселях канваса

        /// <summary>То же проигрывание, но с боковым сносом: на нуле альфы слот
        /// смещён на <paramref name="drift"/>, на единице — на своём месте.
        /// Формула одна для входа и выхода: смещение = (1−k)·drift.</summary>
        public static void Play(CanvasGroup group, float from, float to, float seconds,
                                RectTransform slot, Vector2 drift, Action done = null)
        {
            // Порядок важен: базовый Play снимает снос ПРЕДЫДУЩЕГО перехода,
            // и только после этого текущее положение слота — настоящий дом.
            // Иначе прерванный уход отдаёт сдвинутую позицию как «родную», и с
            // каждым перебитым переходом персонаж уезжает вбок навсегда.
            Play(group, from, to, seconds, done);
            var f = group != null ? group.GetComponent<LvnFade>() : null;
            // Мгновенный путь уже отработал и отпустил всё — цеплять к нему снос
            // значит оставить слот сдвинутым навсегда.
            if (f == null || slot == null || seconds <= 0.001f) return;
            f._slot = slot;
            f._slotBase = slot.anchoredPosition;
            f._drift = drift;
            f.ApplyDrift(0f);
        }

        /// <summary>Поставить снос по ДОЛЕ ПРОЙДЕННОГО ВРЕМЕНИ, а не по альфе.
        /// Альфа не годится: у героя с placement-opacity 0.6 вход кончается на
        /// k=0.6, и «(1−k)» оставляет его сдвинутым на 40% сноса — до самого
        /// конца, где Release дёргает его на место рывком.
        ///
        /// <para>Кривая движения симметричная: мягко разгоняется и тормозит.
        /// Старый ease-out был рассчитан на 0.17 s; после удлинения перехода он
        /// отдавал треть пути в первые кадры и выглядел как рывок.</para></summary>
        private void ApplyDrift(float t01)
        {
            if (_slot == null) return;
            float t = Mathf.Clamp01(t01);
            float e = t * t * (3f - 2f * t);
            float away = _to > _from ? 1f - e : e;   // вход: снаружи домой; уход: наоборот
            var next = _slotBase + _drift * away;
            _slot.anchoredPosition = next;
        }

        /// <summary>Вернуть слот на место — переход кончился или его оборвали.
        /// Не вернуть — и следующая команда постановки прочтёт сдвинутую позицию
        /// как «родную».</summary>
        private void ReleaseDrift()
        {
            if (_slot == null) return;
            // Transition-root имеет ровно одного владельца: LvnFade. Постановка
            // и position-tween двигают его РОДИТЕЛЬСКИЙ Slot, поэтому здесь нет
            // законного конкурирующего писателя, которого надо защищать. Старое
            // условие по equality оставляло полный drift (48.6 px на 1080) после
            // некоторых disable/cancel последовательностей — особенно после
            // живой пересборки героя гардеробом.
            _slot.anchoredPosition = _slotBase;
            _slot = null;
        }

        // Деактивация актёра/сцены может случиться из хвоста перехода или при
        // перестройке UI. Unity больше не вызовет Update у выключенного объекта,
        // поэтому возвращаем единственный принадлежащий нам transform здесь тоже.
        private void OnDisable() => ReleaseDrift();

        /// <summary>Вести альфу <paramref name="group"/> к <paramref name="to"/> за
        /// <paramref name="seconds"/>. Новый вызов отменяет предыдущий: показ
        /// посреди ухода не должен доигрывать чужое исчезновение.</summary>
        public static void Play(CanvasGroup group, float from, float to, float seconds, Action done = null)
        {
            if (group == null) { done?.Invoke(); return; }
            var f = group.GetComponent<LvnFade>() ?? group.gameObject.AddComponent<LvnFade>();
            f._group = group;
            // ХВОСТ ПРОШЛОГО ПЕРЕХОДА НЕ ИСПОЛНЯЕТСЯ. Он выполнял его: повторное
            // скрытие во время ухода тут же дёргало старый хвост, тот
            // деактивировал объект — а выключенный объект не тикает, и новый
            // переход замирал на полпути. Новый переход ОТМЕНЯЕТ старый; свой
            // хвост он доведёт сам.
            f._done = null;
            f.ReleaseDrift();       // и свой снос — до того, как новый запомнит дом
            f._done = done;
            if (seconds <= 0.001f)
            {
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
            f._start = LvnClock.Now();
            // from == to — ЧИСТЫЙ ТАЙМЕР: вид не трогаем, просто ждём и зовём
            // хвост. Так работает уход через растворение — гашением занят
            // _Dissolve, а этот компонент лишь прячет объект в конце.
            if (from != to) f.Apply(from, 0f);
            f.enabled = true;
        }

        /// <summary>Поставить текущее значение перехода — тем путём, который
        /// этому герою подходит.</summary>
        private void Apply(float k, float t01)
        {
            ApplyDrift(t01);
            if (_group != null) _group.alpha = k;
        }

        /// <summary>Закончить общую альфу и вернуть transition-root домой.</summary>
        private void Release()
        {
            if (_group != null) _group.alpha = _to;
            ReleaseDrift();
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
            float t = Mathf.Clamp01((LvnClock.Now() - _start) / _dur);
            // The actor should not cross the stage as a ghost. Entrance becomes
            // solid in the opening 10%; exit fades only in the closing 10%.
            // Drift still receives raw t and therefore spans the whole motion.
            // Без сноса (уход по умолчанию — `fade`) гасить нечему на кромке:
            // там прозрачность и есть весь переход.
            bool withMotion = _slot != null && _drift != Vector2.zero;
            float alphaT = OpacityProgress(t, _to > _from, withMotion);
            float k = Mathf.Lerp(_from, _to, alphaT);
            if (_from != _to) Apply(k, t);
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
