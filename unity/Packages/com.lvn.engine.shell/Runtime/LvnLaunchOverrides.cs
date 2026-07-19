using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Test-lane launch overrides for device automation. A Development build
    /// (and ONLY a Development build) may be pointed at a different LVN server
    /// without re-exporting or rebuilding:
    ///
    ///   Android:    adb shell am start -n &lt;pkg&gt;/&lt;activity&gt; -e lvn_server http://10.0.2.2:8099
    ///   Desktop/CI: LVN_SERVER=http://127.0.0.1:8099 (environment variable)
    ///
    /// Release builds ignore both channels — a shipped APK cannot be steered
    /// to a hostile server by a crafted launch intent.
    /// </summary>
    public static class LvnLaunchOverrides
    {
        public const string IntentExtra = "lvn_server";
        public const string EnvVar = "LVN_SERVER";

        /// <summary>The server override for this launch, or null when absent
        /// (or when this is not a Development build).</summary>
        public static string ServerUrl() => Resolve(RawValue(), Debug.isDebugBuild);

        /// <summary>The pure gate+normalize seam (unit-tested): non-dev builds
        /// always resolve null; whitespace trims away; empty means no override;
        /// a trailing slash drops so path concatenation stays clean.</summary>
        public static string Resolve(string raw, bool isDebugBuild)
        {
            if (!isDebugBuild) return null;
            raw = raw?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            return raw.TrimEnd('/');
        }

        private static string RawValue()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                if (activity == null) return null;
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent?.Call<string>("getStringExtra", IntentExtra);
            }
            catch (System.Exception)
            {
                return null; // no activity yet / non-standard host — no override
            }
#else
            return System.Environment.GetEnvironmentVariable(EnvVar);
#endif
        }
    }
}
