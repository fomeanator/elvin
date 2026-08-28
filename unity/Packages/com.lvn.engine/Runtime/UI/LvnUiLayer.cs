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
        // ДВА КОРНЯ, а не один. Спор за низ экрана иначе неразрешим: боевой
        // интерфейс обязан уходить под окно реплики, полноэкранное меню —
        // лежать поверх всего, и то и другое встречается в одной главе.
        private readonly VisualElement _hud;
        private readonly VisualElement _over;
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
            public string Layer;   // hud | over — в каком корне лежит
            public string When;    // always | idle | say | choice — при какой стадии виден
            public bool Manual;    // спрятано вручную через `ui X hide` — стадия не спорит
            public LvnAppearKind Appear;   // как дерево выходит на экран
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

        public LvnUiLayer(VisualElement hudHost, VisualElement overHost,
                          Func<IReadOnlyDictionary<string, JToken>> vars,
                          Action<string> goTo,
                          Func<VisualElement, string, System.Threading.Tasks.Task> loadImage = null)
        {
            _hud = Fill("lvn-ui", hudHost);
            _over = overHost == null || ReferenceEquals(overHost, hudHost) ? _hud : Fill("lvn-ui-over", overHost);
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
            _hud.schedule.Execute(Refresh).Every(60);

            // Отклик на нажатие — тот же, что у кнопок оболочки: сквош с
            // пружиной. Без него кнопка дерева выглядит мёртвой — палец не
            // получает подтверждения, и человек жмёт второй раз.
            LvnMotion.EnableTapFeedback(_hud);
            if (!ReferenceEquals(_over, _hud)) LvnMotion.EnableTapFeedback(_over);
        }

        // Прозрачный для касаний холст во всю ширину хозяина: клик сквозь
        // пустое место должен доставаться сцене, как доставался до `ui`.
        private static VisualElement Fill(string name, VisualElement host)
        {
            var el = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            el.style.position = Position.Absolute;
            el.style.left = 0; el.style.right = 0; el.style.top = 0; el.style.bottom = 0;
            host.Add(el);
            return el;
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
                if (action == "hide") { t.Manual = true; t.Root.style.display = DisplayStyle.None; }
                else if (action == "show") { t.Manual = false; ApplyStageTo(t); }
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

            var tree = new Tree
            {
                Spec = (JObject)spec.DeepClone(),
                Layer = (string)cmd["layer"] ?? "hud",
                When = (string)cmd["when"] ?? "always",
                Appear = LvnAppear.Parse((string)cmd["appear"]),
            };
            tree.Root = BuildNode(spec, tree);

            // block: слой ловит касание мимо кнопок. Без него тап по пустому
            // месту меню листает историю за его спиной — экран отвечает не на
            // то, куда смотрит игрок.
            if (Truthy(cmd["block"]))
            {
                tree.Root.pickingMode = PickingMode.Position;
                tree.Root.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                tree.Root.RegisterCallback<PointerUpEvent>(e => e.StopPropagation());
            }

            (tree.Layer == "over" ? _over : _hud).Add(tree.Root);
            _trees[id] = tree;
            ApplyStageTo(tree);
            RefreshTree(tree, force: true);
        }

        // ── СТАДИЯ ИГРЫ ─────────────────────────────────────────────────────
        // Дерево не обязано висеть всегда. Боевой интерфейс не нужен, пока идёт
        // реплика; подсказка нужна ТОЛЬКО пока идёт реплика. Раньше автор мог
        // лишь звать `ui X hide` руками в каждой ветке — и одну неизбежно
        // забывал, отчего интерфейс оставался поверх разговора.
        private bool _sayUp, _choiceUp;
        private float _dialogueHeight;

        /// <summary>Движок сообщает, что сейчас на экране. Слой сам решает,
        /// каким деревьям быть видимыми и насколько поджаться снизу.</summary>
        public void SetStage(bool sayVisible, bool choiceVisible, float dialogueHeight)
        {
            bool changed = _sayUp != sayVisible || _choiceUp != choiceVisible
                        || !Mathf.Approximately(_dialogueHeight, dialogueHeight);
            _sayUp = sayVisible; _choiceUp = choiceVisible; _dialogueHeight = dialogueHeight;
            if (!changed) return;

            // Нижний этаж поджимается на высоту окна реплики: `at=bottom` тогда
            // означает «над диалогом», а не «под ним». Без этого автор вручную
            // подбирал отступ, и он разъезжался на первом же длинном имени.
            _hud.style.bottom = dialogueHeight;
            foreach (var kv in _trees) ApplyStageTo(kv.Value);
        }

        private void ApplyStageTo(Tree t)
        {
            if (t?.Root == null) return;
            if (t.Manual) { t.Root.style.display = DisplayStyle.None; return; }
            bool show;
            switch (t.When)
            {
                case "idle":   show = !_sayUp && !_choiceUp; break;
                case "say":    show = _sayUp; break;
                case "choice": show = _choiceUp; break;
                default:       show = true; break;
            }
            t.Root.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show) Appear(t.Root, t.Appear);
        }

        // Появление — из общего набора движка (LvnAppear), а не своя анимация
        // здесь. Если у каждого слоя своё движение, экран выглядит собранным из
        // чужих кусков.
        private static void Appear(VisualElement el, LvnAppearKind kind)
        {
            if (kind == LvnAppearKind.None) kind = LvnAppearKind.Rise;
            LvnAppear.Play(el, kind, appearing: true);
        }

        /// <summary>Убрать всё — смена главы.</summary>
        public void Clear()
        {
            foreach (var kv in _trees) kv.Value.Root.RemoveFromHierarchy();
            _trees.Clear();
        }

        private static bool Truthy(JToken t) => t != null && Truthy(t.ToString());

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
                    // Долой стандартный вид кнопки UI Toolkit: он приносит свои
                    // поля, рамку и СИНЮЮ ОБВОДКУ ФОКУСА — на телефоне она
                    // появляется после нажатия и висит, читаясь как «выделено»,
                    // хотя выделения в игре нет вовсе.
                    b.RemoveFromClassList(Button.ussClassName);
                    b.focusable = false;
                    b.style.marginLeft = 0; b.style.marginRight = 0;
                    b.style.marginTop = 0; b.style.marginBottom = 0;
                    b.style.borderLeftWidth = 0; b.style.borderRightWidth = 0;
                    b.style.borderTopWidth = 0;
                    b.style.unityTextAlign = TextAnchor.MiddleCenter;
                    var target = (string)n["on_click"];
                    if (!string.IsNullOrEmpty(target)) b.clicked += () => _goTo?.Invoke(target);
                    PressDepth(b, Num(n["radius"], 0));
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

            // Узел может выйти на экран по-своему: `appear=drop` у награды,
            // `appear=unfold` у раскрывающегося списка.
            var nodeAppear = LvnAppear.Parse((string)n["appear"]);
            if (nodeAppear != LvnAppearKind.None)
                el.schedule.Execute(() => LvnAppear.Play(el, nodeAppear, true)).StartingIn(1);

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
                float gap = Step(n["gap"], out var gapUnit);
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

        /// <summary>
        /// ГЛУБИНА НАЖАТИЯ. У кнопки есть толщина — тёмная нижняя грань; под
        /// пальцем кнопка проседает ровно на неё и светлеет.
        ///
        /// <para>Грань, а не тень: теней в UI Toolkit нет вовсе. Зато толщина
        /// читается даже лучше — палец видит, что вдавил предмет, а не что
        /// мигнул цвет.</para>
        ///
        /// <para>Подсветка — плёнка поверх, а не подмена цвета фона: у `bg`
        /// может быть живая привязка, и следующая же сверка вернула бы прежний
        /// цвет прямо под пальцем.</para>
        /// </summary>
        private static void PressDepth(VisualElement el, float radius)
        {
            float lift = LvnTokens.ButtonLift;
            var shade = LvnTokens.ButtonShade;
            if (lift > 0f)
            {
                el.style.borderBottomWidth = lift;
                el.style.borderBottomColor = shade;
                el.style.marginBottom = 0;
            }

            var veil = new VisualElement { pickingMode = PickingMode.Ignore };
            veil.style.position = Position.Absolute;
            veil.style.left = 0; veil.style.right = 0; veil.style.top = 0; veil.style.bottom = 0;
            veil.style.backgroundColor = new Color(1f, 1f, 1f, 0f);
            if (radius > 0f)
            {
                veil.style.borderTopLeftRadius = radius; veil.style.borderTopRightRadius = radius;
                veil.style.borderBottomLeftRadius = radius; veil.style.borderBottomRightRadius = radius;
            }
            el.Add(veil);

            // Просадка делается СДВИГОМ, а не отступом: отступ двигает соседей
            // по ряду, и от нажатия одной кнопки дёргается весь ряд.
            void Press()
            {
                veil.style.backgroundColor = new Color(1f, 1f, 1f, 0.13f);
                if (lift > 0f)
                {
                    el.style.borderBottomWidth = 0;
                    el.style.translate = new Translate(0, lift);
                }
            }
            void Release()
            {
                veil.style.backgroundColor = new Color(1f, 1f, 1f, 0f);
                if (lift > 0f)
                {
                    el.style.borderBottomWidth = lift;
                    el.style.translate = new Translate(0, 0);
                }
            }
            el.RegisterCallback<PointerDownEvent>(_ => Press());
            el.RegisterCallback<PointerUpEvent>(_ => Release());
            el.RegisterCallback<PointerLeaveEvent>(_ => Release());
            el.RegisterCallback<PointerCancelEvent>(_ => Release());
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

            // Живой размер ставит первая же сверка (см. ApplyLook) — здесь он
            // разобрался бы в ноль и элемент моргнул бы схлопнутым.
            if (n["w"] != null && !Live(n["w"])) { float v = Len(n["w"], out var u); SetLen(x => s.width = x, v, u); }
            if (n["h"] != null && !Live(n["h"])) { float v = Len(n["h"], out var u); SetLen(x => s.height = x, v, u); }

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
            float v = Step(t, out var u);
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

            // hide, ширина, высота и прозрачность — тоже живые. Без этого
            // «кнопка видна, только если хватает золота» пришлось бы делать
            // пересборкой всего дерева, теряя нажатие под пальцем; а поле
            // `hide` компилятор принимал и рантайм не читал вовсе — молча.
            if (n["hide"] != null) Bind(tree, el, "hide", n["hide"]);
            if (n["opacity"] != null) Bind(tree, el, "opacity", n["opacity"]);
            if (n["w"] != null && Live(n["w"])) Bind(tree, el, "w", n["w"]);
            if (n["h"] != null && Live(n["h"])) Bind(tree, el, "h", n["h"]);
            // Кегль ВСЕГДА из шкалы темы, даже когда автор его не назвал:
            // иначе текст берёт умолчание панели и выходит мелким рядом с тем,
            // что размер получил. Разнобой на одном экране заметнее, чем
            // неудачный размер.
            var kind = (string)n["kind"];
            if (n["size"] != null) s.fontSize = TextSize(n["size"]);
            else if (kind == "text" || kind == "button") s.fontSize = LvnTokens.TextBase;
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
                case "hide":
                    el.style.display = Truthy(value) ? DisplayStyle.None : DisplayStyle.Flex;
                    break;
                case "opacity":
                    el.style.opacity = Num(value, 1f);
                    break;
                case "w":
                    SetLen(v => el.style.width = v, Len(value, out var wu), wu);
                    break;
                case "h":
                    SetLen(v => el.style.height = v, Len(value, out var hu), hu);
                    break;
                case "value":
                    // Полоса: доля 0…1 в ширину заливки. Ради этого одного и
                    // затевалось — раньше это были семнадцать веток с
                    // литеральными ширинами.
                    //
                    // Заливка ЕДЕТ, а не прыгает: мгновенный скачок здоровья
                    // читается как сбой отрисовки, и глаз не успевает связать
                    // удар с потерей.
                    var fv = Lvn.LvnNum.Parse((JToken)value);
                    if (el.childCount > 0 && fv != null)
                    {
                        float f = fv.Value;
                        var fill = el[0];
                        if (fill.style.transitionProperty.keyword == StyleKeyword.Null
                            && (fill.style.transitionDuration.value == null
                                || fill.style.transitionDuration.value.Count == 0))
                        {
                            fill.style.transitionProperty = new List<StylePropertyName> { "width" };
                            fill.style.transitionDuration =
                                new List<TimeValue> { new TimeValue(0.22f, TimeUnit.Second) };
                            fill.style.transitionTimingFunction =
                                new List<EasingFunction> { new EasingFunction(EasingMode.EaseOutCubic) };
                        }
                        fill.style.width = Length.Percent(Mathf.Clamp01(f) * 100f);
                    }
                    break;
            }
        }

        // ── мелочи ──────────────────────────────────────────────────────────

        private static readonly Color Color32Clear = new Color(0, 0, 0, 0);

        private enum Unit { Px, Percent }

        // Длина: число или процент. САМ разбор — в общем доме (LvnNum), здесь
        // остаётся только выбор единицы: у стилей UI Toolkit проценты и
        // пиксели разные типы, а у координат сцены процент — просто доля.
        private static float Len(JToken t, out Unit u)
        {
            u = Unit.Px;
            if (t == null) return 0f;
            var s = t.ToString().Trim();
            if (s.EndsWith("%"))
            {
                u = Unit.Percent;
                return Lvn.LvnNum.Parse(s.Substring(0, s.Length - 1), 0f);
            }
            return Lvn.LvnNum.Parse(t, 0f);
        }

        private static void SetLen(Action<StyleLength> set, float v, Unit u)
            => set(u == Unit.Percent ? Length.Percent(v) : (Length)v);

        // Есть ли в значении живая часть. Статические размеры кладём один раз
        // в ApplyLayout — заводить на них привязку значит опрашивать зря.
        private static bool Live(JToken t) => t != null && t.ToString().Contains("{");

        private static float Len(string s, out Unit u) => Len((JToken)s, out u);

        private static float Num(string s, float def) => Num((JToken)s, def);

        // Словарь общий (Lvn.LvnBool), а вот судьба НЕПОНЯТОГО значения здесь
        // своя и намеренная: в разметке непустая строка исторически значит
        // «свойство задано», поэтому незнакомое слово — согласие, а не
        // умолчание. Это единственное осмысленное расхождение из шести.
        private static bool Truthy(string s)
            => !string.IsNullOrEmpty(s) && Lvn.LvnBool.Of(s, true);

        // Кегль по ИМЕНИ ступени, а не числом: одинаковые вещи на разных
        // экранах обязаны быть одного размера. Число тоже принимается — но
        // тогда за разнобой отвечает автор, а не тема.
        private static float TextSize(JToken t)
        {
            switch (t?.ToString())
            {
                case "xs": return LvnTokens.TextXs;
                case "sm": return LvnTokens.TextSm;
                case "base": return LvnTokens.TextBase;
                case "lg": return LvnTokens.TextLg;
                case "xl": return LvnTokens.TextXl;
                case "display": return LvnTokens.TextDisplay;
            }
            return Num(t, LvnTokens.TextBase);
        }

        // Отступ по ступени шкалы: pad=3 — это Space3 темы, а не «три пикселя».
        // Проценты и пиксели по-прежнему работают, ступень выбирается только
        // для целых 1…6 — их писать удобнее всего, и они самые частые.
        private static float Step(JToken t, out Unit unit)
        {
            unit = Unit.Px;
            var raw = t?.ToString();
            switch (raw)
            {
                case "1": return LvnTokens.Space1;
                case "2": return LvnTokens.Space2;
                case "3": return LvnTokens.Space3;
                case "4": return LvnTokens.Space4;
                case "5": return LvnTokens.Space5;
                case "6": return LvnTokens.Space6;
            }
            return Len(t, out unit);
        }

        private static float Num(JToken t, float def) => Lvn.LvnNum.Parse(t, def);

        private static Color Color(JToken t, Color def) => Color(t?.ToString(), def);

        /// <summary>Цвет из литерала или ИЗ ТОКЕНА ТЕМЫ. Токены важнее
        /// удобства: иначе игровой интерфейс останется единственным местом,
        /// живущим своей палитрой, и смена темы его не тронет.</summary>
        // Цвет — из общего дома (UiColor.Token): имена токенов темы плюс hex.
        // Своя копия здесь и была тем, из-за чего один и тот же `accent` мог
        // означать разное в разных слоях.
        private static Color Color(string s, Color def) => UiColor.Token(s, def);

        private static LvnIcon IconByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return LvnIcon.Star;
            return Enum.TryParse<LvnIcon>(name, true, out var ic) ? ic : LvnIcon.Star;
        }
    }
}
