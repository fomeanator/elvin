using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЕДИНЫЙ индикатор загрузки контента (решение Ильи 25.08): всё, что
    /// качается — «Скачать всё» из настроек, прелоад главы, фоновый стриминг —
    /// показывается одной пилюлей сверху: сколько файлов, мегабайты, скорость,
    /// тонкая полоса прогресса. Раньше прогресс жил только внутри экрана
    /// настроек: закрыл его — и загрузка «пропала», хотя батч продолжал
    /// качать (живой репорт «нажал скачать, закрыл — остановилось»).
    ///
    /// Пилюля пассивна и не ловит тапы: NovelShell кормит её снимком
    /// <c>ContentLoader.Transfers()</c> по таймеру. Появляется при активности,
    /// гаснет через паузу — чтобы не мигать между соседними файлами.
    /// </summary>
    public sealed class DownloadHud : VisualElement
    {
        private readonly Label _label;
        private readonly VisualElement _track, _fill;

        private long _lastBytes;
        private float _lastAt = -1f;
        private float _speed;          // байт/с, сглаженная
        private float _quietSince = -1f;

        public DownloadHud()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.top = 64;
            style.left = 0; style.right = 0;
            style.alignItems = Align.Center;
            style.display = DisplayStyle.None;

            var pill = new VisualElement();
            pill.pickingMode = PickingMode.Ignore;
            var bg = LvnTokens.PanelBg;
            pill.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.94f);
            LvnChrome.Edge(pill);
            LvnChrome.Round(pill, 18f);
            pill.style.paddingTop = 8; pill.style.paddingBottom = 10;
            pill.style.paddingLeft = 16; pill.style.paddingRight = 16;
            pill.style.maxWidth = Length.Percent(88f);
            Add(pill);

            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            pill.Add(row);

            var arrow = new Label("↓");
            arrow.pickingMode = PickingMode.Ignore;
            arrow.style.color = LvnTokens.Accent;
            arrow.style.fontSize = 22;
            arrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            arrow.style.marginRight = 8;
            row.Add(arrow);

            _label = new Label("");
            _label.pickingMode = PickingMode.Ignore;
            _label.style.color = LvnTokens.Text;
            _label.style.fontSize = 20;
            row.Add(_label);

            _track = new VisualElement();
            _track.pickingMode = PickingMode.Ignore;
            _track.style.height = 4;
            _track.style.marginTop = 7;
            _track.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Round(_track, 2f);
            _track.style.overflow = Overflow.Hidden;
            pill.Add(_track);

            _fill = new VisualElement();
            _fill.pickingMode = PickingMode.Ignore;
            _fill.style.height = 4;
            _fill.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.Round(_fill, 2f);
            _track.Add(_fill);
        }

        /// <summary>Скормить свежий снимок сети. Зов — по таймеру оболочки.</summary>
        public void Tick((int inflight, int batchTotal, int batchDone, long received, long expected) t)
        {
            float now = Time.realtimeSinceStartup;
            bool active = t.inflight > 0 || (t.batchTotal > 0 && t.batchDone < t.batchTotal);

            // Скорость: EMA по дельте полученных байт; сброс счётчиков батча
            // (received упал) обнуляет и скорость.
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
                style.display = DisplayStyle.Flex;
                bool batch = t.batchTotal > 0;
                string text = batch
                    ? $"Загрузка {Mathf.Min(t.batchDone + 1, t.batchTotal)}/{t.batchTotal} · {Mb(t.received)}"
                    : $"Загрузка · файлов: {t.inflight}";
                if (_speed > 8f * 1024f) text += " · " + Speed(_speed);
                _label.text = text;

                float frac = t.expected > 0 ? Mathf.Clamp01((float)t.received / t.expected)
                    : batch ? Mathf.Clamp01((float)t.batchDone / Mathf.Max(1, t.batchTotal))
                    : 0f;
                _track.style.display = frac > 0f ? DisplayStyle.Flex : DisplayStyle.None;
                _fill.style.width = Length.Percent(frac * 100f);
            }
            else if (style.display == DisplayStyle.Flex)
            {
                // Пауза между файлами не должна мигать пилюлей — гасим спустя
                // секунду настоящей тишины.
                if (_quietSince < 0f) _quietSince = now;
                if (now - _quietSince > 1.2f)
                {
                    style.display = DisplayStyle.None;
                    _speed = 0f;
                    _quietSince = -1f;
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
    }
}
