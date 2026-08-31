using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Тап-салют: сердечки, разлетающиеся из точки касания (жанровый знак —
    /// 7 Hard Stories и родня). Чистый UI Toolkit: векторные иконки-сердца
    /// с простой физикой (всплытие, разлёт, вращение, растворение), слой
    /// прозрачен для ввода и живёт ПОВЕРХ всего хрома — салют рождается от
    /// тапа по любому элементу, не мешая самому элементу сработать
    /// (стейдж ловит касание trickle-down'ом). Включается данными:
    /// ui.stage.tap_burst = "hearts".
    /// </summary>
    public sealed class TapBurstLayer : VisualElement
    {
        private struct Heart
        {
            public VisualElement El;
            public float X, Y;      // px внутри слоя
            public float Vx, Vy;    // px/с
            public float Rot, RotV; // градусы
            public float Age, Life; // секунды
            public float Size;
        }

        private readonly List<Heart> _live = new List<Heart>();
        private IVisualElementScheduledItem _tick;
        private float _last;

        // Палитра салюта: тёплые сердечные, чуть разные — глаз читает «живое».
        private static readonly Color[] Tints =
        {
            new Color(1.00f, 0.36f, 0.54f), // розовый
            new Color(1.00f, 0.55f, 0.66f), // светло-розовый
            new Color(0.98f, 0.27f, 0.42f), // малиновый
        };

        public TapBurstLayer()
        {
            LvnChrome.Stretch(this);
            style.overflow = Overflow.Hidden;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Салют из точки (локальные px этого слоя).</summary>
        public void Burst(Vector2 at)
        {
            int n = Random.Range(6, 9);
            for (int i = 0; i < n; i++)
            {
                float size = Random.Range(18f, 34f);
                var el = LvnIcons.Make(LvnIcon.Heart, size,
                    Tints[Random.Range(0, Tints.Length)]);
                el.pickingMode = PickingMode.Ignore;
                el.style.position = Position.Absolute;
                Add(el);
                // Веер вверх: каждый со своим углом и запалом.
                float ang = Random.Range(35f, 145f) * Mathf.Deg2Rad;
                float pow = Random.Range(160f, 340f);
                _live.Add(new Heart
                {
                    El = el,
                    X = at.x, Y = at.y,
                    Vx = Mathf.Cos(ang) * pow * (Random.value < 0.5f ? 1f : -1f) * 0.6f,
                    Vy = -Mathf.Sin(ang) * pow,
                    Rot = Random.Range(-25f, 25f),
                    RotV = Random.Range(-140f, 140f),
                    Age = 0f,
                    Life = Random.Range(0.7f, 1.15f),
                    Size = size,
                });
            }
            if (_tick == null) _tick = schedule.Execute(Tick).Every(16);
            _tick.Resume();
            _last = LvnClock.Now();
        }

        private void Tick()
        {
            float now = LvnClock.Now();
            float dt = Mathf.Min(0.05f, now - _last);
            _last = now;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var h = _live[i];
                h.Age += dt;
                if (h.Age >= h.Life)
                {
                    h.El.RemoveFromHierarchy();
                    _live.RemoveAt(i);
                    continue;
                }
                // Всплытие с лёгким торможением: сердечки — не шрапнель.
                h.Vy += 60f * dt;      // мягкая «антигравитация» гаснет
                h.Vx *= 1f - 1.6f * dt;
                h.X += h.Vx * dt;
                h.Y += h.Vy * dt;
                h.Rot += h.RotV * dt;
                float k = h.Age / h.Life;
                float scale = k < 0.15f ? Mathf.Lerp(0.4f, 1.1f, k / 0.15f) // рождение-пульс
                    : Mathf.Lerp(1.1f, 0.85f, (k - 0.15f) / 0.85f);
                h.El.style.left = h.X - h.Size * 0.5f;
                h.El.style.top = h.Y - h.Size * 0.5f;
                h.El.style.opacity = 1f - k * k;              // тает к концу
                h.El.style.rotate = new Rotate(new Angle(h.Rot, AngleUnit.Degree));
                h.El.style.scale = new Scale(new Vector2(scale, scale));
                _live[i] = h;
            }
            if (_live.Count == 0) _tick.Pause();
        }
    }
}
