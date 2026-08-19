using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ДЕРЕВО ИНТЕРФЕЙСА ИЗ СЦЕНАРИЯ — рантайм оператора <c>ui</c>.
    ///
    /// <para>Строит настоящие элементы UI Toolkit: тот же конструктор, которым
    /// написаны хаб и магазин. Не свой лэйаут-движок — флекс уже есть, и
    /// изобретать второй значит гарантированно разойтись с первым.</para>
    ///
    /// <para>РАСКЛАДКУ СЧИТАЕТ ЗДЕСЬ, а не компилятор. Высота текста после
    /// переноса, длина списка, вырез экрана, соотношение сторон — всё это
    /// известно только на живом экране. Компилятор, попытавшись посчитать
    /// заранее, упёрся бы в потолок на первом же переносе строки.</para>
    ///
    /// <para>ОБНОВЛЕНИЕ — СВЕРКОЙ, а не пересборкой. Дерево приходит целиком,
    /// и одинаковые узлы переиспользуются: иначе каждое изменение полосы
    /// здоровья сносило бы кнопки, теряя нажатие под пальцем, прокрутку и
    /// незаконченные анимации.</para>
    /// </summary>
    public sealed class LvnUiLayer
    {
        private readonly VisualElement _root;
        private readonly Func<string, JToken> _varOf;
        private readonly Func<IReadOnlyDictionary<string, JToken>> _vars;
        private readonly Action<string> _goTo;
        private readonly Func<VisualElement, string, System.Threading.Tasks.Task> _loadImage;

        // Дерево по имени: `ui бой { … }` повторно — это замена того же дерева.
        private readonly Dictionary<string, Tree> _trees = new Dictionary<string, Tree>();

        private sealed class Tree
        {
            public VisualElement Root;
            public JObject Spec;
            public readonly List<Binding> Bindings = new List<Binding>();
        }

        /// <summary>Одно живое значение: элемент, что в нём обновлять и по
        /// какому выражению.</summary>
        private sealed class Binding
        {
            public VisualElement El;
            public string Field;   // text | value | color | bg | w | h | …
            public string Expr;    // исходная строка с {…}
            public string Last;    // что показано сейчас — чтобы не трогать зря
        }

        public LvnUiLayer(VisualElement host,
                          Func<IReadOnlyDictionary<string, JToken>> vars,
                          Action<string> goTo,
                          Func<VisualElement, string, System.Threading.Tasks.Task> loadImage = null)
        {
            _root = new VisualElement { name = "lvn-ui", pickingMode = PickingMode.Ignore };
            _root.style.position = Position.Absolute;
            _root.style.left = 0; _root.style.right = 0; _root.style.top = 0; _root.style.bottom = 0;
            host.Add(_root);
            _vars = vars;
            _varOf = null;
            _goTo = goTo;
            _loadImage = loadImage;

            // ОБНОВЛЕНИЕ ОПРОСОМ, а не по сигналу об изменении переменной.
            // Сигнала в движке нет, а добавлять его означало бы менять
            // интерфейс сцены и каждое место записи переменной. Шестьдесят
            // миллисекунд — быстрее, чем глаз замечает у полосы с четвертью
            // секунды анимации, и дешевле: пересчитываются только привязки,
            // и только изменившиеся доходят до стиля.
            _root.schedule.Execute(Refresh).Every(60);
        }

        /// <summary>Показать/заменить дерево.</summary>
        public void Apply(JObject cmd)
        {
            string id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;

            string action = (string)cmd["action"];
            if (!string.IsNullOrEmpty(action))
            {
                if (!_trees.TryGetValue(id, out var t)) return;
                if (action == "hide") t.Root.style.display = DisplayStyle.None;
                else if (action == "show") t.Root.style.display = DisplayStyle.Flex;
                else if (action == "drop") { t.Root.RemoveFromHierarchy(); _trees.Remove(id); }
                return;
            }

            var spec = cmd["tree"] as JObject;
            if (spec == null) return;

            if (_trees.TryGetValue(id, out var old))
            {
                // То же самое дерево — не трогаем ничего: сценарий часто
                // объявляет экран заново на каждом шаге, и пересборка на
                // каждый шаг съедала бы нажатие под пальцем.
                if (JToken.DeepEquals(old.Spec, spec)) return;
                old.Root.RemoveFromHierarchy();
                _trees.Remove(id);
            }

            var tree = new Tree { Spec = (JObject)spec.DeepClone() };
            tree.Root = BuildNode(spec, tree);
            _root.Add(tree.Root);
            _trees[id] = tree;
            RefreshTree(tree, force: true);
        }

        /// <summary>Убрать всё — смена главы.</summary>
        public void Clear()
        {
            foreach (var kv in _trees) kv.Value.Root.RemoveFromHierarchy();
            _trees.Clear();
        }

        // ── построение ──────────────────────────────────────────────────────

        private VisualElement BuildNode(JObject n, Tree tree)
        {
            string kind = (string)n["kind"] ?? "panel";
            VisualElement el;
            switch (kind)
            {
                case "text":
                    el = new Label();
                    break;
                case "button":
                    // Кнопка — настоящая кнопка оболочки: она уже умеет отклик
                    // на нажатие (сквош и пружина), потому что отклик висит на
                    // корне и узнаёт Button по типу.
                    var b = new Button();
                    var target = (string)n["on_click"];
                    if (!string.IsNullOrEmpty(target)) b.clicked += () => _goTo?.Invoke(target);
                    el = b;
                    break;
                case "bar":
                    el = BuildBar();
                    break;
                case "icon":
                    el = LvnIcons.Make(IconByName((string)n["name"]), 24f,
                                       Color(n["color"], LvnTokens.Text), 0f, LvnTheme.Current.IconGlow);
                    break;
                case "image":
                    el = new VisualElement();
                    el.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                    el.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                    el.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                    el.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    var url = (string)n["url"];
                    if (!string.IsNullOrEmpty(url) && _loadImage != null) _ = _loadImage(el, url);
                    break;
                case "scroll":
                    var sv = new ScrollView(ScrollViewMode.Vertical);
                    sv.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                    el = sv;
                    break;
                default: // panel, row, column
                    el = new VisualElement();
                    break;
            }
            el.name = (string)n["id"] ?? kind;

            ApplyLayout(el, n);
            ApplyLook(el, n, tree);

            // Дети кладутся в contentContainer: у ScrollView он не сам элемент.
            var host = el.contentContainer ?? el;
            if (n["children"] is JArray kids)
            {
                var list = new List<JObject>();
                foreach (var k in kids) if (k is JObject o) list.Add(o);
                // z — порядок наложения. UI Toolkit его не знает, зато знает
                // порядок детей: сортируем сами, устойчиво.
                list.Sort((a, c) => Num(a["z"], 0).CompareTo(Num(c["z"], 0)));
                float gap = Len(n["gap"], out var gapUnit);
                for (int i = 0; i < list.Count; i++)
                {
                    var child = BuildNode(list[i], tree);
                    // Зазора между детьми в UI Toolkit нет — раскладываем
                    // отступом у всех, кроме последнего.
                    if (gap > 0f && i < list.Count - 1)
                    {
                        bool row = (string)n["dir"] == "row";
                        if (row) SetLen(v => child.style.marginRight = v, gap, gapUnit);
                        else SetLen(v => child.style.marginBottom = v, gap, gapUnit);
                    }
                    host.Add(child);
                }
            }
            return el;
        }

        private static VisualElement BuildBar()
        {
            var wrap = new VisualElement { name = "bar" };
            wrap.style.overflow = Overflow.Hidden;
            var fill = new VisualElement { name = "fill", pickingMode = PickingMode.Ignore };
            fill.style.height = Length.Percent(100f);
            fill.style.width = Length.Percent(0f);
            wrap.Add(fill);
            return wrap;
        }

        // ── раскладка ───────────────────────────────────────────────────────

        private static void ApplyLayout(VisualElement el, JObject n)
        {
            var s = el.style;
            s.flexDirection = (string)n["dir"] == "row" ? FlexDirection.Row : FlexDirection.Column;

            switch ((string)n["justify"])
            {
                case "center": s.justifyContent = Justify.Center; break;
                case "end": s.justifyContent = Justify.FlexEnd; break;
                case "between": s.justifyContent = Justify.SpaceBetween; break;
                case "around": s.justifyContent = Justify.SpaceAround; break;
                case "start": s.justifyContent = Justify.FlexStart; break;
            }
            switch ((string)n["align"])
            {
                case "center": s.alignItems = Align.Center; break;
                case "end": s.alignItems = Align.FlexEnd; break;
                case "stretch": s.alignItems = Align.Stretch; break;
                case "start": s.alignItems = Align.FlexStart; break;
            }

            if (n["grow"] != null) s.flexGrow = Num(n["grow"], 0);
            if (n["shrink"] != null) s.flexShrink = Num(n["shrink"], 1);
            if (n["basis"] != null) { float v = Len(n["basis"], out var u); SetLen(x => s.flexBasis = x, v, u); }

            if (n["w"] != null) { float v = Len(n["w"], out var u); SetLen(x => s.width = x, v, u); }
            if (n["h"] != null) { float v = Len(n["h"], out var u); SetLen(x => s.height = x, v, u); }

            ApplyPad(n["pad"], v => { s.paddingLeft = v; s.paddingRight = v; s.paddingTop = v; s.paddingBottom = v; });
            ApplyPad(n["pad_x"], v => { s.paddingLeft = v; s.paddingRight = v; });
            ApplyPad(n["pad_y"], v => { s.paddingTop = v; s.paddingBottom = v; });

            // `at` — абсолютная привязка к родителю. Это те самые «якорные
            // группы»: девяносто процентов игрового интерфейса — это «прижать
            // к низу», «растянуть на весь экран», и ради них не нужен весь
            // флекс.
            switch ((string)n["at"])
            {
                case "fill":
                    s.position = Position.Absolute; s.left = 0; s.right = 0; s.top = 0; s.bottom = 0; break;
                case "top":
                    s.position = Position.Absolute; s.left = 0; s.right = 0; s.top = 0; break;
                case "bottom":
                    s.position = Position.Absolute; s.left = 0; s.right = 0; s.bottom = 0; break;
                case "left":
                    s.position = Position.Absolute; s.left = 0; s.top = 0; s.bottom = 0; break;
                case "right":
                    s.position = Position.Absolute; s.right = 0; s.top = 0; s.bottom = 0; break;
                case "center":
                    s.position = Position.Absolute;
                    s.left = Length.Percent(50f); s.top = Length.Percent(50f);
                    s.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
                    break;
            }
        }

        private static void ApplyPad(JToken t, Action<Length> set)
        {
            if (t == null) return;
            float v = Len(t, out var u);
            if (u == Unit.Percent) set(Length.Percent(v)); else set(v);
        }

        // ── вид ─────────────────────────────────────────────────────────────

        private void ApplyLook(VisualElement el, JObject n, Tree tree)
        {
            var s = el.style;
            if (n["bg"] != null) Bind(tree, el, "bg", n["bg"]);
            if (n["color"] != null) Bind(tree, el, "color", n["color"]);
            if (n["text"] != null) Bind(tree, el, "text", n["text"]);
            if (n["value"] != null) Bind(tree, el, "value", n["value"]);

            if (n["radius"] != null)
            {
                float r = Num(n["radius"], 0);
                s.borderTopLeftRadius = r; s.borderTopRightRadius = r;
                s.borderBottomLeftRadius = r; s.borderBottomRightRadius = r;
            }
            if (n["edge"] != null)
            {
                float w = Num(n["edge"], 0);
                if (w > 0f)
                {
                    s.borderTopWidth = w; s.borderBottomWidth = w;
                    s.borderLeftWidth = w; s.borderRightWidth = w;
                    var c = LvnTheme.Current.EdgeColor;
                    s.borderTopColor = c; s.borderBottomColor = c;
                    s.borderLeftColor = c; s.borderRightColor = c;
                }
            }
            else if (LvnTheme.Current.EdgeWidth > 0f && (string)n["kind"] == "panel" && n["bg"] != null)
            {
                LvnChrome.Edge(el);   // тема сама решает, быть ли кромке
            }

            if (n["opacity"] != null) s.opacity = Num(n["opacity"], 1f);
            if (n["size"] != null) s.fontSize = Num(n["size"], 20);
            if ((string)n["weight"] == "bold") s.unityFontStyleAndWeight = FontStyle.Bold;
        }

        // ── живые значения ──────────────────────────────────────────────────

        /// <summary>Кладёт значение сразу и, если в нём есть {…}, заводит
        /// привязку — тогда оно будет пересчитываться само.</summary>
        private void Bind(Tree tree, VisualElement el, string field, JToken raw)
        {
            string str = raw.Type == JTokenType.String ? (string)raw : raw.ToString();
            if (str != null && str.Contains("{"))
                tree.Bindings.Add(new Binding { El = el, Field = field, Expr = str });
            SetField(el, field, str);
        }

        private void Refresh()
        {
            foreach (var kv in _trees) RefreshTree(kv.Value, force: false);
        }

        private void RefreshTree(Tree tree, bool force)
        {
            if (tree.Bindings.Count == 0) return;
            var vars = _vars?.Invoke();
            if (vars == null) return;
            foreach (var b in tree.Bindings)
            {
                string now;
                try { now = TextInterpolation.Apply(b.Expr, vars); }
                catch { continue; }   // сломанное выражение не должно ронять экран
                if (!force && now == b.Last) continue;   // не трогаем зря
                b.Last = now;
                SetField(b.El, b.Field, now);
            }
        }

        private static void SetField(VisualElement el, string field, string value)
        {
            if (el == null || value == null) return;
            switch (field)
            {
                case "text":
                    if (el is Label l) l.text = value;
                    else if (el is Button bt) bt.text = value;
                    break;
                case "color":
                    var c = Color(value, LvnTokens.Text);
                    el.style.color = c;
                    if (el.name == "bar" && el.childCount > 0) el[0].style.backgroundColor = c;
                    break;
                case "bg":
                    el.style.backgroundColor = Color(value, Color32Clear);
                    break;
                case "value":
                    // Полоса: доля 0…1 в ширину заливки. Ради этого одного и
                    // затевалось — раньше это были семнадцать веток с
                    // литеральными ширинами.
                    if (el.childCount > 0 && float.TryParse(value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var f))
                        el[0].style.width = Length.Percent(Mathf.Clamp01(f) * 100f);
                    break;
            }
        }

        // ── мелочи ──────────────────────────────────────────────────────────

        private static readonly Color Color32Clear = new Color(0, 0, 0, 0);

        private enum Unit { Px, Percent }

        private static float Len(JToken t, out Unit u)
        {
            u = Unit.Px;
            if (t == null) return 0f;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<float>();
            var s = t.ToString().Trim();
            if (s.EndsWith("%"))
            {
                u = Unit.Percent;
                float.TryParse(s.Substring(0, s.Length - 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var p);
                return p;
            }
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v);
            return v;
        }

        private static void SetLen(Action<StyleLength> set, float v, Unit u)
            => set(u == Unit.Percent ? Length.Percent(v) : (Length)v);

        private static float Num(JToken t, float def)
        {
            if (t == null) return def;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<float>();
            return float.TryParse(t.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
        }

        private static Color Color(JToken t, Color def) => Color(t?.ToString(), def);

        /// <summary>Цвет из литерала или ИЗ ТОКЕНА ТЕМЫ. Токены важнее
        /// удобства: иначе игровой интерфейс останется единственным местом,
        /// живущим своей палитрой, и смена темы его не тронет.</summary>
        private static Color Color(string s, Color def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            switch (s)
            {
                case "bg": return LvnTokens.Bg;
                case "surface": return LvnTokens.Surface;
                case "surface_hi": return LvnTokens.SurfaceHi;
                case "panel": return LvnTokens.PanelBg;
                case "text": return LvnTokens.Text;
                case "dim": return LvnTokens.TextDim;
                case "accent": return LvnTokens.Accent;
                case "on_accent": return LvnTokens.OnAccent;
                case "gold": return LvnTokens.Gold;
                case "warn": return LvnTheme.Current.Warn;
                case "border": return LvnTokens.Border;
                case "clear": return Color32Clear;
            }
            return UnityEngine.ColorUtility.TryParseHtmlString(s, out var c) ? c : def;
        }

        private static LvnIcon IconByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return LvnIcon.Star;
            return Enum.TryParse<LvnIcon>(name, true, out var ic) ? ic : LvnIcon.Star;
        }
    }
}
