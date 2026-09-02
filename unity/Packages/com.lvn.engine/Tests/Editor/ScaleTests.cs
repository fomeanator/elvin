using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests.Editor
{
    /// <summary>
    /// РОСТОМЕР: рост в метрах — одна мера на всех, кто ставит фигуру.
    ///
    /// <para>«Рост героини скачет — нужна универсальная шкала: потолок 2 метра,
    /// героиня 1.7» (Илья, 27.08). Скакал он потому, что рост был долей экрана,
    /// и долю называл каждый ставящий по-своему. Эти проверки держат главное:
    /// из метров получается одна и та же высота, кто бы ни ставил, и разница
    /// между героями настоящая.</para>
    /// </summary>
    public class ScaleTests
    {
        private float _saved;

        [SetUp]
        public void SetUp() { _saved = LvnScale.SceneMeters; LvnScale.SceneMeters = 2f; }

        private readonly Мусор _мусор = new Мусор();

        [TearDown]
        public void TearDown()
        {
            LvnScale.SceneMeters = _saved;
            _мусор.Убрать();
        }

        // Слот сразу берётся на учёт: упавшее утверждение оставляло его жить, а
        // сцена у тестов редактора общая.
        private RectTransform Slot()
            => _мусор.Беречь(new GameObject("t-slot", typeof(RectTransform))).GetComponent<RectTransform>();

        // Кадр 1080×1920, потолок 2 м: героиня 1.7 м занимает 1.7/2 высоты.
        [Test]
        public void HeightComesFromMetres()
        {
            var slot = Slot();
            var p = Placement.Standing(0.5f);
            p.Meters = 1.7f;
            WorldPlacement.Apply(slot, p, new Vector2(1080f, 1920f));

            Assert.AreEqual(1920f * (1.7f / 2f), slot.sizeDelta.y, 1f,
                "рост посчитан не по шкале мира");
        }

        // Тот же человек, поставленный тремя разными w=/h= (сценарий, меню,
        // гардероб), обязан выйти одного роста — ровно это и скакало.
        [Test]
        public void EveryoneWhoStagesHerGetsTheSameHeight()
        {
            var size = new Vector2(1080f, 1920f);
            float Height(float? w, float? h)
            {
                var slot = Slot();
                var p = Placement.Standing(0.5f);
                p.Width = w; p.Height = h; p.Meters = 1.7f;
                WorldPlacement.Apply(slot, p, size);
                float y = slot.sizeDelta.y;
                return y;
            }

            float script = Height(0.69f, 0.93f);   // постановка сценария
            float menu = Height(0.92f, 1.06f);     // витрина меню
            float wardrobe = Height(0.5f, 0.7f);   // гардероб

            Assert.AreEqual(script, menu, 0.5f, "меню ставит её другого роста");
            Assert.AreEqual(script, wardrobe, 0.5f, "гардероб ставит её другого роста");
        }

        // Двадцать сантиметров разницы обязаны быть видны как двадцать
        // сантиметров, а не как разница в полях чужих png.
        [Test]
        public void TallerPersonIsTallerByTheRightAmount()
        {
            var size = new Vector2(1080f, 1920f);
            float Height(float meters, float figureH)
            {
                var slot = Slot();
                var p = Placement.Standing(0.5f);
                p.Meters = meters;
                p.ContentX = 0f; p.ContentY = 0f; p.ContentW = 1f; p.ContentH = figureH;
                WorldPlacement.Apply(slot, p, size);
                float figure = slot.sizeDelta.y * p.FigureH; // на экране видно фигуру
                return figure;
            }

            // У одного художник оставил четверть холста воздухом, у другого — нет.
            float her = Height(1.70f, 0.75f);
            float him = Height(1.90f, 1.00f);

            Assert.AreEqual(1.90f / 1.70f, him / her, 0.01f,
                "рост считается по холсту, а не по фигуре — воздух в png снова решает");
        }

        // Шкала сцены — это камера: 1.5 м в кадре значит, что все крупнее.
        [Test]
        public void SceneMetresAreTheCamera()
        {
            var size = new Vector2(1080f, 1920f);
            var slot = Slot();
            var p = Placement.Standing(0.5f);
            p.Meters = 1.7f;

            LvnScale.SceneMeters = 2f;
            WorldPlacement.Apply(slot, p, size);
            float far = slot.sizeDelta.y;

            LvnScale.SceneMeters = 1.5f;
            WorldPlacement.Apply(slot, p, size);
            float near = slot.sizeDelta.y;

            Assert.Greater(near, far, "камера подъехала, а фигура не выросла");
            Assert.AreEqual(2f / 1.5f, near / far, 0.01f);
        }

        // Роста нет — всё как раньше: доли экрана и тема. Шкала включается
        // данными и не трогает новеллы, которые её не объявили.
        [Test]
        public void WithoutMetresNothingChanges()
        {
            var slot = Slot();
            var p = Placement.Standing(0.5f);
            p.Height = 0.93f;
            WorldPlacement.Apply(slot, p, new Vector2(1080f, 1920f));

            Assert.AreEqual(1920f * 0.93f, slot.sizeDelta.y, 0.5f,
                "новелла без шкалы получила чужой рост");
        }
    }
}
