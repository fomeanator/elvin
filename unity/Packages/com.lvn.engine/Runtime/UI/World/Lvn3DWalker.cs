using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Ходьба по 3D-набору: WASD с клавиатуры и экранный джойстик на тач.
    ///
    /// <para>Зачем это в движке новеллы. Ракурс набора обычно ставит автор —
    /// `bg3d x= y= z=`, и этого хватает, пока сцена служит задником. Но пока
    /// подбираешь этот ракурс, каждая правка стоит цикла «поменял число →
    /// пересобрал → посмотрел», и в это уходит больше времени, чем в саму
    /// сцену. Возможность просто пройтись внутри превращает подбор кадра в
    /// секунды: встал, куда надо, списал координаты, вписал в скрипт.</para>
    ///
    /// <para>Дальше это же нужно и игроку — в сценах, где место само по себе
    /// содержание: осмотреть комнату, обойти алтарь, заглянуть за угол.
    /// Поэтому режим включается из скрипта (<c>bg3d walk=1</c>), а не только
    /// в отладке.</para>
    ///
    /// <para>Движение идёт В ПЛОСКОСТИ ЗЕМЛИ: камера не взлетает от того, что
    /// смотрит вверх. Иначе «иду вперёд, глядя на небо» уносит из сцены за
    /// пару секунд — классическая ошибка свободной камеры.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DWalker : MonoBehaviour
    {
        /// <summary>Метров в секунду. Шаг человека — примерно столько.</summary>
        public float Speed = 2.2f;
        /// <summary>Ускорение бегом (Shift / джойстик до упора).</summary>
        public float RunFactor = 2.4f;

        private Lvn3DBackdrop _backdrop;
        private Vector2 _stick;      // −1..1 от экранного джойстика
        private bool _stickActive;

        public static Lvn3DWalker Ensure(Lvn3DBackdrop backdrop)
        {
            if (backdrop == null) return null;
            var w = backdrop.GetComponent<Lvn3DWalker>() ?? backdrop.gameObject.AddComponent<Lvn3DWalker>();
            w._backdrop = backdrop;
            return w;
        }

        /// <summary>Положение виртуального стика: x — вбок, y — вперёд, −1..1.
        /// Ноль по обеим осям снимает ввод.</summary>
        public void SetStick(Vector2 v)
        {
            _stick = Vector2.ClampMagnitude(v, 1f);
            _stickActive = _stick.sqrMagnitude > 0.0004f;
        }

        private void Update()
        {
            if (_backdrop == null || !_backdrop.Active) return;

            float x = 0f, z = 0f, up = 0f;
            bool run = false;

            // Клавиатура: WASD и стрелки. Опрашиваем старым Input — он есть в
            // любой сборке, а новая система ввода в проекте не обязательна.
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) z += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) z -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Space)) up += 1f;
            if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftControl)) up -= 1f;
            run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (_stickActive)
            {
                x += _stick.x;
                z += _stick.y;
                run = run || _stick.magnitude > 0.9f;
            }

            if (Mathf.Abs(x) < 0.001f && Mathf.Abs(z) < 0.001f && Mathf.Abs(up) < 0.001f) return;

            float speed = Speed * (run ? RunFactor : 1f) * Time.unscaledDeltaTime;
            _backdrop.Walk(new Vector3(x, up, z) * speed);
        }
    }
}
