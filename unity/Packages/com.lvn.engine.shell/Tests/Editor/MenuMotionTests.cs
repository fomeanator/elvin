using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ДВИЖЕНИЕ В МЕНЮ ЗАКРЕПЛЕНО (TR-65). В витрине ровно два способа сменить
    /// экран: комната ПЕРЕЕЗЖАЕТ и лист ВСПЛЫВАЕТ над ней. Пока у них были
    /// независимые числа (перелёт 680 мс, лист 200 мс), игрок видел в одном
    /// меню два несвязанных движения, а правка одного молча ссорила его со
    /// вторым.
    /// </summary>
    public class MenuMotionTests
    {
        /// <summary>Экран-пустышка: длительность у него та же, что у всех
        /// листов оболочки, — её и проверяем.</summary>
        private sealed class Sheet : LvnOverlayScreen
        {
            public float Seconds => FadeSecondsForTest;
        }

        [TearDown]
        public void Reset() => LvnMenuStage.Apply(new BrowseConfig());

        /// <summary>Всплытие выводится из перелёта: назвали одно — второе
        /// встало само.</summary>
        [Test]
        public void TheRiseFollowsTheTravel()
        {
            LvnMenuStage.Apply(new BrowseConfig { menu_travel_ms = 1000 });
            Assert.AreEqual(1000, LvnMenuStage.TravelMs);
            Assert.AreEqual(450, LvnMenuStage.RiseMs, "всплытие не пошло за перелётом");

            LvnMenuStage.Apply(new BrowseConfig());
            Assert.AreEqual(680, LvnMenuStage.TravelMs, "перелёт не вернулся к движковому");
            Assert.AreEqual(306, LvnMenuStage.RiseMs, "всплытие не вернулось к доле перелёта");
        }

        /// <summary>Автор вправе назвать всплытие своим числом — тогда доля не
        /// применяется.</summary>
        [Test]
        public void TheAuthorMayNameTheRise()
        {
            LvnMenuStage.Apply(new BrowseConfig { menu_travel_ms = 1000, menu_rise_ms = 120 });
            Assert.AreEqual(120, LvnMenuStage.RiseMs);
        }

        /// <summary>Лист берёт время у витрины, а не у своей константы: иначе
        /// правка перелёта в манифесте его не догонит.</summary>
        [Test]
        public void ASheetRisesWithTheShowcaseTiming()
        {
            LvnMenuStage.Apply(new BrowseConfig { menu_travel_ms = 900 });
            var sheet = new Sheet();
            Assert.AreEqual(LvnMenuStage.RiseMs / 1000f, sheet.Seconds, 0.0001f,
                "лист живёт своим временем — в меню снова два разных движения");
        }

        /// <summary>
        /// РАССТАНОВКА КОМНАТ — АВТОРСКАЯ (TR-63). Полотно нарисовано под свою
        /// карту: держать её числами в коде значит требовать сборки ради
        /// композиции чужой игры.
        /// </summary>
        [Test]
        public void TheNovelMayRedrawTheRoomMap()
        {
            var was = LvnTabs.Rooms;
            try
            {
                LvnTabs.Rooms = null;
                Assert.AreEqual(new UnityEngine.Vector2(0f, 1f), LvnTabs.Room(LvnTabs.Wardrobe),
                    "без карты новеллы комнаты стоят движковым ромбом");

                LvnTabs.Rooms = LvnTabs.RoomsOf(new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<float>>
                {
                    ["wardrobe"] = new System.Collections.Generic.List<float> { 1f, 0f },
                    ["broken"] = new System.Collections.Generic.List<float> { 1f },   // половина записи
                });
                Assert.AreEqual(new UnityEngine.Vector2(1f, 0f), LvnTabs.Room(LvnTabs.Wardrobe),
                    "карта новеллы не перебила движковую точку");
                Assert.AreEqual(new UnityEngine.Vector2(1f, 1f), LvnTabs.Room(LvnTabs.Store),
                    "комната без своей точки должна остаться на движковой");
            }
            finally { LvnTabs.Rooms = was; }
        }
    }
}
