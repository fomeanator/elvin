using System;
using System.Reflection;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    public sealed class BootVeilLayoutTests
    {
        private static readonly Type Veil = typeof(NovelApp).Assembly.GetType("Lvn.UI.Screens.BootVeil");
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private VisualElement _root;
        private Func<float> _clock;
        private float _now;
        private bool _motion;

        private static void Call(string method, params object[] args) => Veil.GetMethod(method, Flags).Invoke(null, args);
        private static void Set(string field, object value) => Veil.GetField(field, Flags).SetValue(null, value);

        [SetUp] public void SetUp()
        {
            _clock = LvnClock.Wall;
            _motion = LvnPrefs.ReduceMotion;
            LvnPrefs.ReduceMotion = true;
            _now = 0f;
            LvnClock.Wall = () => _now;
            _root = new VisualElement();
            Set("_root", _root);
            Set("_target", 0f);
            Call("BuildLayout");
            Call("ResetPresentation");
        }

        [TearDown] public void TearDown()
        {
            LvnClock.Wall = _clock;
            LvnPrefs.ReduceMotion = _motion;
            Call("ResetPresentation");
            foreach (string field in new[] { "_root", "_identity", "_progress", "_brandTitle", "_pct", "_status", "_fill" })
                Set(field, null);
            Set("_target", 0f);
        }

        [Test] public void ProgressLivesOutsideTheTitleComposition()
        {
            var identity = _root.Q("boot-identity");
            Assert.IsNull(identity.Q("boot-progress"));
            Assert.AreEqual(Position.Absolute, identity.style.position.value);
            Assert.AreEqual(Position.Absolute, _root.Q("boot-work").style.position.value);
            Assert.IsNotNull(_root.Q<SafeAreaElement>());
        }

        [Test] public void TitleWrapsWithoutForcedSpacingOrMarkup()
        {
            Call("Splash", "Elemental Chronicles");
            var title = _root.Q<Label>("boot-title");
            Assert.AreEqual("Elemental Chronicles", title.text);
            Assert.IsFalse(title.enableRichText);
            Assert.AreEqual(WhiteSpace.Normal, title.style.whiteSpace.value);
            Assert.AreEqual(0f, title.style.letterSpacing.value.value);
            Assert.Greater(title.style.fontSize.value.value, _root.Q<Label>("boot-status").style.fontSize.value.value);
        }

        [Test] public void SlowBootRevealsProgressWithoutAnotherNetworkMilestone()
        {
            Call("Splash", "Хроники стихий");
            Assert.AreEqual(Visibility.Hidden, _root.Q("boot-progress").style.visibility.value);
            _now = 3.1f;
            Call("RevealIfWaiting");
            Assert.AreEqual(Visibility.Visible, _root.Q("boot-progress").style.visibility.value);
        }

        [Test] public void BrandHoldWorksEvenWhenTheClockStartsAtZero()
        {
            Call("Splash", "Elvin");
            Assert.IsTrue((bool)Veil.GetProperty("BrandHolding", Flags).GetValue(null));
            _now = 2.1f;
            Assert.IsFalse((bool)Veil.GetProperty("BrandHolding", Flags).GetValue(null));
        }

        [Test] public void CompletedBrandDoesNotBringBackTheProgressBar()
        {
            Call("Splash", "Elvin");
            Call("Brand", "Elvin");
            _now = 5f;
            Call("RevealIfWaiting");
            Assert.AreEqual(Visibility.Hidden, _root.Q("boot-progress").style.visibility.value);
        }

        [Test] public void ReducedMotionShowsIdentityImmediately()
        {
            Call("Splash", "Elvin");
            Assert.AreEqual(1f, _root.Q("boot-identity").style.opacity.value);
        }

        [Test] public void BlankProductNameFallsBackToTheEngineName()
        {
            Call("Splash", "  ");
            Assert.AreEqual(LvnEngine.Name, _root.Q<Label>("boot-title").text);
        }

        [Test] public void RestartClearsTheOldTitlePercentAndCreep()
        {
            Call("Splash", "Old title");
            Call("Progress", 72, "Old status");
            _now = 4f;
            Call("RevealIfWaiting");
            Call("ResetPresentation");
            Assert.AreEqual("", _root.Q<Label>("boot-title").text);
            Assert.AreEqual("0%", _root.Q<Label>("boot-percent").text);
            Assert.AreEqual(0f, _root.Q("boot-fill").style.width.value.value);
            Assert.AreEqual(0f, (float)Veil.GetField("_creepCeil", Flags).GetValue(null));
        }
    }
}
