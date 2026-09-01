using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using static Lvn.UI.LvnUiValues;

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
        private readonly Func<IReadOnlyDictionary<string, JToken>> _vars;
        private readonly Action<string> _goTo;
        private readonly Action<JObject> _setVars;
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
            public bool Block;     // ловит ли слой касание мимо кнопок
            public bool Shown;     // стоит ли дерево на экране СЕЙЧАС — чтобы не играть вход дважды
            public LvnAppearKind Appear;   // как дерево выходит на экран
            public readonly LvnUiLive Live = new LvnUiLive();   // живые значения этого дерева
        }

        /// <param name="setVars">записать переменные из объектной формы
        /// <c>on_click</c>. Необязателен: без него нажатие отработает одним
        /// переходом — так собирают слой стенды, где переменные не пишут.</param>
        public LvnUiLayer(VisualElement hudHost, VisualElement overHost,
                          Func<IReadOnlyDictionary<string, JToken>> vars,
                          Action<string> goTo,
                          Func<VisualElement, string, System.Threading.Tasks.Task> loadImage = null,
                          Action<JObject> setVars = null)
        {
            _hud = Fill("lvn-ui", hudHost);
            _over = overHost == null || ReferenceEquals(overHost, hudHost) ? _hud : Fill("lvn-ui-over", overHost);
            _vars = vars;
            _goTo = goTo;
            _loadImage = loadImage;
            _setVars = setVars;

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
            LvnChrome.Stretch(el);
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
                // ПРЯЧЕМ ТЕМ ЖЕ ПУТЁМ, ЧТО И ПОКАЗЫВАЕМ. Здесь дерево
                // убиралось напрямую, мимо общего применения стадии, и
                // признак «стоит на экране» оставался поднятым: вернувшееся
                // дерево считалось никуда не уходившим и выходило БЕЗ входа —
                // возникало на месте вместо того, чтобы проступить.
                if (action == "hide") { t.Manual = true; ApplyStageTo(t); }
                else if (action == "show") { t.Manual = false; ApplyStageTo(t); }
                else if (action == "drop") { t.Root.RemoveFromHierarchy(); _trees.Remove(id); }
                return;
            }

            var spec = cmd["tree"] as JObject;
            if (spec == null) return;

            string layer = (string)cmd["layer"] ?? "hud";
            string when = (string)cmd["when"] ?? "always";
            var appear = LvnAppear.Parse((string)cmd["appear"]);
            bool block = Truthy(cmd["block"]);

            if (_trees.TryGetValue(id, out var old))
            {
                // То же самое дерево — не трогаем ничего: сценарий часто
                // объявляет экран заново на каждом шаге, и пересборка на
                // каждый шаг съедала бы нажатие под пальцем.
                //
                // «То же самое» — это ВСЯ команда, а не одно её дерево.
                // Сравнивали только дерево, и `ui бой when=say { …то же… }`
                // после `when=always` не менял ничего: язык обещает заменить
                // объявление целиком, а рантайм молча оставлял прежнее.
                bool sameLook = JToken.DeepEquals(old.Spec, spec)
                                && old.Layer == layer && old.When == when
                                && old.Appear == appear && old.Block == block;
                if (sameLook)
                {
                    // ПОВТОРНОЕ ОБЪЯВЛЕНИЕ ВОЗВРАЩАЕТ СПРЯТАННОЕ РУКОЙ. Раньше
                    // оно уходило в этот же ранний выход, и дерево, убранное
                    // через `ui X hide`, так и лежало невидимым — а автор
                    // объявил его снова и ждёт на экране.
                    if (old.Manual) { old.Manual = false; ApplyStageTo(old); }
                    return;
                }
                old.Root.RemoveFromHierarchy();
                _trees.Remove(id);
            }

            var tree = new Tree
            {
                Spec = (JObject)spec.DeepClone(),
                Layer = layer,
                When = when,
                Appear = appear,
                Block = block,
            };
            tree.Root = BuildNode(spec, tree);

            // block: слой ловит касание мимо кнопок. Без него тап по пустому
            // месту меню листает историю за его спиной — экран отвечает не на
            // то, куда смотрит игрок.
            if (block)
            {
                tree.Root.pickingMode = PickingMode.Position;
                tree.Root.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                tree.Root.RegisterCallback<PointerUpEvent>(e => e.StopPropagation());
            }

            (tree.Layer == "over" ? _over : _hud).Add(tree.Root);
            _trees[id] = tree;
            ApplyStageTo(tree);
            var freshVars = _vars?.Invoke();
            if (freshVars != null) tree.Live.Refresh(freshVars, force: true);
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
            // Спрятанное рукой автора считается ушедшим: вернётся — сыграет вход.
            if (t.Manual) { t.Root.style.display = DisplayStyle.None; t.Shown = false; return; }
            bool show;
            switch (t.When)
            {
                case "idle":   show = !_sayUp && !_choiceUp; break;
                case "say":    show = _sayUp; break;
                case "choice": show = _choiceUp; break;
                default:       show = true; break;
            }
            t.Root.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            // ВХОД ИГРАЕТСЯ НА ПОЯВЛЕНИИ, А НЕ НА КАЖДОЙ СМЕНЕ СТАДИИ.
            //
            // Стадия меняется дважды за реплику — текст допечатался, игрок
            // тапнул, — и оба раза сюда приходили ВСЕ деревья, включая те, что
            // никуда не пропадали: дерево с `when=always` (умолчание, то есть
            // весь постоянный HUD — полосы, счётчики, трекер) обнулялось в
            // прозрачность и всплывало заново. Со стороны это ровная пульсация
            // интерфейса в любой сцене с диалогом.
            if (show && !t.Shown) Appear(t.Root, t.Appear);
            t.Shown = show;
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
                    LvnAir.Margin(b, 0);
                    // НАРОЧНО три стороны, а не ClearBorder: НИЖНЯЯ кромка
                    // здесь не рамка, а объём кнопки (ButtonLift) — погасив её
                    // заодно, кнопка становится плоской.
                    b.style.borderLeftWidth = 0; b.style.borderRightWidth = 0;
                    b.style.borderTopWidth = 0;
                    b.style.unityTextAlign = TextAnchor.MiddleCenter;
                    // ЧТО ЗНАЧИТ НАЖАТИЕ — решает общий дом (LvnClick), тот же,
                    // что у фигуры на сцене. Здесь поле приводили к строке
                    // напрямую, и объектная форма `{goto, set}` — законная в
                    // сцене и описанная в документации — БРОСАЛА: дерево не
                    // строилось, и у игрока пропадал весь интерфейс.
                    var onClick = LvnClick.From(n["on_click"], l => _goTo?.Invoke(l), _setVars);
                    if (onClick != null) b.clicked += onClick;
                    PressDepth(b, Num(n["radius"], 0));
                    el = b;
                    break;
                case "bar":
                    el = BuildBar();
                    break;
                case "icon":
                    el = LvnIcons.Make(IconByName((string)n["name"]), 24f,
                                       Color(n["color"], LvnTokens.Text));
                    break;
                case "image":
                    el = new VisualElement();
                    LvnPicture.Fit(el);
                    var url = (string)n["url"];
                    if (!string.IsNullOrEmpty(url) && _loadImage != null) _ = _loadImage(el, url);
                    break;
                case "scroll":
                    // Список автора — по общим правилам: он тоже тянется рукой,
                    // а не только колесом (см. LvnScroll).
                    el = LvnScroll.Vertical();
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

            bool center = ApplyLayout(el, n);
            ApplyLook(el, n, tree);

            // Дети кладутся в contentContainer: у ScrollView он не сам элемент.
            var host = el.contentContainer ?? el;
            if (n["children"] is JArray kids)
            {
                var list = new List<JObject>();
                foreach (var k in kids) if (k is JObject o) list.Add(o);
                // z — порядок наложения. UI Toolkit его не знает, зато знает
                // порядок детей: сортируем сами.
                //
                // УСТОЙЧИВО — и это не формальность: List.Sort устойчивость НЕ
                // обещает и на длинных списках её не даёт. У детей без своего z
                // (то есть почти у всех) порядок наложения переставал совпадать
                // с порядком, написанным в сценарии, — начиная с семнадцатого
                // ребёнка, где сортировка перестаёт быть вставками.
                list = new List<JObject>(
                    System.Linq.Enumerable.OrderBy(list, o => Num(o["z"], 0)));
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
            // Центрирование просило рамку (см. `at=center`): узел остаётся
            // собой — с именем, привязками и входом, — а место ему держит
            // рамка во весь родитель.
            if (center)
            {
                var wrap = new VisualElement { pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(wrap);
                wrap.style.justifyContent = Justify.Center;
                wrap.style.alignItems = Align.Center;
                el.style.position = Position.Relative;
                wrap.Add(el);
                return wrap;
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
                LvnChrome.EdgeOn(el, LvnSide.Bottom, shade, lift);
                el.style.marginBottom = 0;
            }

            var veil = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(veil);
            veil.style.backgroundColor = new Color(1f, 1f, 1f, 0f);
            if (radius > 0f)
            {
                LvnChrome.Round(veil, radius);
            }
            el.Add(veil);

            // Просадка делается СДВИГОМ, а не отступом: отступ двигает соседей
            // по ряду, и от нажатия одной кнопки дёргается весь ряд.
            void Press()
            {
                veil.style.backgroundColor = new Color(1f, 1f, 1f, 0.13f);
                if (lift > 0f)
                {
                    el.style.borderBottomWidth = 0;   // НАРОЧНО одна толщина: цвет объёма не меняется, кнопка «утоплена»
                    el.style.translate = new Translate(0, lift);
                }
            }
            void Release()
            {
                veil.style.backgroundColor = new Color(1f, 1f, 1f, 0f);
                if (lift > 0f)
                {
                    el.style.borderBottomWidth = lift;   // НАРОЧНО одна толщина: объём вернулся, цвет прежний
                    el.style.translate = new Translate(0, 0);
                }
            }
            el.RegisterCallback<PointerDownEvent>(_ => Press());
            el.RegisterCallback<PointerUpEvent>(_ => Release());
            el.RegisterCallback<PointerLeaveEvent>(_ => Release());
            el.RegisterCallback<PointerCancelEvent>(_ => Release());
        }

        /// <summary>Признак «это полоса» — классом, а не именем. Имя автор
        /// вправе занять своим (`bar id=hp`), и покраска заливки, смотревшая на
        /// имя, у названной полосы молча переставала работать: ширина едет,
        /// красить нечего.</summary>
        internal const string BarClass = "lvn-ui-bar";

        private static VisualElement BuildBar()
        {
            var wrap = new VisualElement { name = "bar" };
            wrap.AddToClassList(BarClass);
            wrap.style.overflow = Overflow.Hidden;
            var fill = new VisualElement { name = "fill", pickingMode = PickingMode.Ignore };
            fill.style.height = Length.Percent(100f);
            fill.style.width = Length.Percent(0f);
            wrap.Add(fill);
            return wrap;
        }

        // ── раскладка ───────────────────────────────────────────────────────

        /// <summary>Возвращает true, если узел просил центр: раскладка сама
        /// центрировать его не может — это делает рамка вокруг, которую
        /// ставит сборщик (см. <c>at=center</c>).</summary>
        private static bool ApplyLayout(VisualElement el, JObject n)
        {
            var s = el.style;
            // ЗАКРЫТОЕ СЛОВО, КОТОРОГО НЕТ В СПИСКЕ, — НЕ МОЛЧАНИЕ. Перечисление
            // случаев имеет тихий исход: не совпало ни с одним — не произошло
            // ничего. Автор пишет «justify=middle», видит вёрстку по умолчанию и
            // идёт искать ошибку в другом месте.
            var dir = (string)n["dir"];
            s.flexDirection = dir == "row" ? FlexDirection.Row : FlexDirection.Column;
            if (dir != null && dir != "row" && dir != "column")
                LvnClosedWord.Unknown("dir", dir, "row | column");

            switch ((string)n["justify"])
            {
                case "center": s.justifyContent = Justify.Center; break;
                case "end": s.justifyContent = Justify.FlexEnd; break;
                case "between": s.justifyContent = Justify.SpaceBetween; break;
                case "around": s.justifyContent = Justify.SpaceAround; break;
                case "start": s.justifyContent = Justify.FlexStart; break;
                default: LvnClosedWord.Unknown("justify", (string)n["justify"],
                    "center | end | between | around | start"); break;
            }
            switch ((string)n["align"])
            {
                case "center": s.alignItems = Align.Center; break;
                case "end": s.alignItems = Align.FlexEnd; break;
                case "stretch": s.alignItems = Align.Stretch; break;
                case "start": s.alignItems = Align.FlexStart; break;
                default: LvnClosedWord.Unknown("align", (string)n["align"],
                    "center | end | stretch | start"); break;
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
                    LvnChrome.Stretch(el); break;
                case "top":
                    s.position = Position.Absolute; s.left = 0; s.right = 0; s.top = 0; break;
                case "bottom":
                    s.position = Position.Absolute; s.left = 0; s.right = 0; s.bottom = 0; break;
                case "left":
                    s.position = Position.Absolute; s.left = 0; s.top = 0; s.bottom = 0; break;
                case "right":
                    s.position = Position.Absolute; s.right = 0; s.top = 0; s.bottom = 0; break;
                case "center":
                    // ЦЕНТР ДЕРЖИТСЯ РАСКЛАДКОЙ, А НЕ СМЕЩЕНИЕМ. Он стоял на
                    // translate(-50%,-50%) — том же свойстве, в которое пишут
                    // виды входа: `appear=up` доводил смещение до нуля и
                    // оставлял узел «центрированным» левым верхним углом,
                    // НАВСЕГДА. Узел заворачивается в невидимую рамку во весь
                    // родитель и центрируется ею — смещение остаётся свободным.
                    return true;
                default:
                    LvnClosedWord.Unknown("at", (string)n["at"],
                        "fill | top | bottom | left | right | center");
                    break;
            }
            return false;
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
            if (n["bg"] != null) tree.Live.Bind(el, "bg", n["bg"]);
            if (n["color"] != null) tree.Live.Bind(el, "color", n["color"]);
            if (n["text"] != null) tree.Live.Bind(el, "text", n["text"]);
            if (n["value"] != null) tree.Live.Bind(el, "value", n["value"]);

            if (n["radius"] != null)
            {
                float r = Num(n["radius"], 0);
                LvnChrome.Round(el, r);
            }
            if (n["edge"] != null)
            {
                float w = Num(n["edge"], 0);
                if (w > 0f)
                {
                    LvnChrome.Border(el, LvnTheme.Current.EdgeColor, w);
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
            if (n["hide"] != null) tree.Live.Bind(el, "hide", n["hide"]);
            if (n["opacity"] != null) tree.Live.Bind(el, "opacity", n["opacity"]);
            if (n["w"] != null && Live(n["w"])) tree.Live.Bind(el, "w", n["w"]);
            if (n["h"] != null && Live(n["h"])) tree.Live.Bind(el, "h", n["h"]);
            // Кегль ВСЕГДА из шкалы темы, даже когда автор его не назвал:
            // иначе текст берёт умолчание панели и выходит мелким рядом с тем,
            // что размер получил. Разнобой на одном экране заметнее, чем
            // неудачный размер.
            var kind = (string)n["kind"];
            if (n["size"] != null) s.fontSize = TextSize(n["size"]);
            else if (kind == "text" || kind == "button") s.fontSize = LvnTokens.TextBase;
            if ((string)n["weight"] == "bold") s.unityFontStyleAndWeight = FontStyle.Bold;
        }

        // Живые значения — свой дом: как завести привязку, когда пересчитать
        // и как поставить значение элементу (см. LvnUiLive).
        private void Refresh()
        {
            var vars = _vars?.Invoke();
            if (vars == null) return;
            foreach (var kv in _trees) kv.Value.Live.Refresh(vars, force: false);
        }

        // Как читается написанное автором — длина, отступ, кегль, цвет,
        // «да/нет», имя значка — живёт отдельно: см. LvnUiValues.
    }
}
