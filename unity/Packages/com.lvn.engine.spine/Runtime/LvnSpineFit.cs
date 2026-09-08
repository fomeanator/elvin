using Spine.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Spine
{
    /// <summary>
    /// Живёт на КОНТЕЙНЕРЕ спайна и делает две вещи: подгоняет фигуру под экран
    /// и проявляет её обычным фейдом. Одна железка, без гонки.
    ///
    /// Почему так, а не как раньше (WarmAlpha-пульс + гейт альфы за Fitted +
    /// Rearm — за 4 захода оно давало «через раз»):
    ///  • SkeletonGraphic.MeshScale равен 1 первые кадр-два после сборки, потом
    ///    устаканивается (~100). Подгонять при 1 — раздуть ~в 100×. Поэтому
    ///    подгонка ждёт в LateUpdate, пока MeshScale не устаканится.
    ///  • ПОКА ЖДЁМ, держим контейнер в scale=0 (нулевая площадь → фигуры не
    ///    видно), но alpha=1 (не гасим — иначе Canvas ОТСЕКАЕТ детей, меш не
    ///    рисуется и MeshScale не устаканивается никогда). scale и alpha
    ///    развязаны: невидимость даёт масштаб, а рисование — альфа.
    ///  • В ТОТ ЖЕ LateUpdate, где подгонка села, ставим верный масштаб И
    ///    alpha=0 — кадр рисуется уже невидимым, вспышки во весь экран нет.
    ///  • Дальше — обычный фейд alpha 0→1. Показывать нечему раздутому: масштаб
    ///    выставлен ровно тогда же, когда стало видно.
    ///
    /// Скрытие мгновенное (SetActive false). Повторный показ заново ждёт
    /// подгонку — меш при скрытии отброшен, MeshScale снова 1, — и это честно
    /// отрабатывает тем же путём, что и первый показ. Никакого Rearm-состояния.
    ///
    /// MUST live in its own file matching the class name: a MonoBehaviour Unity
    /// can't resolve to a MonoScript logs "referenced script (Unknown) missing".
    /// </summary>
    internal sealed class LvnSpineFit : MonoBehaviour
    {
        private const float FadeSeconds = 0.14f;
        // Страховка: если подгонка не удаётся вовсе (нет холста/границ), после
        // ~1.5 c показываем как есть — лучше не идеальный размер, чем невидимая
        // навсегда фигура.
        private const int FitWaitCap = 90;

        private RectTransform _container;
        private SkeletonGraphic _g;
        private RawImage _bg;
        private CanvasGroup _cg;
        private float _scale = 1f;
        private string _mode = "width";

        private bool _shown = true;   // Create() показывает сразу; SetVisible правит
        private bool _fitted;         // подгонка села хотя бы раз в этом показе
        private int _wait;            // кадров ждём подгонку (для страховки)

        public void Setup(SkeletonGraphic g, RawImage bg)
        {
            _g = g;
            _bg = bg;
            _container = transform as RectTransform;
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
        }

        public void Request(float scale, string mode)
        {
            // ТА ЖЕ ПОДГОНКА — НЕ ПОВТОРЯТЬ. Сцена просит подгонку на каждой
            // постановке куклы, и просьба с прежними числами схлопывала
            // подогнанную фигуру в ноль ради того же результата — второе
            // «рождение» на каждом переезде (живой репорт 08.09).
            bool same = Mathf.Approximately(_scale, scale)
                     && (string.IsNullOrEmpty(mode) || mode == _mode);
            if (same && _fitted && _shown && gameObject.activeSelf) return;
            _scale = scale;
            if (!string.IsNullOrEmpty(mode)) _mode = mode;
            Rearm(); // новый запрос — подгонять заново
        }

        // Показ/скрытие. Скрытие мгновенно; показ заново проходит подгонку.
        public void Show(bool visible)
        {
            // УЖЕ НА ЭКРАНЕ И ПОДОГНАНА — НЕ ПЕРЕДЁРГИВАТЬ. Повторный показ
            // видимой куклы (смена места, снятие примерки, реплей гардероба)
            // взводил подгонку заново: контейнер схлопывался в ноль и
            // проявлялся снова — «анимация героини проигрывается дважды»
            // (живой репорт 08.09). Проявление — для рождения и возврата из
            // скрытия; смена подгонки идёт через Request и сама сбрасывает
            // _fitted.
            if (visible && _shown && _fitted && gameObject.activeSelf) return;
            _shown = visible;
            if (!visible)
            {
                if (_cg != null) _cg.alpha = 0f;
                enabled = false;
                gameObject.SetActive(false);
                return;
            }
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            Rearm();
        }

        private void Rearm()
        {
            _fitted = false;
            _wait = 0;
            if (_container != null) _container.localScale = Vector3.zero; // невидимо, пока не подогнали
            if (_cg != null) _cg.alpha = 1f;                              // но рисуемся (не отсекаемся)
            enabled = true;
        }

        private void LateUpdate()
        {
            if (_g == null || !_shown) { enabled = false; return; }
            if (_fitted)
            {
                // Обычный фейд после того, как размер уже верный.
                if (_cg != null)
                {
                    _cg.alpha = Mathf.MoveTowards(_cg.alpha, 1f, Time.unscaledDeltaTime / FadeSeconds);
                    if (_cg.alpha >= 1f) enabled = false;
                }
                else enabled = false;
                return;
            }

            if (LvnSpineBootstrap.TryFit(_container, _g, _bg, _scale, _mode))
            {
                _fitted = true;
                if (_cg != null) _cg.alpha = 0f; // тот же кадр — рисуемся невидимо, без вспышки
                Xray("fit");
                return;
            }

            // Страховка: подгонка не удаётся — показываем как есть, чтобы фигура
            // не осталась невидимой навсегда.
            if (++_wait >= FitWaitCap)
            {
                if (_container != null && _container.localScale == Vector3.zero)
                    _container.localScale = Vector3.one * (_scale <= 0f ? 1f : _scale);
                _fitted = true;
                if (_cg != null) _cg.alpha = 0f;
                Xray("cap");
            }
        }

        // РЕНТГЕН 07.09: механика появления доказанно ни при чём (мой новый код
        // крутится, фигура всё равно битая). Печатаем ФАКТ о рендере в файл на
        // диске — persistentDataPath/lvn-spine-xray.txt, — чтобы прочитать его
        // прямо с машины, не гоняя гигантский лог в консоль. Снять после диагноза.
        // НЕ static: рентген был общим на всю сессию, поэтому первый же
        // сфитившийся скелет (сцена) writeил строку и ГЛУШИЛ замер для всех
        // остальных — карточки хаба не попадали в файл ни разу, и все строки
        // читались как «сцена в порядке».
        private bool _xrayDone;
        private void Xray(string when)
        {
            if (_xrayDone) return;
            _xrayDone = true;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("when=").Append(when)
                  .Append(" mode=").Append(_mode)
                  .Append(" containerScale=").Append(_container != null ? _container.localScale.x.ToString("F3") : "?")
                  .Append(" meshScale=").Append(_g.MeshScale.ToString("F1"));
                // ОПОЗНАНИЕ: размер холста отличает карточку хаба (666×800) от
                // сцены (во весь экран) — иначе строки неразличимы.
                var cRT = _g.canvas != null ? _g.canvas.transform as UnityEngine.RectTransform : null;
                sb.Append(" canvas=").Append(cRT != null
                    ? ((int)cRT.rect.width) + "x" + ((int)cRT.rect.height) : "?");
                var ov = _g.OverrideTexture;
                sb.Append(" override=").Append(ov != null ? ov.name : "NULL");
                if (ov is UnityEngine.Texture2D o2t)
                    sb.Append('[').Append(o2t.width).Append('x').Append(o2t.height).Append(' ').Append(o2t.format).Append(']');
                var gmat = _g.material;
                var gtex = gmat != null ? gmat.mainTexture : null;
                sb.Append(" gMat=").Append(gmat != null ? gmat.shader.name : "NULL")
                  .Append(" gTex=").Append(gtex != null ? gtex.name : "NULL");
                if (gtex is UnityEngine.Texture2D g2)
                    sb.Append('[').Append(g2.width).Append('x').Append(g2.height).Append(' ').Append(g2.format).Append(']');
                var rends = _g.GetComponentsInChildren<UnityEngine.CanvasRenderer>(true);
                sb.Append(" renderers=").Append(rends.Length);
                for (int i = 0; i < rends.Length; i++)
                {
                    var m = rends[i].GetMaterial();
                    var t = m != null ? m.mainTexture : null;
                    sb.Append(" | r").Append(i).Append(": mat=").Append(m != null ? m.shader.name : "NULL")
                      .Append(" tex=").Append(t != null ? t.name : "NULL");
                    if (t is UnityEngine.Texture2D t2)
                        sb.Append('[').Append(t2.width).Append('x').Append(t2.height).Append(' ').Append(t2.format).Append(']');
                    sb.Append(" color=").Append(rends[i].GetColor());
                }
                var line = sb.ToString();
                // В КОНСОЛЬ — БЕЗ КРИКА. Рентген печатается на каждый скелет, а
                // скелет рождается заново при каждой пересборке карточки: в
                // живом логе 08.09 предупреждения шли пачками и топили в себе
                // настоящие. Файл на диске остаётся полным — он и есть рентген.
                UnityEngine.Debug.Log("[lvn-spine-xray] " + line);
                var path = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "lvn-spine-xray.txt");
                System.IO.File.AppendAllText(path, System.DateTime.Now.ToString("HH:mm:ss") + " " + line + "\n");
            }
            catch (System.Exception e) { UnityEngine.Debug.LogWarning("[lvn-spine-xray] сорвался: " + e.Message); }
        }
    }
}
