using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// ОДНО ПРАВИЛО ПЛАВНОЙ СМЕНЫ ФОНА — и для картинки, и для живой
    /// текстуры. Живой фон вставал щелчком и снимался щелчком: «лента в
    /// гардеробе меняет фон резко» (Илья 18.09). Проверяется, что переход
    /// (слой bg-cross с прежним кадром) появляется на ЛЮБОЙ смене кадра при
    /// ненулевой длительности, и не появляется при нулевой.
    /// </summary>
    public class BackgroundCrossfadeTests
    {
        private GameObject _canvas;

        [SetUp]
        public void Up() { _canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas)); }

        [TearDown]
        public void Down() { if (_canvas != null) Object.DestroyImmediate(_canvas); }

        private static Sprite Pixel(Color c)
        {
            var t = new Texture2D(2, 2); t.SetPixels(new[] { c, c, c, c }); t.Apply();
            return Sprite.Create(t, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
        }

        private GameObject Cross() => GameObject.Find("bg-cross");

        [Test]
        public void ЖиваяТекстураПриходитИУходитПереходомКакКартинка()
        {
            var bg = new WorldBackground(_canvas.transform);
            bg.SetSprite(Pixel(Color.red), 0f);
            Assert.IsNull(Cross(), "мгновенная постановка — без слоя перехода");

            var live = new RenderTexture(4, 4, 0);
            live.Create();
            bg.SetLiveTexture(live, 0.3f);
            var cross = Cross();
            Assert.IsNotNull(cross, "картинка → живая текстура: прежний кадр растворяется");
            Assert.IsTrue(cross.activeSelf);
            Assert.AreEqual(live, bg.CurrentTexture, "на полотне — живая текстура");

            bg.SetLiveTexture(null, 0.3f);
            Assert.IsTrue(Cross().activeSelf, "живая → картинка, что ждала под ней: тоже переходом");
            Assert.AreNotEqual(live, bg.CurrentTexture, "полотно вернулось к картинке");
            Assert.IsNotNull(bg.CurrentTexture);

            bg.SetLiveTexture(live, 0f);
            live.Release();
        }

        [Test]
        public void НулеваяДлительностьСтавитЖивуюТекстуруМгновенно()
        {
            var bg = new WorldBackground(_canvas.transform);
            bg.SetSprite(Pixel(Color.blue), 0f);
            var live = new RenderTexture(4, 4, 0);
            live.Create();
            bg.SetLiveTexture(live, 0f);
            Assert.IsTrue(Cross() == null || !Cross().activeSelf, "ноль — кадр 3D-набора ставится как есть, без перехода");
            live.Release();
        }
    }
}
