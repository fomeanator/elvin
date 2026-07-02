using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class LvnPrefsTests
    {
        [TearDown]
        public void ResetStatics()
        {
            // Static prefs leak between tests — put the knobs back to defaults.
            LvnPrefs.TextSpeed = 1f;
            LvnPrefs.AutoAdvance = false;
            LvnPrefs.AutoDelayScale = 1f;
            LvnPrefs.VolMusic = 1f;
            LvnPrefs.ReduceMotion = false;
            LvnPrefs.DialogOpacity = 1f;
            TypewriterClock.UserSpeedMultiplier = 1f;
            TypewriterClock.GlobalCps = 0f;
        }

        [Test]
        public void UserSpeedMultiplierScalesTypewriter()
        {
            TypewriterClock.GlobalCps = 0f;
            TypewriterClock.UserSpeedMultiplier = 1f;
            float baseProgress = TypewriterClock.Progress(1f, 40f);

            TypewriterClock.UserSpeedMultiplier = 2f;
            Assert.AreEqual(baseProgress * 2f, TypewriterClock.Progress(1f, 40f), 0.001f,
                "2× preference doubles the reveal head");

            // Garbage multiplier is treated as 1 — never a frozen typewriter.
            TypewriterClock.UserSpeedMultiplier = 0f;
            Assert.AreEqual(baseProgress, TypewriterClock.Progress(1f, 40f), 0.001f);
        }

        [Test]
        public void TextSpeedClampsAndDrivesTheClock()
        {
            LvnPrefs.TextSpeed = 99f;
            Assert.AreEqual(3f, LvnPrefs.TextSpeed, 0.001f, "clamped to the max");
            Assert.AreEqual(3f, TypewriterClock.UserSpeedMultiplier, 0.001f, "pushed into the clock");

            LvnPrefs.TextSpeed = 0.01f;
            Assert.AreEqual(0.25f, LvnPrefs.TextSpeed, 0.001f, "clamped to the min");
        }

        [Test]
        public void ChangedFiresOncePerRealChange()
        {
            int fired = 0;
            void Count() => fired++;
            LvnPrefs.Changed += Count;
            try
            {
                LvnPrefs.VolMusic = 0.5f;
                LvnPrefs.VolMusic = 0.5f; // no-op — same value
                Assert.AreEqual(1, fired, "idempotent set must not re-fire");
                LvnPrefs.VolMusic = 0.7f;
                Assert.AreEqual(2, fired);
            }
            finally { LvnPrefs.Changed -= Count; }
        }

        [Test]
        public void PrefsPersistToPlayerPrefs()
        {
            LvnPrefs.DialogOpacity = 0.6f;
            Assert.AreEqual(0.6f, PlayerPrefs.GetFloat("lvn_pref_dialog_opacity", -1f), 0.001f,
                "the preference lands on disk-backed PlayerPrefs");

            LvnPrefs.AutoAdvance = true;
            Assert.AreEqual(1, PlayerPrefs.GetInt("lvn_pref_auto_advance", -1));
        }
    }
}
