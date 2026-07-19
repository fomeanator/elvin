using UnityEngine;
using UnityEngine.EventSystems;
using Lvn.UI.Screens;

namespace Lvn.Sandbox
{
    /// <summary>
    /// Dev bootstrap for the engine sandbox. On Play it spins up a
    /// <see cref="NovelApp"/> pointed at the local authoring-panel server
    /// (http://127.0.0.1:8077), with the default runtime theme so UI Toolkit text
    /// renders — no scene setup needed. Iterate on chapters in the panel, press
    /// Play here to see them. Run the server first:
    /// <code>go run ./server -content ./server/content -addr :8077 -admin-token devtoken</code>
    /// </summary>
    public static class Boot
    {
        public const string ServerUrl =
#if UNITY_EDITOR
            "http://127.0.0.1:8078"; // dev: the local content server
#else
            // Device builds of the SANDBOX reach the dev server through
            // adb reverse (see qa/monkey.sh --server). A product fork bakes
            // its own production URL here — or ships a Development build and
            // steers it with the `lvn_server` intent extra (LvnLaunchOverrides).
            "http://127.0.0.1:8078";
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Run()
        {
            if (Object.FindAnyObjectByType<NovelApp>() != null) return; // already booted

            if (Object.FindAnyObjectByType<Camera>() == null)
            {
                var camGo = new GameObject("Main Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                camGo.tag = "MainCamera";
                Object.DontDestroyOnLoad(camGo);
            }

            // UI Toolkit pointer events (tap-to-advance, choices) need an EventSystem.
            if (Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                Object.DontDestroyOnLoad(es);
            }

            var go = new GameObject("NovelApp");
            var app = go.AddComponent<NovelApp>();
            app.ServerUrl = ServerUrl;
            // AskName stays at its default (true): the NOVEL asks the name at a
            // fresh start when the manifest ships ui.name_input — the old
            // hard-off here predates that flow and silently killed the prompt.
            app.SyncInterval = 2f; // live-reload chapters edited in the panel
            app.ThemeResourcePath = "UI/AppLoading/UnityDefaultRuntimeTheme";
            Object.DontDestroyOnLoad(go);

            Debug.Log("[sandbox] NovelApp booting → " + app.ServerUrl);
        }
    }
}
