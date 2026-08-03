using System.Text;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Что сцена стоит прямо сейчас — числами, на экране устройства.
    ///
    /// <para>Бюджет кадра движок держит сам (масштаб буфера падает при
    /// просадке), но увидеть это можно было только в логе с компьютера. А
    /// решения о сцене принимает автор, и принимает он их на телефоне: «здесь
    /// стало тяжело» — это не ощущение, это число.</para>
    ///
    /// <para>Показывает: кадры в секунду, масштаб буфера (то есть насколько
    /// движок уже уступил резкостью), число тел и снимается ли кадр каждый
    /// кадр. Включается из скрипта — `bg3d stats=1`.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DStats : MonoBehaviour
    {
        private Lvn3DBackdrop _backdrop;
        private GUIStyle _style;
        private float _avg = 0.016f;
        private readonly StringBuilder _sb = new StringBuilder(160);

        public static void Show(Lvn3DBackdrop backdrop, bool on)
        {
            if (backdrop == null) return;
            var s = backdrop.GetComponent<Lvn3DStats>();
            if (!on)
            {
                if (s != null) s.enabled = false;
                return;
            }
            s = s ?? backdrop.gameObject.AddComponent<Lvn3DStats>();
            s._backdrop = backdrop;
            s.enabled = true;
        }

        private void Update()
        {
            // Сглаженное время кадра: мгновенное значение прыгает так, что
            // прочитать его нельзя.
            _avg = Mathf.Lerp(_avg, Time.unscaledDeltaTime, 0.1f);
        }

        private void OnGUI()
        {
            if (_backdrop == null) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(12, Screen.height / 54),
                    alignment = TextAnchor.UpperLeft,
                };
                _style.normal.textColor = Color.white;
            }

            _sb.Clear();
            _sb.Append(Mathf.RoundToInt(1f / Mathf.Max(_avg, 0.0001f))).Append(" кадр/с   ");
            _sb.Append((_avg * 1000f).ToString("0.0")).Append(" мс\n");
            _sb.Append("буфер ×").Append(_backdrop.BudgetScale.ToString("0.00"));
            _sb.Append("   тел ").Append(_backdrop.BodyCount);
            _sb.Append(_backdrop.IsLive ? "   живой кадр" : "   кадр замер");

            var text = _sb.ToString();
            var size = _style.CalcSize(new GUIContent(text));
            var box = new Rect(12, 12, size.x + 20, size.y + 12);
            // Подложка: белые цифры на светлом небе не читаются, а сцена бывает
            // какой угодно.
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 10, box.y + 6, box.width, box.height), text, _style);
        }
    }
}
