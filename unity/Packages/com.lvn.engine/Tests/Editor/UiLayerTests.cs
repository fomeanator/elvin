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
            public readonly LvnUiLayer Layer;

            public Rig(bool separateOver = true)
            {
                Layer = new LvnUiLayer(HudHost, separateOver ? OverHost : null,
                                       () => Vars, target => Jumps.Add(target));
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
    }
}
