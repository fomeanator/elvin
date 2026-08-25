using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЕДИНЫЙ индикатор загрузки контента, двух видов (решение Ильи 25.08):
    /// МИНИ — хромовский кружок: стрелка с полочкой и кольцо прогресса вокруг
    /// (painter2D, без единого ассета); тап ПЕРЕТЕКАЕТ капсулу в ПОЛНУЮ
    /// карточку — имя файла, текущая скорость, очередь и скачанный объём.
    /// Морф — одна анимация ширины/высоты/радиуса с кроссфейдом содержимого:
    /// UITK анимирует style-свойства по тикам, поэтому «перетекание» здесь
    /// такое же родное, как в вебе FLIP.
    ///
    /// Сюда стекается ВСЯ сеть («Скачать всё», прелоад главы, стриминг) через
    /// ContentLoader.Transfers() — раньше прогресс жил только в настройках, и
    /// «закрыл настройки — загрузка пропала» (живой репорт).
    /// </summary>
    public sealed class DownloadHud : VisualElement
    {
        // Геометрия двух состояний капсулы.
        private const float MiniSize = 46f;
        private const float FullW = 460f;
        private const float FullH = 118f;

        private readonly VisualElement _capsule;
        private readonly ProgressRing _miniRing;
        private readonly VisualElement _full;
        private readonly ProgressRing _fullRing;
        private readonly Label _file, _stats;

        private bool _expanded;
        private float _morph;          // 0 = мини, 1 = полная (текущее положение)
        private long _lastBytes;
        private float _lastAt = -1f;
        private float _speed;          // байт/с, EMA
        private float _quietSince = -1f;
        private bool _visible;

        public DownloadHud()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.top = 64;
            style.left = 0; style.right = 0;
            style.alignItems = Align.Center;
            style.display = DisplayStyle.None;

            _capsule = new VisualElement();
            var bg = LvnTokens.PanelBg;
            _capsule.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.95f);
            LvnChrome.Edge(_capsule);
            _capsule.style.overflow = Overflow.Hidden;
            _capsule.style.alignItems = Align.Center;
            _capsule.style.justifyContent = Justify.Center;
            _capsule.RegisterCallback<ClickEvent>(_ => SetExpanded(!_expanded));
            _capsule.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            Add(_capsule);

            _miniRing = new ProgressRing(MiniSize * 0.5f - 5f, 3.5f, drawArrow: true);
            _miniRing.style.width = MiniSize; _miniRing.style.height = MiniSize;
            _miniRing.pickingMode = PickingMode.Ignore;
            _capsule.Add(_miniRing);

            // Полное содержимое живёт всегда и кроссфейдится морфом.
            _full = new VisualElement();
            _full.pickingMode = PickingMode.Ignore;
            _full.style.position = Position.Absolute;
            _full.style.left = 16; _full.style.right = 16;
            _full.style.top = 0; _full.style.bottom = 0;
            _full.style.flexDirection = FlexDirection.Row;
            _full.style.alignItems = Align.Center;
            _full.style.opacity = 0f;
            _capsule.Add(_full);

            _fullRing = new ProgressRing(26f, 4.5f, drawArrow: true);
            _fullRing.style.width = 64; _fullRing.style.height = 64;
            _fullRing.style.marginRight = 14;
            _fullRing.style.flexShrink = 0;
            _fullRing.pickingMode = PickingMode.Ignore;
            _full.Add(_fullRing);

            var col = new VisualElement();
            col.pickingMode = PickingMode.Ignore;
            col.style.flexGrow = 1; col.style.flexShrink = 1;
            _full.Add(col);

            _file = new Label("");
            _file.pickingMode = PickingMode.Ignore;
            _file.style.color = LvnTokens.Text;
            _file.style.fontSize = 22;
            _file.style.unityFontStyleAndWeight = FontStyle.Bold;
            _file.style.overflow = Overflow.Hidden;
            _file.style.textOverflow = TextOverflow.Ellipsis;
            _file.style.whiteSpace = WhiteSpace.NoWrap;
            col.Add(_file);

            _stats = new Label("");
            _stats.pickingMode = PickingMode.Ignore;
            _stats.style.color = LvnTokens.TextDim;
            _stats.style.fontSize = 19;
            _stats.style.marginTop = 4;
            col.Add(_stats);

            ApplyMorph(0f);
        }

        // ── морф мини ↔ полная ────────────────────────────────────────────────

        private void SetExpanded(bool on)
        {
            if (_expanded == on) return;
            _expanded = on;
            float from = _morph, to = on ? 1f : 0f;
            _capsule.experimental.animation.Start(0f, 1f, 260, (_, p) =>
            {
                float e = 1f - Mathf.Pow(1f - p, 3f); // OutCubic — тормозит у цели
                ApplyMorph(Mathf.Lerp(from, to, e));
            });
        }

        private void ApplyMorph(float k)
        {
            _morph = k;
            _capsule.style.width = Mathf.Lerp(MiniSize, FullW, k);
            _capsule.style.height = Mathf.Lerp(MiniSize, FullH, k);
            LvnChrome.Round(_capsule, Mathf.Lerp(MiniSize * 0.5f, 22f, k));
            // Кроссфейд содержимого: мини-кольцо гаснет в первой трети морфа,
            // полная карточка проявляется во второй — в середине капсула
            // «пустая», и перетекание читается формой, а не мешаниной слоёв.
            _miniRing.style.opacity = Mathf.Clamp01(1f - k * 3f);
            _full.style.opacity = Mathf.Clamp01((k - 0.55f) / 0.45f);
        }

        // ── данные ────────────────────────────────────────────────────────────

        /// <summary>Скормить свежий снимок сети (таймер оболочки, ~300 мс).</summary>
        public void Tick((int inflight, int batchTotal, int batchDone, long received, long expected, string label) t)
        {
            float now = Time.realtimeSinceStartup;
            bool active = t.inflight > 0 || (t.batchTotal > 0 && t.batchDone < t.batchTotal);

            if (_lastAt > 0f && t.received >= _lastBytes)
            {
                float dt = now - _lastAt;
                if (dt > 0.05f)
                {
                    float inst = (t.received - _lastBytes) / dt;
                    _speed = _speed <= 0f ? inst : Mathf.Lerp(_speed, inst, 0.35f);
                }
            }
            else if (t.received < _lastBytes) _speed = 0f;
            _lastBytes = t.received;
            _lastAt = now;

            if (active)
            {
                _quietSince = -1f;
                if (!_visible)
                {
                    _visible = true;
                    style.display = DisplayStyle.Flex;
                    _capsule.style.opacity = 0f;
                    _capsule.experimental.animation.Start(0f, 1f, 180,
                        (_, p) => _capsule.style.opacity = p);
                }

                float frac = t.expected > 0 ? Mathf.Clamp01((float)t.received / t.expected)
                    : t.batchTotal > 0 ? Mathf.Clamp01((float)t.batchDone / Mathf.Max(1, t.batchTotal))
                    : -1f; // неизвестен — кольцо крутится само
                _miniRing.Progress = frac;
                _fullRing.Progress = frac;

                _file.text = string.IsNullOrEmpty(t.label) ? "Загрузка контента" : t.label;
                int queued = t.batchTotal > 0 ? Mathf.Max(0, t.batchTotal - t.batchDone) : t.inflight;
                string s = _speed > 8f * 1024f ? Speed(_speed) : "…";
                string got = Mb(t.received) + (t.expected > 0 ? " из " + Mb(t.expected) : "");
                _stats.text = $"{s} · в очереди {queued} · {got}";
            }
            else if (_visible)
            {
                if (_quietSince < 0f) _quietSince = now;
                if (now - _quietSince > 1.2f)
                {
                    _visible = false;
                    _quietSince = -1f;
                    _speed = 0f;
                    if (_expanded) SetExpanded(false);
                    _capsule.experimental.animation.Start(1f, 0f, 200, (_, p) =>
                    {
                        _capsule.style.opacity = p;
                        if (p <= 0.01f) style.display = DisplayStyle.None;
                    });
                }
            }
        }

        private static string Mb(long bytes)
            => bytes >= 100L << 20 ? $"{bytes >> 20} МБ"
             : $"{bytes / 1048576f:0.#} МБ".Replace('.', ',');

        private static string Speed(float bytesPerSec)
            => bytesPerSec >= 1048576f
                ? $"{bytesPerSec / 1048576f:0.#} МБ/с".Replace('.', ',')
                : $"{Mathf.RoundToInt(bytesPerSec / 1024f)} КБ/с";

        /// <summary>Хромовский значок загрузки чистым painter2D: стрелка вниз с
        /// полочкой и кольцо прогресса вокруг. Progress &lt; 0 — прогресс
        /// неизвестен: короткая дуга крутится сама (спиннер).</summary>
        private sealed class ProgressRing : VisualElement
        {
            private readonly float _radius, _stroke;
            private readonly bool _arrow;
            private float _progress = -1f;
            private float _spin;

            public float Progress
            {
                get => _progress;
                set { _progress = value; MarkDirtyRepaint(); }
            }

            public ProgressRing(float radius, float stroke, bool drawArrow)
            {
                _radius = radius; _stroke = stroke; _arrow = drawArrow;
                generateVisualContent += Draw;
                // Спиннеру нужен ход времени; при известном прогрессе тик
                // просто перерисовывает свежую дугу.
                schedule.Execute(() =>
                {
                    _spin = (_spin + 5f) % 360f;
                    MarkDirtyRepaint();
                }).Every(33);
            }

            private void Draw(MeshGenerationContext mgc)
            {
                var p = mgc.painter2D;
                var c = new Vector2(resolvedStyle.width * 0.5f, resolvedStyle.height * 0.5f);

                // Фоновое кольцо.
                p.lineWidth = _stroke;
                p.strokeColor = LvnTokens.Faint;
                p.BeginPath();
                p.Arc(c, _radius, 0f, 360f);
                p.Stroke();

                // Дуга прогресса — от «12 часов» по часовой; спиннер — бегущая
                // четверть круга.
                p.strokeColor = LvnTokens.Accent;
                p.lineCap = LineCap.Round;
                p.BeginPath();
                if (_progress >= 0f)
                    p.Arc(c, _radius, -90f, -90f + 360f * Mathf.Clamp01(_progress));
                else
                    p.Arc(c, _radius, _spin, _spin + 90f);
                p.Stroke();

                if (!_arrow) return;
                // Стрелка: штрих вниз + шеврон + полочка (как у Chrome).
                float a = _radius * 0.52f;
                p.strokeColor = LvnTokens.Text;
                p.lineWidth = Mathf.Max(2f, _stroke * 0.8f);
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x, c.y - a));
                p.LineTo(new Vector2(c.x, c.y + a * 0.55f));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - a * 0.6f, c.y - a * 0.05f));
                p.LineTo(new Vector2(c.x, c.y + a * 0.62f));
                p.LineTo(new Vector2(c.x + a * 0.6f, c.y - a * 0.05f));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - a * 0.7f, c.y + a));
                p.LineTo(new Vector2(c.x + a * 0.7f, c.y + a));
                p.Stroke();
            }
        }
    }
}
