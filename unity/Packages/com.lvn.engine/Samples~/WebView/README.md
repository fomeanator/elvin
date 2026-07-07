# Web view (gree adapter)

External wiring for an **in-app web view** — opening a URL inside the app
(the store's "how to pay from Russia" banner, ToS/Policy, promos) instead of
kicking the player out to the system browser.

The engine ships **no** web-view library and never links one. It only exposes a
seam, `Lvn.Services.LvnWebView`:

```csharp
LvnWebView.Opener = url => { /* show it in-app */ return true; };  // host plugs this
LvnWebView.Open("https://pay.example.com/ru");                     // engine calls this
```

With no `Opener` plugged, `LvnWebView.Open` falls back to `Application.OpenURL`
(the external browser) — always safe, zero dependency.

## Install

1. Import **gree/unity-webview** into your project's `Assets/` — its
   `dist/unity-webview.unitypackage`, or a UPM fork. This brings the native
   `WebViewObject` (iOS/Android/editor).
2. Import this sample (Package Manager ▸ **LVN Engine** ▸ Samples ▸
   **Web view (gree adapter)**) or copy `LvnGreeWebView.cs` into `Assets/`.
3. Done. `LvnGreeWebView` self-bootstraps after the first scene loads and
   registers the seam. Every engine `LvnWebView.Open(url)` now opens in-app.

`WebViewObject.Init`'s signature drifts between gree versions — adjust the call
in `LvnGreeWebView.cs` to match yours. Because the file lives in your project
(not the engine package), that never affects the engine build.

## Using another plugin

Any web view works — the seam is plugin-agnostic. Replace the body of `Open`
with your plugin's "load + show" calls and return `true`. UniWebView, a custom
`AndroidJavaObject`/`WKWebView` bridge, etc.
