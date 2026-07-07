using System;
using Lvn.Services;
using UnityEngine;

namespace Lvn.Samples
{
    /// <summary>
    /// Reference adapter that plugs <b>gree/unity-webview</b> into the engine's
    /// <see cref="LvnWebView"/> seam — the EXTERNAL wiring the engine deliberately
    /// does not ship. The engine never references the plugin; this file, compiled
    /// in YOUR project after you import the plugin, is the only thing that does.
    ///
    /// Install:
    ///   1. Import gree/unity-webview (its <c>dist/unity-webview.unitypackage</c>,
    ///      or a UPM fork) into your project's <c>Assets/</c>.
    ///   2. Import this sample (Package Manager ▸ LVN Engine ▸ Samples ▸
    ///      "Web view (gree adapter)") — or copy this file into <c>Assets/</c>.
    ///   3. That's it: the component self-bootstraps after the first scene loads
    ///      and registers <see cref="LvnWebView.Opener"/>. From then on any engine
    ///      call to <c>LvnWebView.Open(url)</c> (the store's "pay from Russia"
    ///      banner, ToS/Policy links) opens IN-APP instead of the browser.
    ///
    /// With the plugin absent, do nothing — the engine's <see cref="LvnWebView"/>
    /// falls back to the system browser on its own.
    ///
    /// NOTE: gree's <c>WebViewObject.Init</c> signature drifts between versions;
    /// adjust the call below to yours. This file lives in <c>Samples~</c>, so it
    /// is never compiled by the engine — API drift here can't break the build.
    /// </summary>
    public sealed class LvnGreeWebView : MonoBehaviour
    {
        private WebViewObject _web;
        private bool _visible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("LvnGreeWebView");
            DontDestroyOnLoad(go);
            go.AddComponent<LvnGreeWebView>();
        }

        private void Awake()
        {
            LvnWebView.Opener = Open;   // engine → in-app web view
            LvnWebView.Closer = Close;
        }

        // Return true so the engine treats the navigation as handled in-app.
        private bool Open(string url)
        {
            EnsureWeb();
            _web.LoadURL(url);
            _web.SetVisibility(true);
            _visible = true;
            return true;
        }

        private void Close()
        {
            if (_web != null) _web.SetVisibility(false);
            _visible = false;
        }

        private void EnsureWeb()
        {
            if (_web != null) return;
            _web = new GameObject("WebViewObject").AddComponent<WebViewObject>();
            _web.Init(
                cb: _ => { },
                err: msg => Debug.LogWarning($"[lvn-webview] {msg}"),
                httpErr: msg => Debug.LogWarning($"[lvn-webview] http {msg}"),
                started: _ => { },
                ld: _ => { },
                enableWKWebView: true);
            // Reserve a top strip for the close button drawn in OnGUI.
            _web.SetMargins(0, 90, 0, 0);
        }

        // gree's web view is a NATIVE overlay above Unity's UI, so a uGUI/UITK
        // close button would be hidden behind it — draw it via OnGUI in the
        // reserved top margin instead.
        private void OnGUI()
        {
            if (!_visible) return;
            if (GUI.Button(new Rect(12, 20, 140, 56), "✕ Закрыть")) Close();
        }

        private void OnDestroy()
        {
            if (LvnWebView.Opener == (Func<string, bool>)Open) LvnWebView.Opener = null;
            if (LvnWebView.Closer == (Action)Close) LvnWebView.Closer = null;
            if (_web != null) Destroy(_web.gameObject);
        }
    }
}
