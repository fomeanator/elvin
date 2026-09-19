using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    // Runs in the game project, which includes the optional spine-unity package.
    // TestHost deliberately has no proprietary runtime; the regular lifecycle
    // tests there still run independently of this real-mesh rendering check.
    [Category("LvnExternalContent")]
    public class SpinePosterIntegrationTests
    {
        private GameObject _document;
        private PanelSettings _settings;
        private VisualElement _host, _screen;
        private readonly List<Object> _assets = new List<Object>();
        private RenderTexture _poster;
        private const string Json = "{\"skeleton\":{\"hash\":\"test\",\"spine\":\"4.2.00\",\"x\":-50,\"y\":-50,\"width\":100,\"height\":100},\"bones\":[{\"name\":\"root\"}],\"slots\":[{\"name\":\"square\",\"bone\":\"root\",\"attachment\":\"square\"}],\"skins\":[{\"name\":\"default\",\"attachments\":{\"square\":{\"square\":{\"width\":100,\"height\":100}}}}],\"animations\":{\"idle\":{}}}";
        private const string Atlas = "poster-test.png\nsize: 2,2\nfilter: Linear,Linear\nrepeat: none\nsquare\n  rotate: false\n  xy: 0,0\n  size: 2,2\n  orig: 2,2\n  offset: 0,0\n  index: -1\n";

        [SetUp] public void SetUp()
        {
            if (!LvnSpineBridge.Available) Assert.Ignore("Requires the game project's spine-unity package.");
            LvnSpineBridge.ClearCache?.Invoke();
            _document = new GameObject("real-spine-panel", typeof(UIDocument));
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _document.GetComponent<UIDocument>().panelSettings = _settings;
            _screen = new VisualElement();
            _screen.style.width = 300; _screen.style.height = 300;
            _document.GetComponent<UIDocument>().rootVisualElement.Add(_screen);
            _host = new VisualElement(); _host.style.width = 100; _host.style.height = 100;
            _screen.Add(_host);
        }

        [TearDown] public void TearDown()
        {
            if (_document != null) _document.GetComponent<UIDocument>().rootVisualElement.Clear();
            Object.Destroy(_document); Object.Destroy(_settings);
            LvnSpineBridge.ClearCache?.Invoke();
            foreach (var asset in _assets) Object.Destroy(asset);
            _assets.Clear(); _poster = null;
        }

        private Sprite Sprite(Texture2D texture)
        {
            _assets.Add(texture);
            var sprite = UnityEngine.Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.one * .5f);
            _assets.Add(sprite); return sprite;
        }
        private static IEnumerator Settle()
        {
            for (int i = 0; i < 30; i++) yield return null;
            yield return new WaitForSecondsRealtime(.3f);
        }
        private void AssertMeshVisible(bool red)
        {
            Assert.IsNotNull(_poster, "poster must finish building");
            var old = RenderTexture.active;
            var readback = new Texture2D(_poster.width, _poster.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = _poster;
                readback.ReadPixels(new Rect(0, 0, _poster.width, _poster.height), 0, 0); readback.Apply();
                int opaque = 0;
                foreach (var p in readback.GetPixels32()) if (p.a > 100 && (!red || p.r > 180 && p.g < 60)) opaque++;
                Assert.Greater(opaque, _poster.width * _poster.height / 4, "real skeleton must cover the transparent camera background");
            }
            finally { RenderTexture.active = old; Object.Destroy(readback); }
        }

        [UnityTest] public IEnumerator RealMeshSurvivesPauseAndResume()
        {
            var texture = new Texture2D(2, 2); texture.SetPixels(new[] { Color.red, Color.red, Color.red, Color.red }); texture.Apply();
            var sprite = Sprite(texture);
            var spine = new LvnSpineRef { json = "test.json", atlas = "test.atlas", auto = "idle", scale = 1 };
            yield return Settle();
            LvnSpinePoster.Attach(_host, spine, url => Task.FromResult(url.EndsWith(".atlas") ? Atlas : Json),
                url => Task.FromResult(sprite), onPoster: rt => _poster = rt);
            yield return Settle(); AssertMeshVisible(true);
            var retained = _poster;
            _screen.style.display = DisplayStyle.None; yield return Settle();
            _screen.style.display = DisplayStyle.Flex; yield return Settle();
            Assert.AreSame(retained, _poster); AssertMeshVisible(true);
            LvnSpineBridge.ClearCache?.Invoke(); // live manifest refresh / stage destruction
            yield return Settle(); AssertMeshVisible(true);
        }

        [UnityTest] public IEnumerator WarmProductionPosterRenders()
        {
            var path = Environment.GetEnvironmentVariable("LVN_SPINE_TEST_JSON");
            if (string.IsNullOrEmpty(path)) Assert.Ignore("Set LVN_SPINE_TEST_JSON to a local production skeleton.");
            var spine = new LvnSpineRef { json = path, atlas = Path.ChangeExtension(path, ".atlas.txt"), scale = 1 };
            var background = Path.Combine(Path.GetDirectoryName(path), "back.jpg");
            if (File.Exists(background)) spine.bg = background;
            var sprites = new Dictionary<string, Sprite>();
            Task<string> Text(string url) => Task.FromResult(File.ReadAllText(url));
            Task<Sprite> Load(string url)
            {
                if (!sprites.TryGetValue(url, out var sprite))
                {
                    var texture = new Texture2D(2, 2); texture.LoadImage(File.ReadAllBytes(url));
                    sprites[url] = sprite = Sprite(texture);
                }
                return Task.FromResult(sprite);
            }
            yield return Settle();
            var warm = LvnSpinePoster.WarmAsync(spine, Text, Load);
            while (!warm.IsCompleted) yield return null;
            Assert.IsFalse(warm.IsFaulted, warm.Exception?.ToString());
            LvnSpinePoster.Attach(_host, spine, Text, Load, onPoster: rt => _poster = rt);
            yield return Settle(); AssertMeshVisible(false);
            _screen.style.display = DisplayStyle.None; yield return Settle();
            _screen.style.display = DisplayStyle.Flex; yield return Settle(); AssertMeshVisible(false);
        }
    }
}
