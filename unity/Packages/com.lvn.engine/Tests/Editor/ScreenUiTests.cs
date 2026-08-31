using System.Reflection;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // Guards the shared shell-screen UI primitives that used to live as private
    // copies in five or six screens — so the consolidation can't silently drift.
    public class ScreenUiTests
    {
        [Test]
        public void Stretch_PinsElementToAllEdges()
        {
            var el = ScreenUi.Stretch(new VisualElement());
            Assert.AreEqual(Position.Absolute, el.style.position.value);
            Assert.AreEqual(0f, el.style.left.value.value);
            Assert.AreEqual(0f, el.style.right.value.value);
            Assert.AreEqual(0f, el.style.top.value.value);
            Assert.AreEqual(0f, el.style.bottom.value.value);
        }

        [Test]
        public void ProgressBar_BuildsTrackUnderZeroWidthFill()
        {
            var bar = ScreenUi.ProgressBar(0.5f, 0.8f, 0.6f, 0.02f, Color.gray, Color.white,
                out var track, out var fill);
            Assert.AreEqual(2, bar.childCount, "bar holds track + fill");
            Assert.AreSame(track, bar[0], "track sits behind");
            Assert.AreSame(fill, bar[1], "fill sits in front");
            Assert.AreEqual(Position.Absolute, bar.style.position.value);
            Assert.AreEqual(0f, fill.style.width.value.value, "fill starts empty");
            Assert.AreEqual(LengthUnit.Percent, fill.style.width.value.unit);
        }

        [Test]
        public void CenterLabel_IsCentredAndIgnoresInput()
        {
            var l = ScreenUi.CenterLabel(0.5f, Color.white, 20f);
            Assert.AreEqual(TextAnchor.MiddleCenter, l.style.unityTextAlign.value);
            Assert.AreEqual(PickingMode.Ignore, l.pickingMode);
            Assert.AreEqual(20f, l.style.fontSize.value.value);
        }

        [Test]
        public void SetText_IsNullSafe()
        {
            Assert.DoesNotThrow(() => ScreenUi.SetText(null, "x"));
            var l = new Label();
            ScreenUi.SetText(l, "hi");
            Assert.AreEqual("hi", l.text);
        }

        // ── кнопка «назад» ──────────────────────────────────────────────────

        /// <summary>К чему приведёт нажатие. Живого тапа в EditMode нет —
        /// события разносит панель, а панели здесь нет, — поэтому спрашиваем
        /// саму кнопку. Null значит «не подписан никто».</summary>
        private static System.Action Обработчик(Button b)
        {
            Assert.NotNull(b, "кнопки нет");
            foreach (var f in typeof(Clickable).GetFields(
                         BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (f.GetValue(b.clickable) is System.Action a) return a;
            return null;
        }

        [Test]
        public void КнопкаНазадВозвращаетсяЗвонящемуДляДонастройки()
        {
            // Дом делает ФОРМУ, а место кнопки на экране — дело экрана: одна
            // стоит в шапке галереи, другая в углу таблицы рекордов. Верни он
            // void — и экрану пришлось бы искать кнопку заново или собирать
            // свою, то есть ровно та копия формы, ради ухода от которой дом и
            // заводился.
            var back = ScreenUi.BackButton(() => { }, 46f, 34f);

            Assert.IsNotNull(back, "дом не отдал кнопку — экрану нечего ставить на место");
            Assert.AreEqual("‹", back.text, "у кнопки «назад» пропал знак — гнездо стало пустым квадратом");

            back.style.marginLeft = 12f;   // звонящий продолжает её настраивать
            Assert.AreEqual(12f, back.style.marginLeft.value.value, 1e-4f);
        }

        [Test]
        public void РазмерИКегльКнопкиНазадБерутсяУЭкрана()
        {
            // Размер честно разный: на экране с крупной шапкой кнопка крупнее,
            // на тесном списке мельче. Зашей его в дом — и один из экранов
            // получит чужую кнопку: либо палец не попадёт по мелкой, либо
            // крупная перекроет заголовок.
            var мелкая = ScreenUi.BackButton(() => { }, 46f, 34f);
            var крупная = ScreenUi.BackButton(() => { }, 52f, 36f);

            Assert.AreEqual(46f, мелкая.style.width.value.value, 1e-4f,
                "размер кнопки взят не у экрана");
            Assert.AreEqual(52f, крупная.style.width.value.value, 1e-4f,
                "размер кнопки взят не у экрана");

            // Кегль идёт через оптическую поправку гарнитуры, как и вся
            // остальная типографика: минуй он её — знак «‹» поехал бы
            // относительно соседних надписей, стоило игроку сменить шрифт.
            Assert.AreEqual(LvnFonts.Size(34f), мелкая.style.fontSize.value.value, 1e-4f,
                "кегль знака взят не у экрана (или мимо поправки на гарнитуру)");
            Assert.Greater(крупная.style.fontSize.value.value, мелкая.style.fontSize.value.value,
                "экран попросил более крупный знак, а получил тот же");
        }

        [Test]
        public void ФормаКнопкиНазадОднаНаВсехЭкранах()
        {
            // Ловушка была ровно тут: вместе с размером каждый экран копировал
            // и форму — плашку, скругление, центровку знака. Разъезд такой
            // копии не видно, пока не сложишь файлы рядом, а игрок видит его
            // сразу: две кнопки «назад» в одном приложении выглядят по-разному.
            var a = ScreenUi.BackButton(() => { }, 46f, 34f);
            var b = ScreenUi.BackButton(() => { }, 52f, 36f);

            Assert.AreEqual(a.text, b.text, "знак «назад» разный на разных экранах");
            Assert.AreEqual(a.style.backgroundColor.value, b.style.backgroundColor.value,
                "плашка кнопки «назад» разная на разных экранах");
            Assert.AreEqual(a.style.borderTopLeftRadius.value.value,
                            b.style.borderTopLeftRadius.value.value, 1e-4f,
                "скругление кнопки «назад» разное на разных экранах");
            Assert.AreEqual(a.style.color.value, b.style.color.value,
                "цвет знака разный на разных экранах");

            foreach (var кнопка in new[] { a, b })
            {
                Assert.AreEqual(кнопка.style.width.value.value, кнопка.style.height.value.value, 1e-4f,
                    "гнездо под знак перестало быть квадратным — знак сядет не по центру");
                Assert.AreEqual(Align.Center, кнопка.style.alignItems.value,
                    "знак «‹» съехал с центра гнезда");
                Assert.AreEqual(Justify.Center, кнопка.style.justifyContent.value,
                    "знак «‹» съехал с центра гнезда");
                Assert.AreEqual(0f, кнопка.style.borderTopWidth.value, 1e-4f,
                    "у гнезда появилась рамка — на соседних экранах её нет");
            }
        }

        [Test]
        public void НажатиеКнопкиНазадВедётВПереданноеДействие()
        {
            // Кнопка «назад» — единственный выход с экрана галереи и таблицы
            // рекордов. Не дойди действие до нажатия — игрок останется на
            // экране, из которого некуда деться: не тупик в интерфейсе, а
            // тупик в игре.
            int закрыли = 0;
            var back = ScreenUi.BackButton(() => закрыли++, 46f, 34f);

            var ход = Обработчик(back);
            Assert.IsNotNull(ход, "нажатие по «назад» никуда не ведёт — с экрана не выйти");
            ход.Invoke();

            Assert.AreEqual(1, закрыли, "нажатие ушло не в то действие, что передал экран");
        }

        // ── вкладка хаба ────────────────────────────────────────────────────

        [Test]
        public void КореньВкладкиНеЛовитНажатия()
        {
            // Вкладка хаба — не окно поверх игры, а ещё одна вкладка той же
            // витрины: НИЖНЕЕ МЕНЮ живёт под ней и обязано нажиматься. Поймай
            // корень тап — и, открыв профиль, игрок больше не сможет уйти из
            // него по нижнему меню: экран съедает каждое касание, а выход из
            // раздела там единственный.
            var корень = new VisualElement();
            ScreenUi.HubTabSheet(корень, new VisualElement());

            Assert.AreEqual(PickingMode.Ignore, корень.pickingMode,
                "корень вкладки ловит нажатия — нижнее меню под ним перестало работать");
            Assert.AreEqual(Color.clear, корень.style.backgroundColor.value,
                "корень закрасил собой мир — вкладка стала модальным окном");
        }

        [Test]
        public void ЛистВкладкиОставляетВерхГероинеАНизМеню()
        {
            // Числа тут и есть решение (Илья, 26.08, «как гардероб»), и записаны
            // они были ДВАЖДЫ — в профиле и в лавке. Разъедься они молча, две
            // вкладки одной витрины получили бы разную высоту воздуха: это
            // читается как небрежность, а не как разные экраны.
            var лист = new VisualElement();
            ScreenUi.HubTabSheet(new VisualElement(), лист);

            Assert.AreEqual(Position.Absolute, лист.style.position.value,
                "лист встал в поток — верх экрана перестал быть воздухом");
            Assert.AreEqual(39f, лист.style.top.value.value, 1e-4f,
                "лист поднялся выше — он закрывает лицо героини");
            Assert.AreEqual(LengthUnit.Percent, лист.style.top.value.unit,
                "верх листа задан пикселями: на другом экране он уедет с лица героини");
            Assert.AreEqual(132f, лист.style.bottom.value.value, 1e-4f,
                "дырка под нижнее меню закрыта листом — по меню больше не попасть");
            Assert.AreEqual(LengthUnit.Pixel, лист.style.bottom.value.unit,
                "дырка под меню задана долей, а меню — фиксированной высоты");
            Assert.AreEqual(10f, лист.style.left.value.value, 1e-4f, "лист прижался к краю экрана");
            Assert.AreEqual(10f, лист.style.right.value.value, 1e-4f, "лист прижался к краю экрана");
        }

        [Test]
        public void УЛистаЕстьКрай_ИнажеЭтоТекстНаПолотне()
        {
            // Лист — единственный объект вкладки, которому позволена сильная
            // кромка: без неё содержимое читается как текст, наклеенный прямо
            // на сцену, и вкладка теряет границу вовсе. Тон полупрозрачный
            // намеренно — атмосфера меню обязана дышать сквозь него.
            var лист = new VisualElement();
            ScreenUi.HubTabSheet(new VisualElement(), лист);

            Assert.AreEqual(2f, лист.style.borderTopWidth.value, 1e-4f,
                "у листа пропала верхняя кромка — это «крышка», по ней и видно край вкладки");
            Assert.Greater(лист.style.borderTopColor.value.a, лист.style.borderLeftColor.value.a,
                "верхняя кромка сравнялась с боковыми — лист перестал читаться как лист");
            var тон = лист.style.backgroundColor.value;
            Assert.AreEqual(0.92f, тон.a, 1e-3f,
                "лист стал непрозрачным — атмосфера меню под ним погасла");
            Assert.Greater(лист.style.paddingTop.value.value, 0f, "внутри листа не осталось воздуха");
        }

        [Test]
        public void ВкладкаБезЛистаНеРоняетЭкран()
        {
            // Вкладку собирают по частям: лист приезжает вместе с данными, и до
            // них его нет. Урони это сборку — игрок остался бы не без листа, а
            // без всего раздела.
            Assert.DoesNotThrow(() => ScreenUi.HubTabSheet(new VisualElement(), null));
            Assert.DoesNotThrow(() => ScreenUi.HubTabSheet(null, new VisualElement()));
            Assert.DoesNotThrow(() => ScreenUi.HubTabSheet(null, null));
        }
    }
}
