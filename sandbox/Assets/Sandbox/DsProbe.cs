using UnityEngine;
using UnityEngine.UIElements;

namespace Sandbox
{
    /// <summary>
    /// ПРОБНИК готовой системы дизайна (sinanata/unity-ui-toolkit-design-system,
    /// MIT) с нашими цветами.
    ///
    /// <para>Проверяем ровно один вопрос: можно ли получить приличный экран, не
    /// рисуя каждую панель руками. Поэтому здесь не «красивая демка», а хаб,
    /// собранный из чужих компонентов и наших токенов — то же, что в игре,
    /// только чужими руками.</para>
    ///
    /// <para>Живёт в песочнице, а не в движке: пока это опыт, а не решение.
    /// Включается переменной окружения LVN_DS_PROBE=1 или флагом в сборке —
    /// иначе грузится обычное приложение.</para>
    /// </summary>
    public sealed class DsProbe : MonoBehaviour
    {
        // ПОСЛЕ загрузки сцены, а не до: объект, созданный до неё, погибает
        // вместе с её загрузкой, и Start у него не доживает. На этом я и
        // потерял два захода — пробник «не запускался», хотя код был в сборке.
        // пробник компонентов отключён: витриной стал CyberHub
        private static void Boot()
        {
            Debug.Log("[ds-probe] запуск");
            // ПРОБНАЯ СБОРКА: пробник включён всегда.
            var go = new GameObject("DsProbe");
            DontDestroyOnLoad(go);
            go.AddComponent<DsProbe>();
        }

        private void Start()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1080, 1920);
            ps.match = 1f;      // тянемся по высоте: экран телефона вертикальный
            ps.sortingOrder = 500; // ПОВЕРХ игры: без этого пробник рисуется под ней
            // Панели, созданной в коде, обязательно нужна тема: без неё у текста
            // нет даже шрифта, и панель молча рисует пустоту. На это я и попался
            // в первый заход — пробник «не работал», а на деле был невидим.
            var theme = Resources.Load<ThemeStyleSheet>("UI/AppLoading/UnityDefaultRuntimeTheme")
                     ?? Resources.Load<ThemeStyleSheet>("UnityDefaultRuntimeTheme");
            if (theme != null) ps.themeStyleSheet = theme;
            doc.panelSettings = ps;
            Debug.Log($"[ds-probe] тема панели: {(theme != null ? theme.name : "НЕ НАЙДЕНА")}");

            var sheet = Resources.Load<StyleSheet>("UI/Styles/DesignSystem/DesignSystem");
            _romance = Resources.Load<StyleSheet>("TimeRomanceTokens");
            _cyber = Resources.Load<StyleSheet>("CyberpunkTokens");
            var ours = _romance;
            var root = doc.rootVisualElement;
            if (sheet != null) root.styleSheets.Add(sheet);
            // НАШИ токены идут ПОСЛЕ авторских: цвета переопределяются, а вся
            // геометрия и тайминги остаются выверенными автором.
            if (ours != null) root.styleSheets.Add(ours);

            root.AddToClassList("ds-root");
            root.AddToClassList("mobile"); // области касания 48 px вместо 36

            _root = root;
            Build(root);
            // Переключатель тем: две одновременно живущие темы — это и есть
            // проверка, ради которой всё затевалось. Одни и те же компоненты,
            // разные 23 значения.
            var swap = new Button(SwapTheme) { text = "Сменить тему" };
            swap.AddToClassList("ds-btn");
            swap.AddToClassList("ds-btn--tertiary");
            swap.style.marginTop = 12;
            root.Add(swap);
            Debug.Log($"[ds-probe] стиль системы: {(sheet != null ? "загружен" : "НЕ НАЙДЕН")}, " +
                      $"наши токены: {(ours != null ? "загружены" : "НЕ НАЙДЕНЫ")}");
        }

        private static void Build(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.paddingLeft = 20; root.style.paddingRight = 20;
            root.style.paddingTop = 48; root.style.paddingBottom = 20;

            root.Add(Text("Time Romance", "ds-h1"));
            root.Add(Text("Истории, которые оживают", "ds-body-2"));

            root.Add(Gap(18));
            root.Add(Text("Кнопки", "ds-h3"));
            var btns = Row();
            btns.Add(Btn("Играть", "ds-btn--primary"));
            btns.Add(Btn("Подробнее", "ds-btn--secondary"));
            btns.Add(Btn("Позже", "ds-btn--ghost"));
            root.Add(btns);

            root.Add(Gap(18));
            root.Add(Text("Метки", "ds-h3"));
            var chips = Row();
            foreach (var s in new[] { "Романс", "Новое", "18+" })
            {
                var c = new Label(s);
                c.AddToClassList("ds-chip");
                c.style.marginRight = 8;
                chips.Add(c);
            }
            root.Add(chips);

            root.Add(Gap(18));
            root.Add(Text("Карточка", "ds-h3"));
            var card = new VisualElement();
            card.AddToClassList("ds-card");
            card.Add(Text("Эпизод 1. Неминуемые изменения", "ds-h3"));
            card.Add(Text("Ты просыпаешься в чужом городе, и всё уже началось без тебя.", "ds-body-2"));
            var cardRow = Row();
            cardRow.Add(Btn("Продолжить", "ds-btn--primary"));
            card.Add(cardRow);
            root.Add(card);

            root.Add(Gap(18));
            root.Add(Text("Поле и переключатель", "ds-h3"));
            var input = new TextField { value = "Виктория" };
            input.AddToClassList("ds-input");
            root.Add(input);
            var toggle = new Toggle("Озвучка") { value = true };
            toggle.AddToClassList("ds-toggle");
            root.Add(toggle);

            root.Add(Gap(24));
            var nav = new VisualElement();
            nav.AddToClassList("ds-bottom-nav");
            foreach (var s in new[] { "Главная", "Магазин", "Гардероб", "Профиль" })
                nav.Add(Text(s, "ds-caption"));
            root.Add(nav);
        }

        private static StyleSheet _romance, _cyber;
        private static VisualElement _root;
        private static bool _isCyber;

        private static void SwapTheme()
        {
            if (_root == null) return;
            var from = _isCyber ? _cyber : _romance;
            var to = _isCyber ? _romance : _cyber;
            if (from != null) _root.styleSheets.Remove(from);
            if (to != null) _root.styleSheets.Add(to);
            _isCyber = !_isCyber;
            Debug.Log($"[ds-probe] тема: {(_isCyber ? "киберпанк" : "романс")}");
        }

        private static Label Text(string s, string cls)
        {
            var l = new Label(s);
            l.AddToClassList(cls);
            return l;
        }

        private static Button Btn(string s, string variant)
        {
            var b = new Button { text = s };
            b.AddToClassList("ds-btn");
            b.AddToClassList(variant);
            b.style.marginRight = 10;
            return b;
        }

        private static VisualElement Row()
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row;
            r.style.marginTop = 8;
            return r;
        }

        private static VisualElement Gap(int h)
        {
            var g = new VisualElement();
            g.style.height = h;
            return g;
        }
    }
}
