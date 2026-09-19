using System;
using System.Collections;
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
    public class PosterAnimationProbe : MonoBehaviour
    {
        public int Updates;
        public int LastLateFrame = -1;
        private void Update() => Updates++;
        private void LateUpdate() => LastLateFrame = Time.frameCount;
    }

    public class SpinePosterVisibilityTests
    {
        private GameObject _document;
        private PanelSettings _settings;
        private VisualElement _root, _screen, _host;
        private Texture2D _texture;
        private Sprite _sprite;
        private RenderTexture _poster;
        private PosterAnimationProbe _animation;
        private Camera _camera;
        private int _renders;
        private int _rendersBeforeAnimation;
        private int _builds;
        private Func<RectTransform, string, string, Texture2D[], float, Texture2D, GameObject> _create;
        private Action<GameObject, bool> _visible;
        private Action<GameObject, float, string> _refit;

        [SetUp] public void SetUp()
        {
            _renders = 0; _rendersBeforeAnimation = 0; _builds = 0;
            _create = LvnSpineBridge.Create; _visible = LvnSpineBridge.SetVisible; _refit = LvnSpineBridge.Refit;
            LvnSpineBridge.SetVisible = null; LvnSpineBridge.Refit = null;
            LvnSpineBridge.Create = (parent, json, atlas, textures, scale, bg) =>
            {
                _builds++;
                var go = new GameObject("poster-animation", typeof(RectTransform), typeof(PosterAnimationProbe));
                go.transform.SetParent(parent, false);
                _animation = go.GetComponent<PosterAnimationProbe>();
                _camera = parent.parent.GetComponentInChildren<Camera>();
                _camera.backgroundColor = Color.red;
                return go;
            };
            _texture = new Texture2D(2, 2);
            _sprite = Sprite.Create(_texture, new Rect(0, 0, 2, 2), Vector2.one * .5f);
            _document = new GameObject("poster-panel", typeof(UIDocument));
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _document.GetComponent<UIDocument>().panelSettings = _settings;
            _root = _document.GetComponent<UIDocument>().rootVisualElement;
            _screen = Box(300, 300); _root.Add(_screen);
            _host = Box(80, 100); _screen.Add(_host);
            Camera.onPostRender += OnRender;
        }

        [TearDown] public void TearDown()
        {
            Camera.onPostRender -= OnRender;
            _root?.Clear();
            LvnSpineBridge.Create = _create; LvnSpineBridge.SetVisible = _visible; LvnSpineBridge.Refit = _refit;
            Object.Destroy(_document); Object.Destroy(_settings);
            Object.Destroy(_sprite); Object.Destroy(_texture);
        }

        private void OnRender(Camera camera)
        {
            if (camera != _camera) return;
            _renders++;
            if (_animation != null && _animation.LastLateFrame != Time.frameCount)
                _rendersBeforeAnimation++;
        }
        private static VisualElement Box(float width, float height)
        {
            var box = new VisualElement();
            box.style.width = width; box.style.height = height;
            box.style.flexShrink = 0;
            return box;
        }
        private void Attach(VisualElement visibleConsumer = null)
            => LvnSpinePoster.Attach(_host, new LvnSpineRef { json = "skeleton.json", atlas = "skeleton.atlas" },
                url => Task.FromResult(url.EndsWith(".atlas") ? "page.png\nsize: 2,2\n" : "{}"),
                url => Task.FromResult(_sprite), onPoster: rt => _poster = rt, visibilityTarget: visibleConsumer);
        private static IEnumerator Settle()
        {
            for (int i = 0; i < 5; i++) yield return null;
            yield return new WaitForSecondsRealtime(.12f);
        }

        [UnityTest] public IEnumerator PosterRendersAfterAnimationLateUpdate()
        {
            yield return Settle(); Attach(); yield return Settle();
            // A component added mid-frame gets its first Update next frame.
            // Measure the established animation loop, not that initial draw.
            _renders = 0; _rendersBeforeAnimation = 0;
            yield return Settle();
            Assert.Greater(_renders, 0, "positive control: camera really submitted frames");
            Assert.AreEqual(0, _rendersBeforeAnimation,
                "posters must not sample the previous animation frame from inside LateUpdate");
        }

        [UnityTest] public IEnumerator RefreshingSamePosterKeepsOneRig()
        {
            yield return Settle(); Attach(); yield return Settle();
            var camera = _camera; var texture = _poster;
            for (int i = 0; i < 12; i++) Attach();
            yield return Settle();
            Assert.AreEqual(1, _builds, "menu refresh must not accumulate invisible duplicate cameras");
            Assert.AreSame(camera, _camera); Assert.AreSame(texture, _poster);
            _host.RemoveFromHierarchy(); yield return Settle();
            Assert.IsTrue(camera == null); Assert.IsTrue(texture == null);
        }

        [UnityTest] public IEnumerator SupersededLoadCannotOverwriteNewPoster()
        {
            yield return Settle();
            var slow = new TaskCompletionSource<string>();
            LvnSpinePoster.Attach(_host, new LvnSpineRef { json = "slow.json", atlas = "slow.atlas" },
                url => url.EndsWith(".json") ? slow.Task : Task.FromResult("page.png\nsize: 2,2\n"),
                url => Task.FromResult(_sprite), onPoster: _ => Assert.Fail("stale poster won the race"));
            Attach(); yield return Settle();
            var texture = _poster; slow.SetResult("{}"); yield return Settle();
            Assert.AreEqual(1, _builds); Assert.AreSame(texture, _poster);
        }

        [UnityTest] public IEnumerator ReplacingPosterReleasesOldRig()
        {
            yield return Settle(); Attach(); yield return Settle();
            var oldCamera = _camera; var oldTexture = _poster;
            LvnSpinePoster.Attach(_host, new LvnSpineRef { json = "other.json", atlas = "other.atlas" },
                url => Task.FromResult(url.EndsWith(".atlas") ? "page.png\nsize: 2,2\n" : "{}"),
                url => Task.FromResult(_sprite), onPoster: rt => _poster = rt);
            yield return Settle();
            Assert.AreEqual(2, _builds); Assert.IsTrue(oldCamera == null); Assert.IsTrue(oldTexture == null);
            AssertRenderedRed(_poster);
        }

        [UnityTest] public IEnumerator FailedBuildReleasesPartialRigAndShowsFallback()
        {
            yield return Settle();
            bool fallback = false;
            LvnSpineBridge.Create = (parent, json, atlas, textures, scale, bg) =>
            {
                _camera = parent.parent.GetComponentInChildren<Camera>();
                throw new NullReferenceException("poster regression probe");
            };
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("\\[lvn-async\\].*SpinePoster.*poster regression probe"));
            LvnSpinePoster.Attach(_host, new LvnSpineRef { json = "test.json", atlas = "test.atlas" },
                url => Task.FromResult(url.EndsWith(".atlas") ? "page.png\nsize: 2,2\n" : "{}"),
                url => Task.FromResult(_sprite), onFallback: () => fallback = true);
            yield return Settle();
            Assert.IsTrue(fallback); Assert.IsTrue(_camera == null, "exception must not leak a live camera");
        }

        private sealed class PinLedger : ILvnPinLedger
        {
            internal int Balance;
            public void PinSprite(Sprite sprite, bool pinned) => Balance += pinned ? 1 : -1;
        }

        [UnityTest] public IEnumerator AtlasStaysPinnedWhileBackgroundLoadsAndDetachBalancesPins()
        {
            yield return Settle();
            var ledger = new PinLedger();
            var background = new TaskCompletionSource<Sprite>();
            bool pageWasPinned = false;
            Task<Sprite> Load(string url)
            {
                if (url == "back.jpg") { pageWasPinned = ledger.Balance > 0; return background.Task; }
                return Task.FromResult(_sprite);
            }
            LvnSpinePoster.Attach(_host, new LvnSpineRef { json = "test.json", atlas = "test.atlas", bg = "back.jpg" },
                url => Task.FromResult(url.EndsWith(".atlas") ? "page.png\nsize: 2,2\n" : "{}"), Load, ledger);
            Assert.IsTrue(pageWasPinned, "the background download must not evict the atlas page");
            _host.RemoveFromHierarchy();
            background.SetResult(_sprite); yield return Settle();
            Assert.AreEqual(0, _builds, "detached in-flight request must not create a rig");
            Assert.AreEqual(0, ledger.Balance, "cancelled load releases every temporary page pin");
        }

        [UnityTest] public IEnumerator HiddenScreenStopsActualCameraAndAnimationThenResumesSameTexture()
        {
            yield return Settle(); Attach(); yield return Settle();
            Assert.Greater(_renders, 0, "positive control: real camera renders");
            Assert.Greater(_animation.Updates, 0);
            var rt = _poster;
            Assert.IsTrue(rt.IsCreated());
            _screen.style.display = DisplayStyle.None;
            yield return Settle();
            int renders = _renders, updates = _animation.Updates;
            Assert.IsNotNull(_host.panel, "hidden screens remain attached: reproduce the bug");
            yield return Settle();
            Assert.AreEqual(renders, _renders, "hidden camera must do zero work");
            Assert.AreEqual(updates, _animation.Updates, "hidden skeleton must not animate");
            _screen.style.display = DisplayStyle.Flex;
            yield return Settle();
            Assert.Greater(_renders, renders); Assert.Greater(_animation.Updates, updates);
            Assert.AreSame(rt, _poster, "return does not rebuild or lose the poster");
            AssertRenderedRed(rt);
            _screen.RemoveFromHierarchy(); yield return Settle();
            Assert.IsTrue(_camera == null, "detach releases the rig");
            Assert.IsTrue(rt == null, "detach releases its texture");
        }

        [UnityTest] public IEnumerator ClippedAndTransparentCardsPauseAndRecover()
        {
            _screen.style.width = 100;
            _screen.style.overflow = Overflow.Hidden;
            _host.style.position = Position.Absolute;
            yield return Settle(); Attach(); yield return Settle();
            Assert.Greater(_renders, 0);
            _host.style.left = 400;
            yield return Settle(); int before = _renders;
            yield return Settle(); Assert.AreEqual(before, _renders, "outside scroll viewport");
            _host.style.left = 80; // partially visible: must draw, not disappear at the edge
            yield return Settle();
            Assert.Greater(_renders, before, $"partial card={_host.worldBound}, clip={_screen.worldBound}, panel={_root.panel.visualTree.worldBound}");
            _screen.style.opacity = 0;
            yield return Settle(); before = _renders;
            yield return Settle(); Assert.AreEqual(before, _renders, "transparent ancestor");
            _screen.style.opacity = 1;
            yield return Settle(); Assert.Greater(_renders, before);
        }

        [UnityTest] public IEnumerator SharedHiddenMasterUsesConsumerVisibility()
        {
            // Same contract as PackShopScreen: a hidden master lends its RT to visible cards.
            _host.style.visibility = Visibility.Hidden;
            var consumer = Box(80, 100); _screen.Add(consumer);
            yield return Settle(); Attach(_screen); yield return Settle();
            consumer.style.backgroundImage = Background.FromRenderTexture(_poster);
            Assert.Greater(_renders, 0, "intentional hidden master still feeds visible shop");
            AssertRenderedRed(_poster);
            _screen.style.display = DisplayStyle.None;
            yield return Settle(); int before = _renders;
            yield return Settle(); Assert.AreEqual(before, _renders);
            _screen.style.display = DisplayStyle.Flex;
            yield return Settle(); Assert.Greater(_renders, before);
            AssertRenderedRed(_poster);
        }

        [UnityTest] public IEnumerator ScrollViewportStopsOffscreenCardWithoutDetachingIt()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.width = 200; scroll.style.height = 150;
            _screen.Add(scroll);
            _host.RemoveFromHierarchy(); scroll.Add(_host); scroll.Add(Box(80, 500));
            yield return Settle(); Attach(); yield return Settle();
            Assert.Greater(_renders, 0);
            scroll.scrollOffset = new Vector2(0, 250);
            yield return Settle(); int before = _renders;
            Assert.IsNotNull(_host.panel);
            yield return Settle(); Assert.AreEqual(before, _renders);
            scroll.scrollOffset = Vector2.zero;
            yield return Settle(); Assert.Greater(_renders, before);
        }

        [UnityTest] public IEnumerator VisibilityChecksDoNotAllocatePerFrame()
        {
            yield return Settle();
            Assert.IsTrue(LvnSpinePoster.IsVisible(_host));
            using var allocations = Unity.Profiling.ProfilerRecorder.StartNew(
                Unity.Profiling.ProfilerCategory.Memory, "GC.Alloc", 8192,
                Unity.Profiling.ProfilerRecorderOptions.StartImmediately
                | Unity.Profiling.ProfilerRecorderOptions.CollectOnlyOnCurrentThread
                | Unity.Profiling.ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
            int control = allocations.Count;
            var bytes = new byte[4096];
            Assert.Greater(allocations.Count, control, "positive allocation control");
            GC.KeepAlive(bytes);
            int before = allocations.Count;
            bool visible = true;
            for (int i = 0; i < 1000; i++) visible &= LvnSpinePoster.IsVisible(_host);
            int allocated = allocations.Count - before;
            Assert.IsTrue(visible); Assert.AreEqual(0, allocated);
        }

        private static void AssertRenderedRed(RenderTexture rt)
        {
            var previous = RenderTexture.active;
            var pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                pixel.ReadPixels(new Rect(rt.width / 2, rt.height / 2, 1, 1), 0, 0); pixel.Apply();
                Assert.Greater(pixel.GetPixel(0, 0).r, .9f, "actual render texture has the expected image");
                Assert.Less(pixel.GetPixel(0, 0).g, .1f);
            }
            finally { RenderTexture.active = previous; Object.Destroy(pixel); }
        }
    }
}
