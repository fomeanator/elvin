using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// Дома настроек: движковый дефолт работает без манифеста, манифест
    /// перекрывает его точечно, а общее правило существует в одном экземпляре.
    /// Тесты держат именно это — не конкретные числа, а контракт.
    public class StagingHomesTests
    {
        [TearDown]
        public void Restore()
        {
            LvnMotion.Tempo = 1f;
            LvnMenuStage.Apply(0.91f, 1f, 0.35f, 0.14f);
            LvnWardrobeStage.Apply(null);
        }

        [Test]
        public void Tempo_ScalesEveryDuration_AndClampsToSanity()
        {
            LvnMotion.Tempo = 1f;
            Assert.AreEqual(LvnMotion.Normal, LvnMotion.Ms(LvnMotion.Normal), "темп 1 ничего не меняет");

            LvnMotion.Tempo = 2f;
            Assert.AreEqual(LvnMotion.Normal * 2, LvnMotion.Ms(LvnMotion.Normal), "вдвое вальяжнее");
            Assert.AreEqual(0.5f, LvnMotion.Sec(0.25f), 1e-4f, "секунды идут тем же множителем");

            LvnMotion.Tempo = 99f;
            Assert.LessOrEqual(LvnMotion.Tempo, 4f, "нелепый темп обрезается, а не ломает экран");
            LvnMotion.Tempo = 0f;
            Assert.Greater(LvnMotion.Tempo, 0f, "ноль превратил бы движение в деление на ноль");
        }

        [Test]
        public void StageMotion_CombinesSceneTempoAndGlobalTempo()
        {
            float scale = VnTheme.MotionDurationScale;
            LvnMotion.Tempo = 1f;
            Assert.AreEqual(0.4f * scale, VnTheme.Motion(0.4f), 1e-4f, "темп сцены применён");

            LvnMotion.Tempo = 0.5f;
            Assert.AreEqual(0.4f * scale * 0.5f, VnTheme.Motion(0.4f), 1e-4f,
                "общая ручка действует поверх сценической");
            Assert.AreEqual(VnTheme.MotionMs(seconds: 0.4f), (int)System.Math.Round(VnTheme.Motion(0.4f) * 1000f),
                "миллисекунды — та же величина");
        }

        [Test]
        public void MenuPan_WalksTheCanvasInOrder_AndStaysOnIt()
        {
            LvnMenuStage.Apply(null, null, 0.35f, 0.14f);
            Assert.AreEqual(0.35f, LvnMenuStage.PanFor(0), 1e-4f, "первая вкладка — стартовая точка");
            Assert.AreEqual(0.49f, LvnMenuStage.PanFor(1), 1e-4f, "шаг за вкладку");
            Assert.Greater(LvnMenuStage.PanFor(3), LvnMenuStage.PanFor(2), "камера едет в одну сторону");
            Assert.AreEqual(LvnMenuStage.PanFor(3), LvnMenuStage.PanFor(9), 1e-4f,
                "за последнюю вкладку полотно не уезжает");
            Assert.LessOrEqual(LvnMenuStage.PanFor(3), 1f, "и не съезжает с картины");
        }

        [Test]
        public void MenuCanvas_FollowsTheRoomMap()
        {
            LvnMenuStage.PanSpread = 1f; LvnMenuStage.LiftSpread = 1f;
            // Карта Ильи (08.09): профиль слева сверху, главная в центре,
            // гардероб слева снизу, магазин справа снизу. Комнаты — в экранных
            // осях (y вниз), полотно — в осях картины (y вверх): к верхней
            // комнате камера показывает ВЕРХ картины.
            var profile  = LvnMenuStage.CanvasPointFor(0f,   0f);
            var home     = LvnMenuStage.CanvasPointFor(0.5f, 0.5f);
            var wardrobe = LvnMenuStage.CanvasPointFor(0f,   1f);
            var store    = LvnMenuStage.CanvasPointFor(1f,   1f);

            Assert.Less(wardrobe.x, home.x, "левая комната показывает левую часть картины");
            Assert.Less(home.x, store.x, "правая — правую");
            Assert.AreEqual(new Vector2(0.5f, 0.5f), home, "главная — центр полотна");
            Assert.Greater(profile.y, home.y, "верхняя комната поднимает кадр к верху картины");
            Assert.Less(wardrobe.y, home.y, "нижняя — опускает к низу");
            Assert.AreEqual(wardrobe.y, store.y, 1e-4f, "нижние комнаты на одной высоте");
            Assert.AreEqual(profile.x, wardrobe.x, 1e-4f, "профиль и гардероб — по одной вертикали");

            LvnMenuStage.PanSpread = 0f;
            Assert.AreEqual(0.5f, LvnMenuStage.CanvasPointFor(0f, 1f).x, 1e-4f,
                "нулевой размах — полотно стоит на месте");
            LvnMenuStage.PanSpread = 1f;
        }

        [Test]
        public void MenuHeroine_StandsInStageSlots_HomeLeftOfCentre_SidesCentred()
        {
            // МЕСТА ГЕРОИНИ — ТОЛЬКО СТОЯЧИЕ СЛОТЫ СЦЕНЫ. Своих долей у витрины
            // нет: доля из манифеста («0.32») не действует, слово-слот — да.
            string wasHome = LvnMenuStage.HomeDollSlot;
            try
            {
                foreach (var place in new[] { "0.32", "", null, "offscreen_left" })
                {
                    LvnMenuStage.HomeDollSlot = "center_left";
                    LvnMenuStage.Apply(null, null, null, null, place);
                    Assert.AreEqual("center_left", LvnMenuStage.HomeDollSlot,
                        $"«{place}» — не стоячий слот, витрина остаётся при своём");
                }
                LvnMenuStage.Apply(null, null, null, null, "left");
                Assert.AreEqual("left", LvnMenuStage.HomeDollSlot, "автор назвал слот — он и стоит");

                foreach (var home in new[] { "center_left", "left" })
                {
                    LvnMenuStage.HomeDollSlot = home;
                    float onHome = LvnMenuStage.DollSlotX(LvnMenuStage.Room.Home);
                    float onSide = LvnMenuStage.DollSlotX(LvnMenuStage.Room.Side);
                    Assert.Less(onHome, onSide, "с главной она уходит именно ВЛЕВО от центра");
                    Assert.AreEqual(0.5f, onSide, 1e-4f, "на боковых — центр кадра");
                    CollectionAssert.Contains(Placement.StandingSlotXs, onHome);
                    CollectionAssert.Contains(Placement.StandingSlotXs, onSide);
                }
            }
            finally { LvnMenuStage.HomeDollSlot = wasHome; }
        }

        [Test]
        public void MenuHeroine_InTheStore_StandsLeft_OnACloserPlan()
        {
            // Магазин — своя комната: героиня снова слева (справа товары, как
            // на главной карточки) и на пять сотых КРУПНЕЕ обычного плана
            // боковых («в магазине героиня наоборот слева и больше на 5
            // процентов, чем обычно» — Илья 08.09).
            Assert.AreEqual("left", LvnMenuStage.DollSlot(LvnMenuStage.Room.Store));
            Assert.AreEqual(LvnMenuStage.DollSlotX(LvnMenuStage.Room.Home),
                            LvnMenuStage.DollSlotX(LvnMenuStage.Room.Store), 1e-4f,
                            "в магазине она стоит там же, где на главной");
            Assert.IsTrue(Placement.IsStandingSlot(LvnMenuStage.StoreDollSlot), "слот магазина — стоячий слот сцены");

            float home = LvnMenuStage.CastZoomFor(LvnMenuStage.Room.Home);
            float store = LvnMenuStage.CastZoomFor(LvnMenuStage.Room.Store);
            Assert.AreEqual(1.05f, store / home, 1e-3f,
                "магазин крупнее ОБЫЧНОГО роста (того, что на главной) ровно на 5 %");

            // ГЛАВНАЯ — ПРИБЛИЖЕНИЕ, А НЕ ОТДАЛЕНИЕ. Витрина героиню НЕ
            // УМЕНЬШАЕТ: место ей освобождает композиция (панели столбиком
            // справа), а не масштаб. За день число прошло 0.9 → 1 → 1.15
            // («не уменьшать», «сделай приближение 15 процентов» — Илья 08.09);
            // страж держит смысл, а не сотые.
            float side = LvnMenuStage.CastZoomFor(LvnMenuStage.Room.Side);
            Assert.GreaterOrEqual(home, 1f, "витрина героиню не уменьшает");
            Assert.AreEqual(1.115f, home, 1e-3f, "на главной — приближение 1.15 минус 0.035");
            Assert.Greater(home, side, "на главной она ближе, чем в гардеробе, — так попросили");
        }

        [Test]
        public void MenuHeroine_OnHome_StandsATouchRightOfHerSlot()
        {
            // Слот остаётся словом сцены, а композиция главной просит чуть
            // правее («сдвинь вправо чуть героиню на 5 процентов» — Илья
            // 08.09): в позе живут оба поля — слово для переездов и арбитра,
            // число для точного места (у сцены x сильнее position).
            var pose = LvnPrima.Pose("v", "left", 1f, 1f, 0, nudge: LvnMenuStage.HomeDollNudge);
            Assert.AreEqual("left", (string)pose["position"], "слово-слот остаётся");
            Assert.AreEqual(Placement.SlotX("left") + LvnMenuStage.HomeDollNudge, (float)pose["x"], 1e-4f, "точное место — правее слота на сдвиг");
            Assert.IsNull(LvnPrima.Pose("v", "left", 1f, 1f, 0)["x"], "без сдвига числа нет — слот и только слот");

            Assert.AreEqual(0.12f, LvnMenuStage.DollNudge(LvnMenuStage.Room.Home), 1e-4f, "5 % + ещё 7 % — Илья 08.09");
            Assert.AreEqual(0f, LvnMenuStage.DollNudge(LvnMenuStage.Room.Store), "магазин стоит ровно в слоте");
            Assert.AreEqual(0f, LvnMenuStage.DollNudge(LvnMenuStage.Room.Side), "боковые — тоже");
            Assert.AreEqual(LvnMenuStage.DollSlotX(LvnMenuStage.Room.Home) + LvnMenuStage.HomeDollNudge,
                            LvnMenuStage.DollX(LvnMenuStage.Room.Home), 1e-4f);
        }

        [Test]
        public void SceneCleanup_KeepsWhatTheMenuShowsAfterEveryChapter()
        {
            // Уборка сцены отпускает пины кадра, но не то, что витрина покажет
            // сразу после главы: полотно, прогретый арт рамок, ядро створа,
            // текущий фон и облик хранимой героини. Всё прочее — отпускается.
            Assert.IsTrue(VnStage.SurvivesCleanup("menu-canvas", null), "полотно витрины");
            Assert.IsTrue(VnStage.SurvivesCleanup("menu-art", null), "арт витрины (рамки, нав, лого)");
            Assert.IsTrue(VnStage.SurvivesCleanup("portal-core", null), "ядро створа");
            Assert.IsTrue(VnStage.SurvivesCleanup("bg", null), "фон остаётся до нового bg");
            Assert.IsTrue(VnStage.SurvivesCleanup("actor:prima", "prima"), "хранимая героиня");
            Assert.IsFalse(VnStage.SurvivesCleanup("actor:prima", null), "без хранения — отпускается");
            Assert.IsFalse(VnStage.SurvivesCleanup("actor:mara", "prima"), "чужой актёр — отпускается");
        }

        [Test]
        public void MenuHeroine_MayBeCroppedByTheFrame_StoryActorsMayNot()
        {
            // Портрет витрины в 0.9 ширины экрана «слева» стоит только обрезом:
            // поза витрины разрешает его явно, а у актёра истории разрешения
            // нет — там зажим по-прежнему держит фигуру целиком в кадре.
            var doll = LvnPrima.Pose("v", "left", 1f, 1f, 0);
            Assert.IsTrue((bool)doll["crop"], "витрина разрешает обрез");
            Assert.IsTrue(VnStage.PlacementFrom(doll).Crop, "поле доходит до постановки");
            var story = new Newtonsoft.Json.Linq.JObject { ["op"] = "actor", ["id"] = "v", ["position"] = "left" };
            Assert.IsFalse(VnStage.PlacementFrom(story).Crop, "актёр истории без crop= не обрезается");
            CollectionAssert.Contains(VnStage.ReservedActorFields, "crop",
                "crop — поле постановки, а не ось каста");
        }

        [Test]
        public void WideFigure_MayCrossTheEdge_NarrowOneStaysInFrame()
        {
            // «Давай разрешим таким фигурам за край выходить» (Илья 08.09):
            // кукла витрины в 0.89 ширины кадра сбоку стоит только обрезом.
            // Узкий актёр истории без разрешения остаётся целиком в кадре.
            Assert.IsTrue(Lvn.UI.World.WorldStage.MayCrossEdge(false, 0.89f), "широкой — за край можно");
            Assert.IsFalse(Lvn.UI.World.WorldStage.MayCrossEdge(false, 0.5f), "узкую держим в кадре");
            Assert.IsTrue(Lvn.UI.World.WorldStage.MayCrossEdge(true, 0.5f), "…если автор не разрешил обрез явно");
            Assert.That(Lvn.UI.World.WorldStage.WideFigureShare, Is.InRange(0.6f, 0.9f),
                "порог «широкой» — между половиной и почти всем кадром");
        }

        [Test]
        public void CameraTween_StartsFromWhatIsOnScreen_NotFromTheOldTarget()
        {
            // Твин на середине пути от 0.9 к 1.0 показывает ~0.95; новый твин
            // обязан стартовать оттуда, а не с цели 1.0 (прыжок фигуры при
            // закрытии гардероба, 08.09).
            float mid = Lvn.UI.World.WorldCameraRig.Along(0.9f, 1f, 0.5f);
            Assert.Greater(mid, 0.9f); Assert.Less(mid, 1f);
            Assert.AreEqual(0.9f, Lvn.UI.World.WorldCameraRig.Along(0.9f, 1f, 0f), 1e-5f, "в начале — начало");
            Assert.AreEqual(1f,   Lvn.UI.World.WorldCameraRig.Along(0.9f, 1f, 1f), 1e-5f, "в конце — цель");
        }

        [Test]
        public void MenuHeroine_ArrivesWithTheFrame_NotBeforeIt()
        {
            // Заявленное сцене время проходит темп темы и укорачивание
            // мизансцены; витрина просит ЭКРАННЫЕ секунды и должна получить
            // ровно их — иначе героиня приезжает раньше интерфейса.
            float wanted = LvnMenuStage.TravelMs / 1000f;
            float declared = VnStage.DeclareMovement(wanted);
            Assert.Greater(declared, wanted, "заявляют больше, чем хотят увидеть");
            var pose = LvnPrima.Pose("v", "center", 1f, 1f, 0);
            pose["transition_duration"] = declared;
            var p = VnStage.PlacementFrom(pose);
            // тот же путь, что у команды: темп темы, затем укорачивание хода
            p.TransitionDuration = VnTheme.Motion(p.TransitionDuration) * 0.75f;
            Assert.AreEqual(Lvn.UI.LvnMotion.Sec(wanted), p.TransitionDuration, 1e-3f,
                "на экране движение длится столько, сколько просила витрина");
        }

        [Test]
        public void MenuStaging_ManifestOverridesOnlyWhatItNames()
        {
            LvnMenuStage.Apply(0.91f, 1f, 0.35f, 0.14f);
            LvnMenuStage.Apply(0.8f, null, null, null);
            Assert.AreEqual(0.8f, LvnMenuStage.DollHeight, 1e-4f, "названное — перекрыто");
            Assert.AreEqual(1f, LvnMenuStage.DollWidth, 1e-4f, "остальное осталось движковым");
            Assert.AreEqual(0.35f, LvnMenuStage.PanStart, 1e-4f);
        }

        [Test]
        public void WardrobeAxis_IsRecognisedByMeaning_InOnePlace()
        {
            Assert.AreEqual(LvnWardrobeAxisKind.Hair, LvnWardrobeStage.KindOf("hairstyle"));
            Assert.AreEqual(LvnWardrobeAxisKind.Hair, LvnWardrobeStage.KindOf("Причёска"));
            Assert.AreEqual(LvnWardrobeAxisKind.Decor, LvnWardrobeStage.KindOf("decor"));
            Assert.AreEqual(LvnWardrobeAxisKind.Decor, LvnWardrobeStage.KindOf("украшения"));
            Assert.AreEqual(LvnWardrobeAxisKind.Outfit, LvnWardrobeStage.KindOf("armor"),
                "незнакомая ось — вещь на корпусе: самый безобидный кадр");
            Assert.IsTrue(LvnWardrobeStage.IsHair("hair_color"));
            Assert.AreEqual(LvnIcon.Crown, LvnWardrobeStage.IconFor("hairstyle"));
            Assert.AreEqual(LvnIcon.Wardrobe, LvnWardrobeStage.IconFor("outfit"));
        }

        [Test]
        public void WardrobeFraming_HasSaneDefaults_AndBendsToTheManifest()
        {
            LvnWardrobeStage.Apply(null);
            var hair = LvnWardrobeStage.Framing("hairstyle");
            var outfit = LvnWardrobeStage.Framing("outfit");
            var all = LvnWardrobeStage.Framing(LvnWardrobeStage.AllAxis);
            Assert.Greater(hair.zoom, all.zoom, "причёску показываем крупнее общего плана");
            Assert.Less(hair.anchorY, outfit.anchorY, "голова выше корпуса");
            Assert.AreEqual(1f, LvnWardrobeStage.Framing("decor").zoom, 1e-4f,
                "украшения приходят кроп-иконками — приближать нечего");

            LvnWardrobeStage.Apply(new WardrobeConfig
            {
                framing = new Dictionary<string, FramingConfig>
                {
                    ["hairstyle"] = new FramingConfig { zoom = 2.2f },
                },
            });
            var tuned = LvnWardrobeStage.Framing("hairstyle");
            Assert.AreEqual(2.2f, tuned.zoom, 1e-4f, "новелла подвела кадр под свой арт");
            Assert.AreEqual(hair.anchorY, tuned.anchorY, 1e-4f,
                "не названное манифестом осталось движковым");
            Assert.AreEqual(outfit.zoom, LvnWardrobeStage.Framing("outfit").zoom, 1e-4f,
                "и соседняя ось не тронута");
        }
    }
}
