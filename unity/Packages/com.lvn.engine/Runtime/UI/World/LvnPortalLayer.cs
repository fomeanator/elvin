using UnityEngine;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// СТВОР ПОРТАЛА — слой сцены, а не постэффект кадра.
    ///
    /// <para>Первая версия жила в полноэкранном стеке (<see cref="LvnFxStack"/>,
    /// <c>OnRenderImage</c>), и оттуда росла вся её ненадёжность. Без камеры в
    /// сцене стека нет вовсе — команда уходит в никуда молча, и снаружи это
    /// выглядит как «портал через раз». Уборка сцены сбрасывает стек, и створ
    /// гаснет посреди перехода. А лечь ПОД героиню постэффект не может в
    /// принципе: он работает с уже готовым кадром, где она нарисована.</para>
    ///
    /// <para>Здесь створ — обычная картинка на канвасе сцены со своим шейдером:
    /// рисуется всегда, стоит между фоном и актёрами (то есть за героиней), и
    /// ничья уборка эффектов его не касается. Раскрытие ведёт один параметр —
    /// 0 закрыт, 1 раскрыт на свой радиус.</para>
    /// </summary>
    public sealed class LvnPortalLayer : MonoBehaviour
    {
        private RawImage _image;
        private Material _mat;
        private RectTransform _rt;

        private float _open, _target, _speed;
        private float _radius = 0.30f;
        private Vector2 _center = new Vector2(0.5f, 0.5f);

        /// <summary>Создать слой в указанном родителе. Порядок в иерархии и
        /// определяет, что створ рисуется ЗА актёрами: он вставляется сразу
        /// после фона, а актёры добавляются после него.</summary>
        public static LvnPortalLayer Create(Transform parent, int siblingIndex)
        {
            var go = new GameObject("vn-portal", typeof(RectTransform), typeof(RawImage));
            go.transform.SetParent(parent, false);
            if (siblingIndex >= 0) go.transform.SetSiblingIndex(siblingIndex);

            var layer = go.AddComponent<LvnPortalLayer>();
            layer._rt = go.GetComponent<RectTransform>();
            layer._image = go.GetComponent<RawImage>();
            layer._image.raycastTarget = false;

            // Загрузка ИМЕННО ИЗ Resources — как у всех шейдеров движка
            // (LvnFx, LvnBlur, LvnActorComposite, LvnSpriteFx). Shader.Find
            // ищет по имени среди уже загруженного и в билде честно находит
            // только то, что кто-то потянул за собой; файл лежит здесь же, в
            // Runtime/Resources, и просить его по имени файла надёжнее.
            var shader = Resources.Load<Shader>("LvnPortalDisk")
                      ?? Shader.Find("Hidden/LvnPortalDisk");
            // ШЕЙДЕР ЛИБО РАБОТАЕТ, ЛИБО СТВОРА НЕТ. Материал с несобравшимся
            // шейдером Unity рисует ЯДОВИТО-РОЗОВЫМ, и при радиусе во весь
            // экран это заливка всей сцены (живой репорт Ильи 28.08). Пустой
            // проём честнее испорченного кадра, а причина уходит в лог.
            if (shader != null && shader.isSupported)
            {
                layer._mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                layer._image.material = layer._mat;
            }
            else
            {
                Lvn.LvnLog.Warn(shader == null
                    ? "[lvn-portal] шейдер Hidden/LvnPortalDisk не найден — створ не рисуется"
                    : "[lvn-portal] шейдер Hidden/LvnPortalDisk не собрался — створ не рисуется");
                layer._image.enabled = false;
                layer.enabled = false;
            }
            // Слой квадратный и центрируется по точке створа: круг остаётся
            // кругом на любом экране, без аспект-поправок в шейдере.
            layer._rt.anchorMin = layer._rt.anchorMax = new Vector2(0.5f, 0.5f);
            layer._rt.pivot = new Vector2(0.5f, 0.5f);
            layer.Apply();
            return layer;
        }

        /// <summary>Раскрыть/закрыть створ. <paramref name="seconds"/> = 0 —
        /// мгновенно.</summary>
        public void Set(float open, float seconds)
        {
            _target = Mathf.Clamp01(open);
            _speed = seconds > 0.001f ? 1f / seconds : 0f;
            if (_speed <= 0f) _open = _target;
            enabled = true;
            Apply();
            Lvn.LvnLog.Trace($"[lvn-portal] створ → {_target:0.00} (сейчас {_open:0.00}), "
                              + $"радиус={_radius:0.00}, центр=({_center.x:0.00},{_center.y:0.00}), dur={seconds:0.00}");
        }

        /// <summary>
        /// ЯДРО СТВОРА КАРТИНКОЙ. Процедурный вихрь дешёв и работает без
        /// единого файла, но читается «ломаными линиями»; готовый шар энергии
        /// читается тем, чем он и является. Картинка растёт вместе с
        /// раскрытием и медленно вращается — то есть ведёт себя как ядро, а не
        /// как наклейка. Null возвращает процедурный вид.
        /// </summary>
        public void SetCore(Texture core)
        {
            if (_mat == null) return;
            _mat.SetTexture("_CoreTex", core);
            _mat.SetFloat("_HasCore", core != null ? 1f : 0f);
        }

        /// <summary>Где стоит створ (доли кадра, y вниз — как в авторских
        /// координатах) и во что раскрывается (доля МЕНЬШЕЙ стороны).</summary>
        public void Place(Vector2 center, float radius, Color color)
        {
            _center = center;
            _radius = Mathf.Clamp(radius, 0.02f, 3f);
            if (_mat != null) _mat.SetColor("_Color", color);
            Apply();
        }

        /// <summary>
        /// ДЫХАНИЕ СТВОРА — медленное «вдох-выдох» в пределах десятой доли
        /// (просьба Ильи 28.08). Портал в меню стоит открытым подолгу, и
        /// неподвижный круг читается как наклейка; чуть живущий размер — как
        /// проём, за которым что-то происходит. Ходит вокруг ЗАДАННОГО
        /// раскрытия, поэтому не спорит ни с открытием, ни с закрытием.
        /// </summary>
        public const float BreathAmount = 0.10f;   // ±10% от текущего раскрытия
        public const float BreathPeriod = 7f;      // секунд на полный цикл

        // Раз в секунду спрашиваем материал, ЧЕМ он собирается рисовать.
        // Розовый прямоугольник во весь экран (живой репорт 28.08) — это
        // подставленный Unity error-шейдер, и снаружи причина неотличима от
        // «портал сломался вообще». Пусть створ говорит сам и не портит кадр:
        // пустой проём честнее ядовитой заливки.
        private float _nextAudit;

        private void AuditMaterial()
        {
            if (LvnClock.Now() < _nextAudit) return;
            _nextAudit = LvnClock.Now() + 1f;
            if (_image == null) return;
            var m = _image.material;
            bool ok = m != null && m.shader != null && m.shader.isSupported
                   && m.shader.name == "Hidden/LvnPortalDisk";
            if (ok) return;
            string what = m == null ? "материала НЕТ"
                        : m.shader == null ? "шейдер NULL"
                        : m.shader.name + (m.shader.isSupported ? "" : " (НЕ ПОДДЕРЖАН)");
            Lvn.LvnLog.Warn($"[lvn-portal] СТВОР РИСУЕТ НЕ ТЕМ: {what}, "
                             + $"раскрытие={_open:0.00}, сторона={_rt?.sizeDelta.x:0} — гашу слой");
            _image.enabled = false;
            enabled = false;
        }

        private void Update()
        {
            AuditMaterial();
            if (_speed > 0f && !Mathf.Approximately(_open, _target))
                _open = Mathf.MoveTowards(_open, _target, Time.unscaledDeltaTime * _speed);

            // ГЕОМЕТРИЯ ПЕРЕСЧИТЫВАЕТСЯ КАЖДЫЙ КАДР, пока створ виден. В первом
            // кадре у родителя ещё нет размера (rect = 0), и слой, посчитанный
            // один раз, остаётся нулевым навсегда: «в главе портал есть, а при
            // первом заходе в меню — нет». Пересчёт стоит несколько
            // присваиваний, а размер кадра всё равно меняется от поворота
            // экрана и смены безопасной зоны.
            if (_open > 0.001f || _target > 0.001f) Apply();
            else if (_image != null && _image.enabled) _image.enabled = false;
        }

        private void Apply()
        {
            if (_rt == null || _image == null) return;
            var parent = _rt.parent as RectTransform;
            float w = parent != null ? parent.rect.width : 0f;
            float h = parent != null ? parent.rect.height : 0f;
            // Родитель ещё не размечен — берём экран: нулевой кадр не должен
            // превращать створ в точку, которую потом никто не пересчитает.
            if (w <= 1f || h <= 1f) { w = Screen.width; h = Screen.height; }
            // Сторона слоя — по МЕНЬШЕЙ стороне кадра: радиус 1 означает «во всю
            // ширину телефона», и это одинаково читается на любом экране.
            float side = Mathf.Min(w, h) * 2f * _radius;
            _rt.sizeDelta = new Vector2(side, side);
            // Авторская y идёт вниз, канвасная — вверх.
            _rt.anchoredPosition = new Vector2((_center.x - 0.5f) * w, (0.5f - _center.y) * h);

            _image.enabled = _open > 0.001f || _target > 0.001f;
            if (_mat != null)
            {
                // Дышит ПОКАЗЫВАЕМОЕ раскрытие, а само _open остаётся тем, что
                // задали: иначе вдох посреди закрытия оставил бы створ приоткрытым.
                float breath = 1f + BreathAmount
                             * Mathf.Sin(LvnClock.Now() * (2f * Mathf.PI / BreathPeriod));
                _mat.SetFloat("_Open", Mathf.Clamp01(_open * breath));
            }
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }
    }
}
