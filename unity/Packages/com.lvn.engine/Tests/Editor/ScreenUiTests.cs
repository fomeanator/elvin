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
