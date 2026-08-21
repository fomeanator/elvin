using System;
using System.Collections.Generic;
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
    /// <para>Однослойный герой использует штатную альфу CanvasGroup. У
    /// многослойного она показывает тело сквозь полупрозрачную одежду, поэтому
    /// его слои остаются непрозрачными, а проявление ведётся яркостью из тёмного
    /// силуэта. Это не требует материала/шейдера и не создаёт прямой «шторки».</para>
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class LvnFade : MonoBehaviour
    {
        private CanvasGroup _group;
        private float _from, _to, _start = -1f, _dur;
        private Action _done;
        private bool _viaTint;
        private readonly Dictionary<Graphic, Color> _tintBase = new Dictionary<Graphic, Color>();

        private static bool NeedsLayerSafeFade(int graphicCount) => graphicCount > 1;
        /// <summary>Куда сносит персонажа вид drift: правый (x ≥ 0.5) — вправо,
        /// левый — влево. Правило вынесено отдельно, потому что это КОНТРАКТ
        /// постановки («левый уходит влево»), а не деталь анимации.</summary>
        public static float DriftSign(float x01) => x01 >= 0.5f ? 1f : -1f;

        private RectTransform _slot;    // кого сносим (слот актёра)
        private Vector2 _slotBase;      // его место до перехода
        private Vector2 _slotWrote;     // последнее, что МЫ записали в слот
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
        /// <para>Кривая у движения СВОЯ — ease-out, а не общая с гашением
        /// «плавно с обоих концов». Переход в продукте идёт около 0.17 s, и
        /// мягкий старт съедает почти весь ход: глаз видит статичного героя,
        /// который где-то в конце чуть дёрнулся. Ease-out тратит расстояние
        /// сразу и мягко тормозит — движение читается даже на коротком
        /// переходе, но не мельтешит при долгом чтении.</para></summary>
        private void ApplyDrift(float t01)
        {
            if (_slot == null) return;
            float e = 1f - Mathf.Pow(1f - Mathf.Clamp01(t01), 3f);
            float away = _to > _from ? 1f - e : e;   // вход: снаружи домой; уход: наоборот
            var next = _slotBase + _drift * away;
            _slot.anchoredPosition = next;
            _slotWrote = next;
        }

        /// <summary>Вернуть слот на место — переход кончился или его оборвали.
        /// Не вернуть — и следующая команда постановки прочтёт сдвинутую позицию
        /// как «родную».</summary>
        private void ReleaseDrift()
        {
            if (_slot == null) return;
            // Только если с тех пор в слот не писал НИКТО другой: постановка
            // (`actor id=x x=0.2`) посреди перехода уже поставила новый дом, и
            // возврат к нашей старой базе телепортировал бы героя обратно.
            if (_slot.anchoredPosition == _slotWrote) _slot.anchoredPosition = _slotBase;
            _slot = null;
        }

        /// <summary>Вести альфу <paramref name="group"/> к <paramref name="to"/> за
        /// <paramref name="seconds"/>. Новый вызов отменяет предыдущий: показ
        /// посреди ухода не должен доигрывать чужое исчезновение.</summary>
        public static void Play(CanvasGroup group, float from, float to, float seconds, Action done = null)
        {
            if (group == null) { done?.Invoke(); return; }
            var f = group.GetComponent<LvnFade>() ?? group.gameObject.AddComponent<LvnFade>();
            f._group = group;
            f._done?.Invoke();      // прошлый переход отпускает свой хвост сам
            f.ReleaseDrift();       // и свой снос — до того, как новый запомнит дом
            f._done = done;
            if (seconds <= 0.001f)
            {
                f._viaTint = false;
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
            // from == to — ЧИСТЫЙ ТАЙМЕР: вид не трогаем, просто ждём и зовём
            // хвост. Так работает уход через шейдерное растворение — гашением
            // занят _Dissolve, а этот компонент лишь прячет объект в конце.
            // Гнать сюда Apply нельзя: при placement-opacity 0.9 «таймер»
            // начал бы прорешечивать героя десятой долей пикселей.
            f._viaTint = from != to
                && NeedsLayerSafeFade(group.GetComponentsInChildren<Graphic>(true).Length);
            if (f._viaTint) f.CaptureTint();
            if (from != to) f.Apply(from, 0f);
            f.enabled = true;
        }

        /// <summary>Поставить текущее значение перехода — тем путём, который
        /// этому герою подходит.</summary>
        private void Apply(float k, float t01)
        {
            ApplyDrift(t01);
            if (_viaTint)
            {
                float peak = Mathf.Max(_from, _to);
                float light = peak > 0.001f ? Mathf.Clamp01(k / peak) : 0f;
                // ХВОСТ ВЕДЁТ АЛЬФА, СЕРЕДИНУ — ЯРКОСТЬ. Чистая яркость гасит
                // героя в НЕПРОЗРАЧНЫЙ ЧЁРНЫЙ силуэт и снимает его скачком на
                // самом нуле: на замерах уходящий персонаж за последние 10%
                // перехода прыгал из ясно видимого в ничто, а на светлом фоне
                // это ещё и чёрная вырезка вместо человека. Поэтому ниже порога
                // цвет больше не темнеет, а остаток пути герой доезжает
                // прозрачностью — на почти чёрных слоях просвечивание тела
                // сквозь одежду уже неразличимо, ради чего яркость и вводилась.
                const float floorLight = 0.35f;
                float lit = Mathf.Max(light, floorLight);
                float alpha = peak * Mathf.Clamp01(light / floorLight);
                if (_group != null) _group.alpha = alpha;
                foreach (var pair in _tintBase)
                {
                    if (pair.Key == null) continue;
                    var c = pair.Value;
                    pair.Key.color = new Color(c.r * lit, c.g * lit, c.b * lit, c.a);
                }
            }
            else if (_group != null) _group.alpha = k;
        }

        private void CaptureTint()
        {
            RestoreTint();
            foreach (var graphic in GetComponentsInChildren<Graphic>(true))
                if (graphic != null) _tintBase[graphic] = graphic.color;
        }

        private void RestoreTint()
        {
            foreach (var pair in _tintBase)
                if (pair.Key != null) pair.Key.color = pair.Value;
            _tintBase.Clear();
        }

        /// <summary>Отпустить гашение: материал возвращается к обычному виду,
        /// иначе следующий выход героя начнётся с чужого значения.</summary>
        private void Release()
        {
            RestoreTint();
            if (_group != null) _group.alpha = _to;
            _viaTint = false;
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
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _start) / _dur);
            // Плавно на входе и на выходе: линейное проявление читается как
            // мигание подсветки, а не как появление человека.
            float k = Mathf.Lerp(_from, _to, t * t * (3f - 2f * t));
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
