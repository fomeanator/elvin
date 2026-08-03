using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Движение тела сцены: переезд, поворот, рост, растворение — и постоянные
    /// движения (вращение, покачивание, пульс).
    ///
    /// <para>Зачем отдельный компонент, а не «двигать в команде». Команда
    /// исполняется мгновенно, а движение живёт во времени: дверь открывается
    /// полторы секунды, факел качается всю сцену, призрак растворяется на
    /// реплике. Всё это должно идти САМО, пока идёт диалог, и не требовать от
    /// автора ни цикла, ни ожидания.</para>
    ///
    /// <para>Ключевая деталь: пока что-то движется, кадр обязан сниматься. Об
    /// этом компонент сообщает набору сам (<see cref="Lvn3DBackdrop.SetLive"/>)
    /// — иначе автор поставит вращение и увидит застывший объект, а причину
    /// будет искать в своём скрипте.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DMotion : MonoBehaviour
    {
        private Lvn3DBackdrop _owner;

        // Переезд из состояния в состояние.
        private Vector3 _fromPos, _toPos;
        private Quaternion _fromRot, _toRot;
        private Vector3 _fromScale, _toScale;
        private float _fromDissolve, _toDissolve;
        private float _time, _duration;
        private bool _running;

        // Постоянные движения.
        private float _spin;          // градусов в секунду вокруг своей оси
        private Vector3 _bob;         // амплитуда покачивания, метры
        private float _bobSpeed = 1f; // циклов в секунду
        private float _pulse;         // пульсация размера, доля
        private float _pulseSpeed = 1f;
        private Vector3 _base;        // положение и размер, вокруг которых качаемся
        private Vector3 _baseScale;
        private float _clock;

        public static Lvn3DMotion Ensure(Transform t, Lvn3DBackdrop owner)
        {
            var m = t.GetComponent<Lvn3DMotion>() ?? t.gameObject.AddComponent<Lvn3DMotion>();
            m._owner = owner;
            return m;
        }

        /// <summary>Плавно перевести тело в заданное состояние. Значения null —
        /// «не трогать»: `o3d id=дверь yaw=90 dur=1.2` крутит дверь, не сдвигая
        /// её с места.</summary>
        public void MoveTo(Vector3? pos, Vector3? euler, Vector3? scale, float? dissolve, float seconds)
        {
            _fromPos = transform.localPosition;
            _fromRot = transform.localRotation;
            _fromScale = transform.localScale;
            _toPos = pos ?? _fromPos;
            _toRot = euler is Vector3 e ? Quaternion.Euler(e.x, e.y, e.z) : _fromRot;
            _toScale = scale ?? _fromScale;
            _fromDissolve = CurrentDissolve();
            _toDissolve = dissolve ?? _fromDissolve;

            if (seconds <= 0.001f)
            {
                Apply(1f);
                Rebase();
                _running = false;
                return;
            }
            _duration = seconds;
            _time = 0f;
            _running = true;
            _owner?.SetLive(true);   // пока едет — снимаем каждый кадр
        }

        /// <summary>Постоянные движения. Ноль выключает своё.</summary>
        public void SetLoops(float? spin, Vector3? bob, float? bobSpeed, float? pulse, float? pulseSpeed)
        {
            if (spin is float s) _spin = s;
            if (bob is Vector3 b) _bob = b;
            if (bobSpeed is float bs && bs > 0f) _bobSpeed = bs;
            if (pulse is float p) _pulse = p;
            if (pulseSpeed is float ps && ps > 0f) _pulseSpeed = ps;
            Rebase();
            if (Alive) _owner?.SetLive(true);
        }

        private bool Alive => Mathf.Abs(_spin) > 0.001f
                              || _bob.sqrMagnitude > 0.000001f
                              || Mathf.Abs(_pulse) > 0.001f;

        /// <summary>Запомнить состояние покоя: качание идёт ВОКРУГ него, а не от
        /// текущего кадра — иначе объект уползает с каждым циклом.</summary>
        private void Rebase()
        {
            _base = transform.localPosition;
            _baseScale = transform.localScale;
        }

        private void Awake() => Rebase();

        private float CurrentDissolve()
        {
            var mr = GetComponent<MeshRenderer>();
            var mat = mr != null ? mr.sharedMaterial : null;
            return mat != null && mat.HasProperty("_Amount") ? mat.GetFloat("_Amount") : 0f;
        }

        private void Apply(float k)
        {
            transform.localPosition = Vector3.Lerp(_fromPos, _toPos, k);
            transform.localRotation = Quaternion.Slerp(_fromRot, _toRot, k);
            transform.localScale = Vector3.Lerp(_fromScale, _toScale, k);

            float d = Mathf.Lerp(_fromDissolve, _toDissolve, k);
            var mr = GetComponent<MeshRenderer>();
            var mat = mr != null ? mr.material : null;
            if (mat != null && mat.HasProperty("_Amount")) mat.SetFloat("_Amount", d);
        }

        private void LateUpdate()
        {
            bool busy = false;

            if (_running)
            {
                _time += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(_time / Mathf.Max(_duration, 0.0001f));
                // Сглаживание на концах: линейный переезд читается механическим,
                // а сцена новеллы — это кадр, а не таблица значений.
                Apply(k * k * (3f - 2f * k));
                if (k >= 1f) { _running = false; Rebase(); }
                busy = true;
            }

            if (Alive)
            {
                _clock += Time.unscaledDeltaTime;
                if (Mathf.Abs(_spin) > 0.001f)
                    transform.localRotation *= Quaternion.Euler(0f, _spin * Time.unscaledDeltaTime, 0f);
                if (_bob.sqrMagnitude > 0.000001f)
                {
                    float w = Mathf.Sin(_clock * _bobSpeed * Mathf.PI * 2f);
                    transform.localPosition = _base + _bob * w;
                }
                if (Mathf.Abs(_pulse) > 0.001f)
                {
                    float w = 1f + _pulse * Mathf.Sin(_clock * _pulseSpeed * Mathf.PI * 2f);
                    transform.localScale = _baseScale * w;
                }
                busy = true;
            }

            // Ничего не движется — отпускаем живой режим: неподвижный кадр не
            // должен снимать шестьдесят одинаковых картинок в секунду.
            if (!busy) enabled = false;
        }
    }
}
