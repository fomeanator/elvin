using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// A built 3D set standing in for painted art: the set is filmed off-screen
    /// and its frame becomes the background, so one room yields as many angles as
    /// the script asks for.
    public class Backdrop3DTests
    {
        // A bare hierarchy, not a primitive: creating a primitive drags in the
        // standard shader, and compiling it in a headless test run crashes the
        // editor. What we assert here is the backdrop's bookkeeping, not Unity's
        // ability to draw a cube.
        private static GameObject NewSet()
        {
            var go = new GameObject("test-set");
            var child = new GameObject("prop");
            child.transform.SetParent(go.transform, false);
            return go;
        }

        [Test]
        public void SetStands_AndIsFilmedIntoATexture()
        {
            var host = new GameObject("host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            var prefab = NewSet();

            backdrop.SetSet(prefab);

            Assert.IsTrue(backdrop.Active, "set is standing");
            // A headless run has no GPU to film into — the set still stands, and
            // that is what this asserts. With a display, a frame is allocated.
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.IsNotNull(backdrop.Texture, "the set is being filmed into a frame");
                Assert.Greater(backdrop.Texture.width, 0, "the frame has a size");
            }
        }

        [Test]
        public void SetIsBuiltFarFromTheScene_SoNothingElseSeesIt()
        {
            var host = new GameObject("host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            backdrop.SetSet(NewSet());

            var built = GameObject.Find("lvn-3d-set:test-set");
            Assert.IsNotNull(built, "the set was instantiated");
            Assert.Less(built.transform.position.y, -1000f,
                "built far below the scene: the stage camera must never catch it in frame");
        }

        [Test]
        public void CanvasActors_AreAlwaysASeparateSiblingAboveThe3DFrame()
        {
            var host = new GameObject("stage-host");
            var stage = new WorldStage(host.transform);
            stage.EnsureActor("hero");

            var gameRoot = stage.Root.transform.Find("game-root");
            var background = gameRoot.Find("bg");
            var content = gameRoot.Find("content");
            var actor = content.Find("vn-obj-hero");

            Assert.IsNotNull(background);
            Assert.IsNotNull(actor);
            Assert.Less(background.GetSiblingIndex(), content.GetSiblingIndex(),
                "the filmed 3D frame is background only; actors paint after it");
            Object.DestroyImmediate(host);
        }

        [Test]
        public void StageCanvasCamera_RendersAfterOtherScreenCameras()
        {
            var host = new GameObject("stage-host");
            var mainGo = new GameObject("Main Camera");
            var main = mainGo.AddComponent<Camera>();
            mainGo.tag = "MainCamera";
            main.depth = 0f;
            var contentCamera = new GameObject("content-camera").AddComponent<Camera>();
            contentCamera.depth = 7f;

            var stage = new WorldStage(host.transform);
            var canvas = stage.Root.GetComponent<Canvas>();

            Assert.IsNotNull(canvas.worldCamera);
            Assert.Greater(canvas.worldCamera.depth, contentCamera.depth,
                "the camera carrying bg + actors must paint after every content camera");

            stage.Dispose();
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(mainGo);
            Object.DestroyImmediate(contentCamera.gameObject);
        }

        [Test]
        public void RebuildAfterRelease_FilmsIntoABufferAgain_NeverOntoTheScreen()
        {
            // Живой сценарий бага: рисованный `bg` сносит сет (Release убивает
            // буфер), следующий `bg3d` строит сет заново — камера обязана снова
            // получить буфер. Камера без targetTexture рисует ПРЯМО В ЭКРАН и
            // накрывает сетом все спрайты сцены — так в бою пропали персонажи.
            var host = new GameObject("host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            backdrop.SetSet(NewSet());
            backdrop.Release();

            backdrop.SetSet(NewSet());

            Assert.IsTrue(backdrop.Active, "set stands again");
            var cam = backdrop.GetComponentInChildren<Camera>(true);
            Assert.IsNotNull(cam, "the camera survived the release");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.IsNotNull(backdrop.Texture, "the frame buffer is rebuilt");
                Assert.AreEqual(backdrop.Texture, cam.targetTexture,
                    "the camera films into the buffer — never straight onto the screen");
            }
        }

        [Test]
        public void ASetWithOnlyItsOwnEnvironmentCard_CountsAsStill()
        {
            // Lvn3DSetEnv — служебный компонент (небо/туман сета), а не анимация.
            // Считать его «движением» = снимать каждый статичный сет 60 раз в
            // секунду: на слабом устройстве это фризы по секунде.
            var host = new GameObject("host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            var prefab = NewSet();
            // На ребёнке, не на корне: SetAnimates обходит ВСЮ иерархию, а Apply()
            // (правка RenderSettings) в тестовой сцене редактора здесь ни к чему.
            prefab.transform.GetChild(0).gameObject.AddComponent<Lvn3DSetEnv>();

            backdrop.SetSet(prefab);

            var cam = backdrop.GetComponentInChildren<Camera>(true);
            Assert.IsNotNull(cam, "camera exists");
            Assert.IsFalse(cam.enabled,
                "a still set is filmed on demand — the camera must not run every frame");
        }

        [Test]
        public void FramingSnapsWithoutDuration_AndKeepsUnsetAxes()
        {
            var host = new GameObject("host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            backdrop.SetSet(NewSet());

            backdrop.Frame(1f, 2f, 3f, -10f, 45f, 50f, 0f);
            // Камера живёт на самом бэкдропе: он нарочно КОРНЕВОЙ объект, а не
            // ребёнок сцены — Canvas масштабирует детей под экран и ломал кадр.
            var cam = backdrop.GetComponentInChildren<Camera>();
            Assert.IsNotNull(cam, "the set has its own camera");
            Assert.AreEqual(50f, cam.fieldOfView, 0.01f, "field of view applied");
            var first = cam.transform.position;

            // Only the yaw moves: everything left unset must keep its value, or a
            // script that nudges one axis would silently reset the shot.
            backdrop.Frame(null, null, null, null, 90f, null, 0f);
            Assert.AreEqual(first, cam.transform.position, "position untouched by a yaw-only move");
            Assert.AreEqual(90f, cam.transform.rotation.eulerAngles.y, 0.1f, "yaw applied");
            Assert.AreEqual(50f, cam.fieldOfView, 0.01f, "field of view kept");
        }

        [Test]
        public void ReleasingTheSetFreesTheFrame()
        {
            var host = new GameObject("host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            backdrop.SetSet(NewSet());

            backdrop.Release();

            Assert.IsFalse(backdrop.Active, "no set standing");
            Assert.IsNull(backdrop.Texture, "the frame buffer is freed, not left dangling");
        }

        [Test]
        public void StandingASetWithNull_TearsItDown()
        {
            var host = new GameObject("host");
            var backdrop = Lvn3DBackdrop.Ensure(host.transform);
            backdrop.SetSet(NewSet());

            backdrop.SetSet(null);

            Assert.IsFalse(backdrop.Active, "null stands nothing — the scene goes back to flat art");
        }
    }
}
