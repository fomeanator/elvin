using System.Collections.Generic;
using System.Reflection;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ДЕРЕВО ИНТЕРФЕЙСА ИЗ СЦЕНАРИЯ — оператор <c>ui</c> (<see cref="LvnUiLayer"/>).
    ///
    /// <para>Это то, чем автор новеллы рисует полосы здоровья, счётчики и боевой
    /// HUD, — и до сих пор у слоя не было ни одного теста. Здесь закреплены
    /// именно ПРАВИЛА, каждое из которых уже стоило живого дефекта: обновление
    /// сверкой (иначе теряется нажатие под пальцем), вход только на появлении
    /// (иначе весь постоянный HUD пульсирует в каждой реплике), ручное скрытие
    /// сильнее стадии, два корня и поджатие нижнего этажа под окно реплики.</para>
    ///
    /// <para>ПРО АНИМАЦИЮ. Ход <see cref="LvnAppear"/> асинхронный, кадров в
    /// EditMode нет — но НАЧАЛЬНОЕ состояние он ставит синхронно, в том же
    /// кадре: прозрачность 0 и уменьшенный масштаб. Именно по нему здесь и
    /// видно, сыграл вход или нет.</para>
    /// </summary>
    public class UiLayerTests
    {
        // ── стенд ───────────────────────────────────────────────────────────

        /// <summary>Слой на двух пустых хозяевах плюс переменные истории. Корни
        /// слой создаёт сам («lvn-ui» под диалогом, «lvn-ui-over» поверх), их и
        /// спрашиваем — так тест смотрит на то же, на что смотрит игрок.</summary>
        private sealed class Rig
        {
            public readonly VisualElement HudHost = new VisualElement();
            public readonly VisualElement OverHost = new VisualElement();
            public readonly Dictionary<string, JToken> Vars = new Dictionary<string, JToken>();
            public readonly List<string> Jumps = new List<string>();
            /// <summary>Что уже лежало в переменных В МОМЕНТ каждого перехода —
            /// по нему видно, успела ли объектная форма нажатия записать их до
            /// того, как история ушла по метке.</summary>
            public readonly List<Dictionary<string, JToken>> VarsAtJump
                = new List<Dictionary<string, JToken>>();
            public readonly LvnUiLayer Layer;

            public Rig(bool separateOver = true)
            {
                Layer = new LvnUiLayer(HudHost, separateOver ? OverHost : null,
                                       () => Vars,
                                       target =>
                                       {
                                           VarsAtJump.Add(new Dictionary<string, JToken>(Vars));
                                           Jumps.Add(target);
                                       },
                                       loadImage: null,
                                       setVars: ops =>
                                       {
                                           foreach (var p in ops.Properties()) Vars[p.Name] = p.Value;
                                       });
            }

            public VisualElement Hud => HudHost.Q("lvn-ui");
            public VisualElement Over => OverHost.Q("lvn-ui-over") ?? Hud;

            public void Ui(string json) => Layer.Apply(JObject.Parse(json));

            /// <summary>Опрос живых значений. В игре его крутит планировщик
            /// панели (каждые 60 мс), а в EditMode панели нет вовсе — поэтому
            /// такт даём руками, иначе правило «значения обновляются сами»
            /// проверить нечем.</summary>
            public void Poll()
            {
                var m = typeof(LvnUiLayer).GetMethod("Refresh",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(m, "у слоя пропал приватный Refresh — опрос живых значений переехал?");
                m.Invoke(Layer, null);
            }
        }

        private static DisplayStyle Shown(VisualElement el) => el.style.display.value;
        private static float Opacity(VisualElement el) => el.style.opacity.value;
        private static Vector2 Scale(VisualElement el) => el.style.scale.value.value;

        /// <summary>Считать вход отыгранным: так выглядит дерево, когда
        /// анимация появления доехала до конца. Дальше любое обнуление
        /// прозрачности — это ВТОРОЙ вход, и он тут же виден.</summary>
        private static void Settle(VisualElement el)
        {
            el.style.opacity = 1f;
            el.style.scale = new UnityEngine.UIElements.Scale(Vector2.one);
        }

        // ── 1. Обновление сверкой, а не пересборкой ─────────────────────────

        [Test]
        public void ТоЖеДеревоПереиспользуетЖивыеЭлементы()
        {
            // Сценарий объявляет экран заново НА КАЖДОМ ШАГЕ — это норма языка.
            // Если каждое такое объявление пересобирает дерево, у игрока из-под
            // пальца исчезает кнопка, сбрасывается прокрутка и обрывается
            // недоигранная анимация. Ради этого механизм и написан.
            const string script = @"{'op':'ui','id':'бой','tree':{
                'kind':'panel','id':'корень','at':'bottom','children':[
                    {'kind':'text','id':'счёт','text':'Ход 1'},
                    {'kind':'button','id':'удар','text':'Бить','on_click':'hit'}
                ]}}";
            var rig = new Rig();
            rig.Ui(script);

            var root = rig.Hud.Q("корень");
            var label = rig.Hud.Q<Label>("счёт");
            var button = rig.Hud.Q<Button>("удар");
            Assert.NotNull(root); Assert.NotNull(label); Assert.NotNull(button);

            // Живое состояние, которого нет в описании: допечатанная реплика на
            // метке и доигранный вход у корня.
            label.text = "Ход 1 — напечатано на месте";
            Settle(root);

            rig.Ui(script);   // то же самое дерево вторым объявлением

            Assert.AreSame(root, rig.Hud.Q("корень"), "корень обязан остаться тем же элементом");
            Assert.AreSame(label, rig.Hud.Q<Label>("счёт"), "метка пересобрана — потеряна печать");
            Assert.AreSame(button, rig.Hud.Q<Button>("удар"), "кнопка пересобрана — нажатие под пальцем потеряно");
            Assert.AreEqual("Ход 1 — напечатано на месте", label.text,
                "живой текст затёрт описанием: это и есть пересборка");
            Assert.AreEqual(1f, Opacity(root), 0.001f,
                "повторное объявление не должно переигрывать вход");
            Assert.AreEqual(1, rig.Hud.childCount, "на экране остаётся ровно одно дерево");
        }

        [Test]
        public void ИзменённоеДеревоЗаменяетПрежнее_БезДублейНаЭкране()
        {
            // Обратная половина того же правила: описание ИЗМЕНИЛОСЬ — старое
            // дерево обязано уйти с экрана целиком. Забытый корень остаётся
            // висеть поверх нового, и автор видит две полосы здоровья.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'бой','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'text','id':'строка','text':'первое'}]}}");
            var first = rig.Hud.Q("корень");

            rig.Ui(@"{'op':'ui','id':'бой','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'text','id':'строка','text':'второе'}]}}");

            Assert.AreEqual(1, rig.Hud.childCount, "старое дерево не снято — на экране два");
            Assert.IsNull(first.parent, "прежний корень остался в иерархии");
            Assert.AreEqual("второе", rig.Hud.Q<Label>("строка").text);
        }

        // ── 2. `when` — при какой стадии дерево видно ────────────────────────

        [Test]
        public void КогдаВидноРешаетСтадияСцены()
        {
            // Без этого автор прятал дерево руками в каждой ветке и одну ветку
            // неизбежно забывал — боевой интерфейс оставался поверх разговора.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'a','tree':{'kind':'panel','id':'всегда'}}");
            rig.Ui(@"{'op':'ui','id':'b','when':'idle','tree':{'kind':'panel','id':'покой'}}");
            rig.Ui(@"{'op':'ui','id':'c','when':'say','tree':{'kind':'panel','id':'реплика'}}");
            rig.Ui(@"{'op':'ui','id':'d','when':'choice','tree':{'kind':'panel','id':'выбор'}}");

            var always = rig.Hud.Q("всегда");
            var idle = rig.Hud.Q("покой");
            var say = rig.Hud.Q("реплика");
            var choice = rig.Hud.Q("выбор");

            // Тишина: ни реплики, ни выбора.
            Assert.AreEqual(DisplayStyle.Flex, Shown(idle), "покой — это когда на экране ничего не ждёт");
            Assert.AreEqual(DisplayStyle.None, Shown(say));
            Assert.AreEqual(DisplayStyle.None, Shown(choice));

            rig.Layer.SetStage(true, false, 220f);   // идёт реплика
            Assert.AreEqual(DisplayStyle.Flex, Shown(say));
            Assert.AreEqual(DisplayStyle.None, Shown(idle), "подсказка покоя не должна лежать на реплике");
            Assert.AreEqual(DisplayStyle.None, Shown(choice));

            rig.Layer.SetStage(false, true, 220f);   // показан выбор
            Assert.AreEqual(DisplayStyle.Flex, Shown(choice));
            Assert.AreEqual(DisplayStyle.None, Shown(say));
            Assert.AreEqual(DisplayStyle.None, Shown(idle));

            // `when` не написан — дерево висит всю сцену: это весь постоянный
            // HUD (полосы, счётчики, трекер), и умолчание тут дороже всего.
            Assert.AreEqual(DisplayStyle.Flex, Shown(always), "умолчание when — always");
        }

        // ── 3. Вход играется на появлении, а не на каждой смене стадии ───────

        [Test]
        public void ВходИграетсяНаПоявлении_АНеНаКаждойСменеСтадии()
        {
            // Стадия меняется дважды за реплику: текст допечатался, игрок
            // тапнул. Раньше оба раза сюда приходили ВСЕ деревья, включая те,
            // что никуда не пропадали, — и постоянный HUD обнулялся в
            // прозрачность и всплывал заново. Со стороны это ровная пульсация
            // интерфейса в любой сцене с диалогом.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'корень'}}");
            var root = rig.Hud.Q("корень");

            // Вход на самом появлении обязан быть: начальное состояние LvnAppear
            // ставится синхронно, и вот оно.
            Assert.AreEqual(0f, Opacity(root), 0.001f, "дерево должно выйти на экран входом");
            Assert.Less(Scale(root).x, 1f, "вход начинается с уменьшенного масштаба");

            Settle(root);   // анимация доехала

            rig.Layer.SetStage(true, false, 220f);    // допечаталась реплика
            rig.Layer.SetStage(false, true, 220f);    // игрок тапнул, пришёл выбор

            Assert.AreEqual(1f, Opacity(root), 0.001f,
                "видимое дерево переиграло вход на смене стадии — это пульсация HUD");
            Assert.AreEqual(Vector2.one, Scale(root),
                "масштаб обнулён заново — вход сыгран второй раз");
        }

        // ── 4. Ручное скрытие сильнее стадии ────────────────────────────────

        [Test]
        public void РучноеСкрытиеСильнееСтадии()
        {
            // `ui X hide` — прямое слово автора. Стадия не должна его отменять,
            // иначе первая же реплика возвращает спрятанное меню на экран.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'меню','tree':{'kind':'panel','id':'корень'}}");
            var root = rig.Hud.Q("корень");

            rig.Ui(@"{'op':'ui','id':'меню','action':'hide'}");
            Assert.AreEqual(DisplayStyle.None, Shown(root));

            rig.Layer.SetStage(true, false, 220f);
            Assert.AreEqual(DisplayStyle.None, Shown(root), "стадия спорит с рукой автора");
            rig.Layer.SetStage(false, false, 0f);
            Assert.AreEqual(DisplayStyle.None, Shown(root), "стадия спорит с рукой автора");
        }

        [Test]
        public void ВернувшеесяДеревоИграетВходЗаново()
        {
            // Спрятанное рукой считается УШЕДШИМ. Вернуть его без входа значит
            // «моргнуть» им на экране — предмет обязан прийти так же, как
            // пришёл в первый раз.
            //
            // ПАРА hide+show ПОДРЯД — самый частый случай и самый хрупкий:
            // команда `hide` однажды убирала дерево напрямую, мимо разбора
            // стадии, и признак «стоит на экране» оставался поднятым. Тогда
            // `show` считал, что дерево никуда не уходило, и оно возникало на
            // месте вместо того, чтобы проступить. Смены стадии между ними тут
            // НЕТ намеренно: с ней путь чинится сам и дефект не виден.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'меню','tree':{'kind':'panel','id':'корень'}}");
            var root = rig.Hud.Q("корень");
            Settle(root);

            rig.Ui(@"{'op':'ui','id':'меню','action':'hide'}");
            Assert.AreEqual(DisplayStyle.None, Shown(root));

            rig.Ui(@"{'op':'ui','id':'меню','action':'show'}");

            Assert.AreEqual(DisplayStyle.Flex, Shown(root), "show обязан вернуть дерево");
            Assert.AreEqual(0f, Opacity(root), 0.001f, "вернувшееся дерево должно сыграть вход");
        }

        // ── 5. Нижний этаж поджимается под окно реплики ─────────────────────

        [Test]
        public void НижнийЭтажПоджимаетсяНаВысотуОкнаРеплики()
        {
            // `at=bottom` обязано значить «над диалогом», а не «под ним». Пока
            // этого не было, автор подбирал отступ руками, и отступ разъезжался
            // на первом же длинном имени говорящего.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'полоса','at':'bottom','h':40}}");
            var bar = rig.Hud.Q("полоса");

            Assert.AreEqual(Position.Absolute, bar.style.position.value, "at=bottom — абсолютная привязка");
            Assert.AreEqual(0f, bar.style.bottom.value.value, 0.001f, "узел прижат к низу СВОЕГО этажа");

            rig.Layer.SetStage(true, false, 240f);
            Assert.AreEqual(240f, rig.Hud.style.bottom.value.value, 0.001f,
                "нижний этаж не поджался — HUD уехал под окно реплики");
            Assert.AreEqual(0f, rig.Over.style.bottom.value.value, 0.001f,
                "верхний этаж лежит поверх всего и диалогу не уступает");

            rig.Layer.SetStage(false, false, 0f);
            Assert.AreEqual(0f, rig.Hud.style.bottom.value.value, 0.001f,
                "окно ушло — этаж обязан вернуть себе низ экрана");
        }

        // ── 6. Два корня ────────────────────────────────────────────────────

        [Test]
        public void ДеревоУходитВСвойЭтаж()
        {
            // Спор за низ экрана иначе неразрешим: боевой интерфейс обязан
            // уходить ПОД окно реплики, полноэкранное меню — лежать поверх
            // всего, и то и другое встречается в одной главе. На первой живой
            // проверке ряд кнопок боевого интерфейса закрыл собой текст.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'бой','tree':{'kind':'panel','id':'боевой'}}");
            rig.Ui(@"{'op':'ui','id':'меню','layer':'over','tree':{'kind':'panel','id':'меню-корень'}}");

            Assert.AreNotSame(rig.Hud, rig.Over, "этажа должно быть два, а не один");
            Assert.AreSame(rig.Hud, rig.Hud.Q("боевой").parent, "hud — умолчание слоя");
            Assert.AreSame(rig.Over, rig.Over.Q("меню-корень").parent, "layer=over кладёт дерево поверх всего");
            Assert.AreEqual(1, rig.Hud.childCount);
            Assert.AreEqual(1, rig.Over.childCount);
        }

        [Test]
        public void БезВторогоХозяинаДеревоНеТеряется()
        {
            // Движок встраивают и в один контейнер (оболочка без отдельного
            // верхнего слоя). Тогда этаж один — но `layer=over` не должно
            // ронять дерево на пол: экран без меню хуже, чем меню не на своём
            // месте.
            var rig = new Rig(separateOver: false);
            rig.Ui(@"{'op':'ui','id':'меню','layer':'over','tree':{'kind':'panel','id':'корень'}}");

            Assert.NotNull(rig.Hud.Q("корень"), "дерево верхнего слоя пропало вместе с хозяином");
        }

        // ── 7. Живые значения ───────────────────────────────────────────────

        [Test]
        public void ЖивойТекстОбновляетсяОпросом()
        {
            // Сигнала «переменная изменилась» в движке нет, и заводить его
            // значило бы менять каждое место записи. Поэтому опрос — но опрос
            // обязан доходить до элемента.
            var rig = new Rig();
            rig.Vars["hp"] = 10;
            rig.Vars["max"] = 10;
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'text','id':'строка','text':'HP {hp}/{max}'}]}}");

            var label = rig.Hud.Q<Label>("строка");
            Assert.AreEqual("HP 10/10", label.text, "первое значение кладётся сразу, без ожидания такта");

            rig.Vars["hp"] = 3;
            rig.Poll();
            Assert.AreEqual("HP 3/10", label.text, "живое значение не доехало до метки");
        }

        [Test]
        public void ОпросНеТрогаетНеизменившееся()
        {
            // Опрос идёт каждые 60 мс по ВСЕМ привязкам. Если он пишет в
            // элемент безусловно, он затирает всё, что живёт на элементе между
            // тактами, — печать по буквам, подсветку, чужую анимацию, — и
            // делает это шестнадцать раз в секунду.
            var rig = new Rig();
            rig.Vars["gold"] = 7;
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'text','id':'строка','text':'{gold}'}]}}");

            var label = rig.Hud.Q<Label>("строка");
            Assert.AreEqual("7", label.text);

            label.text = "печатается…";   // живое состояние между тактами
            rig.Poll();                    // переменная та же

            Assert.AreEqual("печатается…", label.text,
                "неизменившееся значение всё равно доехало до элемента и затёрло живое состояние");
        }

        [Test]
        public void ПолосаЕдетВДолюЗначения()
        {
            // Полоса — то, ради чего затевались живые значения: раньше это были
            // семнадцать веток с литеральными ширинами. Заливка ЕДЕТ, а не
            // прыгает: мгновенный скачок здоровья читается как сбой отрисовки.
            var rig = new Rig();
            rig.Vars["hp"] = 25;
            rig.Vars["max"] = 100;
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'bar','w':'60%','h':8,'bg':'track','color':'accent','value':'{hp/max}'}]}}");

            var bar = rig.Hud.Q("bar");
            Assert.NotNull(bar, "полоса не построена");
            Assert.AreEqual(1, bar.childCount, "у полосы должна быть заливка");
            var fill = bar[0];

            Assert.AreEqual(25f, fill.style.width.value.value, 0.001f, "доля не доехала до ширины заливки");
            Assert.AreEqual(LengthUnit.Percent, fill.style.width.value.unit, "ширина заливки — доля, а не пиксели");
            Assert.AreEqual(1, fill.style.transitionDuration.value.Count,
                "заливка прыгает вместо того, чтобы ехать");

            // Перелечили сверх максимума — полоса не имеет права вылезти за края.
            rig.Vars["hp"] = 250;
            rig.Poll();
            Assert.AreEqual(100f, fill.style.width.value.value, 0.001f, "доля больше единицы не обрезана");

            rig.Vars["hp"] = -40;
            rig.Poll();
            Assert.AreEqual(0f, fill.style.width.value.value, 0.001f, "отрицательное здоровье вывернуло полосу");
        }

        [Test]
        public void ЖивоеСкрытиеУзлаЧитаетсяРантаймом()
        {
            // «Кнопка видна, только если хватает золота» — иначе это делалось
            // бы пересборкой всего дерева, с потерей нажатия под пальцем. Само
            // поле `hide` компилятор принимал давно, а рантайм не читал вовсе —
            // молча.
            var rig = new Rig();
            rig.Vars["gold"] = 5;
            rig.Ui(@"{'op':'ui','id':'лавка','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'button','id':'купить','text':'Купить','hide':'{gold < 10}'}]}}");

            var buy = rig.Hud.Q<Button>("купить");
            Assert.AreEqual(DisplayStyle.None, Shown(buy), "денег не хватает — кнопки быть не должно");

            rig.Vars["gold"] = 50;
            rig.Poll();
            Assert.AreEqual(DisplayStyle.Flex, Shown(buy), "денег хватило — кнопка обязана вернуться");
        }

        // ── срок жизни ──────────────────────────────────────────────────────

        [Test]
        public void DropУбираетОдноДерево_ClearВсе()
        {
            // `drop` — слово автора про одно дерево, `Clear` — смена главы.
            // Перепутать их значит либо оставить чужой интерфейс на новой
            // главе, либо снести соседнее дерево вместе с названным.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'бой','tree':{'kind':'panel','id':'боевой'}}");
            rig.Ui(@"{'op':'ui','id':'меню','layer':'over','tree':{'kind':'panel','id':'меню-корень'}}");

            rig.Ui(@"{'op':'ui','id':'бой','action':'drop'}");
            Assert.AreEqual(0, rig.Hud.childCount, "drop не снял названное дерево");
            Assert.AreEqual(1, rig.Over.childCount, "drop снёс соседнее дерево");

            // Снятое имя обязано освободиться: та же новелла объявляет дерево
            // заново, и оно должно построиться, а не считаться живым.
            rig.Ui(@"{'op':'ui','id':'бой','tree':{'kind':'panel','id':'боевой'}}");
            Assert.AreEqual(1, rig.Hud.childCount, "имя после drop осталось занятым");

            rig.Layer.Clear();
            Assert.AreEqual(0, rig.Hud.childCount, "смена главы не убрала нижний этаж");
            Assert.AreEqual(0, rig.Over.childCount, "смена главы не убрала верхний этаж");

            // Команда про несуществующее дерево — опечатка автора, а не повод
            // ронять главу.
            Assert.DoesNotThrow(() => rig.Ui(@"{'op':'ui','id':'нетакого','action':'hide'}"));
            Assert.DoesNotThrow(() => rig.Ui(@"{'op':'ui','id':'нетакого','action':'drop'}"));
        }

        // ── центр ───────────────────────────────────────────────────────────

        [Test]
        public void ЦентрНеОтнимаетСмещениеУВхода()
        {
            // `at=center` держался на translate(-50%,-50%) — том же свойстве, в
            // которое пишет вход. `appear` доводил смещение до нуля, и узел
            // оставался «центрированным» СВОИМ ЛЕВЫМ ВЕРХНИМ УГЛОМ навсегда:
            // окно уезжало вправо вниз и больше не возвращалось.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'окно','tree':{
                'kind':'panel','id':'рамка','at':'center','appear':'up',
                'children':[{'kind':'text','id':'слово','text':'привет'}]}}");

            var node = rig.Hud.Q("рамка");
            Assert.NotNull(node, "узел с at=center не найден");

            // Вход отыгран до конца — смещение обнулено, как и бывает в игре.
            node.style.translate = new Translate(0, 0);

            var wrap = node.parent;
            Assert.AreNotSame(rig.Hud, wrap,
                "центр обязан держаться рамкой: иначе вход и центрирование делят одно свойство");
            Assert.AreEqual(Justify.Center, wrap.style.justifyContent.value,
                "рамка не центрирует по горизонтали");
            Assert.AreEqual(Align.Center, wrap.style.alignItems.value,
                "рамка не центрирует по вертикали");
            Assert.AreEqual(PickingMode.Ignore, wrap.pickingMode,
                "рамка во весь родитель обязана пропускать касания насквозь");
        }

        [Test]
        public void ЦентрНеПереходитНаДетей()
        {
            // Решение о рамке принимается при разборе раскладки, а исполняется
            // после сборки детей — а дети идут этим же путём. Не сними флаг
            // сразу — рамку получит ПЕРВЫЙ РЕБЁНОК вместо родителя, и в центре
            // окажется не окно, а его первая строка.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'окно','tree':{
                'kind':'panel','id':'рамка','at':'center','children':[
                    {'kind':'text','id':'строка','text':'привет'},
                    {'kind':'text','id':'вторая','text':'пока'}]}}");

            var line = rig.Hud.Q("строка");
            Assert.NotNull(line);
            Assert.AreSame(rig.Hud.Q("рамка"), line.parent,
                "ребёнок завёрнут в чужую рамку — центрирование протекло вниз");
        }

        // ── нажатие ─────────────────────────────────────────────────────────

        /// <summary>Обработчик нажатия кнопки — тот, на который слой подписал
        /// переход. Живого тапа в EditMode нет: события разносит панель, а
        /// панели здесь нет вовсе, поэтому спрашиваем саму кнопку, к чему она
        /// приведёт. Null — не подписан никто.</summary>
        private static System.Action Handler(Button b) => Нажатие.Обработчик(b);

        [Test]
        public void НажатиеПоКнопкеВедётПоМеткеАвтора()
        {
            // Ради этого кнопку и рисуют. `on_click` — единственный способ
            // вернуть управление истории: не дойди метка до перехода, меню
            // автора становится картинкой, из которой нет выхода, и глава
            // встаёт намертво.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'меню','layer':'over','block':true,'tree':{
                'kind':'panel','id':'корень','children':[
                    {'kind':'button','id':'играть','text':'Играть','on_click':'старт'},
                    {'kind':'button','id':'молча','text':'Просто надпись'}]}}");

            var переход = Handler(rig.Over.Q<Button>("играть"));
            Assert.NotNull(переход, "нажатие по кнопке никуда не ведёт: on_click не подписан");
            переход.Invoke();

            CollectionAssert.AreEqual(new[] { "старт" }, rig.Jumps,
                "нажатие увело не по той метке, что написал автор");

            Assert.IsNull(Handler(rig.Over.Q<Button>("молча")),
                "кнопка без on_click куда-то ведёт — история прыгнет от случайного тапа");
        }

        [Test]
        public void НажатиеПишетПеременныеПЕРЕДТемКакУйтиПоМетке()
        {
            // Объектная форма (`on_click={goto, set}`) — законная запись языка,
            // и порядок в ней не косметика: ветка, в которую ведёт метка, ЭТИ ЖЕ
            // переменные и читает. Запиши их после перехода — и первая реплика
            // новой ветки увидит старые значения: игрок взял ключ, а дверь
            // говорит, что ключа нет.
            var rig = new Rig();
            rig.Vars["ключ"] = false;
            rig.Ui(@"{'op':'ui','id':'сумка','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'button','id':'взять','text':'Взять ключ',
                         'on_click':{'goto':'дверь','set':{'ключ':true,'шагов':2}}}]}}");

            var нажать = Handler(rig.Hud.Q<Button>("взять"));
            Assert.NotNull(нажать, "объектная форма нажатия никуда не ведёт");
            нажать.Invoke();

            CollectionAssert.AreEqual(new[] { "дверь" }, rig.Jumps, "нажатие увело не по той метке");
            Assert.AreEqual(1, rig.VarsAtJump.Count);
            Assert.AreEqual(true, (bool)rig.VarsAtJump[0]["ключ"],
                "переход случился РАНЬШЕ записи — новая ветка прочитает старые значения");
            Assert.AreEqual(2, (int)rig.VarsAtJump[0]["шагов"], "записана не вся правка");
        }

        [Test]
        public void ОбъектнаяФормаНажатияНеУноситВесьИнтерфейс()
        {
            // Поле приводили к строке напрямую, а приведение объекта к строке в
            // Newtonsoft БРОСАЕТ: исключение уходило наверх, дерево не строилось
            // вовсе — у игрока на этом шаге пропадал ВЕСЬ интерфейс. Автор при
            // этом написал ровно то, что написано в документации движка, просто
            // не в том операторе.
            //
            // Слой собирают и БЕЗ приёмника переменных (встраивание, стенд,
            // демо). Тогда объектная форма обязана отработать хотя бы переходом,
            // а не остаться мёртвой кнопкой.
            var host = new VisualElement();
            var vars = new Dictionary<string, JToken>();
            var jumps = new List<string>();
            var layer = new LvnUiLayer(host, null, () => vars, t => jumps.Add(t));

            Assert.DoesNotThrow(() => layer.Apply(JObject.Parse(
                @"{'op':'ui','id':'сумка','tree':{'kind':'panel','id':'корень','children':[
                    {'kind':'button','id':'взять','text':'Взять',
                     'on_click':{'goto':'дверь','set':{'ключ':true}}}]}}")),
                "объектная форма нажатия уронила сборку — интерфейс пропал целиком");

            var корень = host.Q("корень");
            Assert.NotNull(корень, "дерево не построилось: у игрока пустой экран вместо интерфейса");

            var нажать = Handler(host.Q<Button>("взять"));
            Assert.NotNull(нажать, "без приёмника переменных кнопка осталась мёртвой");
            нажать.Invoke();
            CollectionAssert.AreEqual(new[] { "дверь" }, jumps,
                "без приёмника переменных потерялся и переход — из меню нет выхода");
        }

        // ── порядок наложения ───────────────────────────────────────────────

        [Test]
        public void ПриРавномZПорядокОстаётсяАвторским()
        {
            // z — порядок наложения, и UI Toolkit его не знает: слой сортирует
            // детей сам. Но своего z нет ПОЧТИ У ВСЕХ, и для них «порядок
            // наложения» означает ровно «как написано в сценарии».
            //
            // Сортировка обязана быть устойчивой. List.Sort устойчивость не
            // обещает и на длинных списках её не даёт — начиная примерно с
            // семнадцатого ребёнка порядок переставал совпадать с авторским, и
            // ряд кнопок молча перемешивался. Поэтому детей здесь двадцать, а
            // не три: на трёх дефект не виден.
            var дети = new List<string>();
            for (int i = 0; i < 20; i++) дети.Add("{'kind':'text','id':'n" + i + "','text':'" + i + "'}");
            var rig = new Rig();
            rig.Ui("{'op':'ui','id':'ряд','tree':{'kind':'panel','id':'корень','children':["
                   + string.Join(",", дети) + "]}}");

            var корень = rig.Hud.Q("корень");
            var порядок = new List<string>();
            for (int i = 0; i < корень.childCount; i++) порядок.Add(корень[i].name);

            var какНаписано = new List<string>();
            for (int i = 0; i < 20; i++) какНаписано.Add("n" + i);
            CollectionAssert.AreEqual(какНаписано, порядок,
                "дети без своего z перемешались — порядок наложения разошёлся с написанным в сценарии");
        }

        [Test]
        public void ЯвныйZПоднимаетУзелНадСоседями()
        {
            // Ради этого сортировка и заводилась: подсказка обязана лечь ПОВЕРХ
            // полосы, как бы автор ни расставил их в тексте. Равные z при этом
            // остаются в авторском порядке — иначе одно поднятое окно
            // перемешивало бы всё остальное.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'экран','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'text','id':'подсказка','z':5,'text':'жми'},
                        {'kind':'text','id':'первый','text':'а'},
                        {'kind':'text','id':'второй','text':'б'},
                        {'kind':'text','id':'фон','z':-1,'text':'в'}]}}");

            var корень = rig.Hud.Q("корень");
            var порядок = new List<string>();
            for (int i = 0; i < корень.childCount; i++) порядок.Add(корень[i].name);

            CollectionAssert.AreEqual(new[] { "фон", "первый", "второй", "подсказка" }, порядок,
                "z не решает, кто лежит поверх кого, — или он перемешал равных между собой");
        }

        // ── центр внутри дерева ─────────────────────────────────────────────

        [Test]
        public void ЦентрВнутриДереваОстаётсяВСвоёмРодителе()
        {
            // Рамка центрирования встаёт ВО ВЕСЬ РОДИТЕЛЬ. Улети она в корень
            // слоя, узел центрировался бы по всему экрану: значок посреди
            // полосы здоровья оказался бы посреди игры, поверх реплики.
            var rig = new Rig();
            rig.Vars["доля"] = 30;
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'panel','id':'полоса','h':40,'children':[
                            {'kind':'text','id':'подпись','at':'center','text':'{доля}%'}]},
                        {'kind':'text','id':'снизу','text':'внизу'}]}}");

            var подпись = rig.Hud.Q("подпись");
            Assert.NotNull(подпись, "центрированный узел потерялся внутри дерева");

            var рамка = подпись.parent;
            Assert.AreSame(rig.Hud.Q("полоса"), рамка.parent,
                "рамка центрирования уехала из своего родителя — узел встал по центру экрана");
            Assert.AreEqual(Justify.Center, рамка.style.justifyContent.value);

            // Узел остаётся собой: имя, привязки и живое значение при нём.
            Assert.AreEqual("30%", ((Label)подпись).text, "центрированный узел потерял живое значение");
            rig.Vars["доля"] = 75;
            rig.Poll();
            Assert.AreEqual("75%", ((Label)подпись).text,
                "живое значение перестало доходить до узла, завёрнутого в рамку");

            Assert.AreEqual(2, rig.Hud.Q("корень").childCount,
                "рамка добавилась лишним ребёнком корню — раскладка соседей поехала");
        }

        // ── зазор и единицы размеров ────────────────────────────────────────

        [Test]
        public void ЗазорРазводитДетей_НоНеОтталкиваетПоследнего()
        {
            // Зазора между детьми в UI Toolkit нет вовсе, и слой раскладывает
            // его отступами. Повесь отступ и на последнего — панель получит
            // хвост пустоты: ряд кнопок отъедет от края, а `at=bottom`
            // перестанет означать «у нижней грани».
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'столб','tree':{'kind':'panel','id':'столбец','gap':3,'children':[
                        {'kind':'text','id':'а','text':'а'},
                        {'kind':'text','id':'б','text':'б'},
                        {'kind':'text','id':'в','text':'в'}]}}");

            var столбец = rig.Hud.Q("столбец");
            Assert.AreEqual(LvnTokens.Space3, столбец[0].style.marginBottom.value.value, 0.01f,
                "зазор «3» — это ступень шкалы темы, а не три пикселя: три пикселя глазом не видно");
            Assert.AreEqual(LvnTokens.Space3, столбец[1].style.marginBottom.value.value, 0.01f,
                "зазор достался не всем детям");
            Assert.AreEqual(0f, столбец[2].style.marginBottom.value.value, 0.01f,
                "последний ребёнок унёс зазор за собой — у панели вырос хвост пустоты");
            Assert.AreEqual(0f, столбец[0].style.marginRight.value.value, 0.01f,
                "в столбце зазор развёл детей вбок");

            // Ряд разводится в свою сторону: тот же зазор снизу оставил бы
            // кнопки стоять вплотную.
            rig.Ui(@"{'op':'ui','id':'ряд','tree':{'kind':'panel','id':'строка','dir':'row','gap':12,'children':[
                        {'kind':'text','id':'левый','text':'л'},
                        {'kind':'text','id':'правый','text':'п'}]}}");

            var строка = rig.Hud.Q("строка");
            Assert.AreEqual(12f, строка[0].style.marginRight.value.value, 0.01f,
                "в ряду зазор не развёл детей вбок");
            Assert.AreEqual(0f, строка[0].style.marginBottom.value.value, 0.01f,
                "в ряду зазор ушёл вниз");
            Assert.AreEqual(0f, строка[1].style.marginRight.value.value, 0.01f,
                "последний в ряду унёс зазор за собой");
        }

        [Test]
        public void РазмерыЧитаютсяВСвоихЕдиницах()
        {
            // Доля и пиксели у стилей UI Toolkit — разные типы, и перепутать их
            // значит поставить 50 пикселей там, где автор просил половину
            // экрана. А `auto` язык обещает (`w=auto` — «по содержимому»), и
            // разбирался он как мусор — то есть как ноль: узел схлопывался в
            // невидимую точку, и автор видел пустое место вместо надписи.
            var rig = new Rig();
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'panel','id':'доля','w':'50%','h':'25%'},
                        {'kind':'panel','id':'точки','w':120,'h':8},
                        {'kind':'panel','id':'по-содержимому','w':'auto','h':'auto'}]}}");

            var доля = rig.Hud.Q("доля");
            Assert.AreEqual(LengthUnit.Percent, доля.style.width.value.unit,
                "процент прочитан как пиксели — полэкрана стало полусотней точек");
            Assert.AreEqual(50f, доля.style.width.value.value, 0.01f);
            Assert.AreEqual(LengthUnit.Percent, доля.style.height.value.unit, "процент высоты прочитан как пиксели");

            var точки = rig.Hud.Q("точки");
            Assert.AreEqual(LengthUnit.Pixel, точки.style.width.value.unit, "число прочитано как доля");
            Assert.AreEqual(120f, точки.style.width.value.value, 0.01f);

            var авто = rig.Hud.Q("по-содержимому");
            Assert.AreEqual(StyleKeyword.Auto, авто.style.width.keyword,
                "«auto» разобрано числом — узел схлопнулся в невидимую точку");
            Assert.AreEqual(StyleKeyword.Auto, авто.style.height.keyword,
                "«auto» по высоте разобрано числом — узел схлопнулся в полоску");
        }

        [Test]
        public void ЖивойРазмерВиденСПервогоЖеКадра()
        {
            // Размер с {…} нельзя разбирать раскладкой: там он превратится в
            // ноль, и полоса моргнёт схлопнутой ровно в тот кадр, в который
            // появилась. Поэтому живой размер ставит первая же сверка — и
            // ставит её ДО того, как дерево покажут.
            var rig = new Rig();
            rig.Vars["width"] = 40;
            rig.Ui(@"{'op':'ui','id':'хад','tree':{'kind':'panel','id':'корень','children':[
                        {'kind':'panel','id':'полоска','w':'{width}%','h':6}]}}");

            var полоска = rig.Hud.Q("полоска");
            Assert.AreEqual(LengthUnit.Percent, полоска.style.width.value.unit, "живая ширина потеряла долю");
            Assert.AreEqual(40f, полоска.style.width.value.value, 0.01f,
                "живая ширина не доехала до элемента при первом показе — узел моргнул схлопнутым");

            rig.Vars["width"] = 80;
            rig.Poll();
            Assert.AreEqual(80f, полоска.style.width.value.value, 0.01f, "живая ширина перестала обновляться");
        }
    }
}
