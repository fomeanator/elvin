using System;
using UnityEngine;

namespace Lvn.Services
{
    /// <summary>
    /// In-app web view seam — opening a URL INSIDE the app (a "how to pay from
    /// Russia" instructions page pinned in the store, ToS/Policy, a promo).
    ///
    /// The engine ships NO web-view library and never links one: the host
    /// installs a plugin (gree/unity-webview etc.) as EXTERNAL wiring and plugs
    /// an adapter into <see cref="Opener"/>. With no hook the seam falls back to
    /// the system browser (<see cref="Application.OpenURL"/>) — always safe, so
    /// the engine compiles and runs with zero web-view dependency. Same contract
    /// as <see cref="LvnAds.ShowRewarded"/> / <c>StoreScreen.PurchaseFlow</c>:
    /// the engine calls, the host provides.
    /// </summary>
    public static class LvnWebView
    {
        /// <summary>Host hook: open <paramref name="url"/> in an in-app web view;
        /// return true if it was handled. Unset, or a false return, falls back to
        /// the external browser. gree/unity-webview adapter: create a
        /// WebViewObject, <c>LoadURL(url)</c>, <c>SetVisibility(true)</c>, return
        /// true. See howto/EMBEDDING.md for the reference adapter.</summary>
        public static Func<string, bool> Opener;

        /// <summary>Host hook to CLOSE the in-app web view (optional — most
        /// adapters draw their own close button and never need this).</summary>
        public static Action Closer;

        /// <summary>True when a host in-app web view is installed. UI can still
        /// call <see cref="Open"/> without it (external browser), but a host may
        /// prefer to phrase a banner differently when it's absent.</summary>
        public static bool Available => Opener != null;

        /// <summary>Open a URL: the host web view when one is plugged and it
        /// handles the URL, otherwise the system browser. No-op on empty input;
        /// a throwing host hook can never swallow the navigation.</summary>
        public static void Open(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                if (Opener != null && Opener(url)) return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[lvn-webview] host opener threw, falling back to browser: {e.Message}");
            }
            Application.OpenURL(url);
        }

        /// <summary>Close the in-app web view if the host supports it.</summary>
        public static void Close()
        {
            try { Closer?.Invoke(); }
            catch (Exception e) { Debug.LogWarning($"[lvn-webview] host closer threw: {e.Message}"); }
        }
    }
}
