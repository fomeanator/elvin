using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Assigns the launcher icon set (Assets/Icon/) to PlayerSettings — the
/// default icon plus Android's adaptive foreground/background pair — so a
/// headless build ships the branded icon instead of the Unity cube.
/// Run once: Unity -batchmode -quit -executeMethod SetAppIcon.Apply
/// </summary>
public static class SetAppIcon
{
    public static void Apply()
    {
        var legacy = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Icon/app-icon.png");
        var fg = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Icon/app-icon-fg.png");
        var bg = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Icon/app-icon-bg.png");
        if (legacy == null) { Debug.LogError("[icon] Assets/Icon/app-icon.png missing"); EditorApplication.Exit(1); return; }

        PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { legacy }, IconKind.Any);

        SetAndroid(AndroidPlatformIconKind.Adaptive, bg, fg);
        SetAndroid(AndroidPlatformIconKind.Round, legacy, null);
        SetAndroid(AndroidPlatformIconKind.Legacy, legacy, null);

        AssetDatabase.SaveAssets();
        Debug.Log("[icon] launcher icons applied");
    }

    private static void SetAndroid(PlatformIconKind kind, Texture2D a, Texture2D b)
    {
        var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
        foreach (var icon in icons)
            icon.SetTextures(b == null ? new[] { a } : new[] { a, b });
        PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kind, icons);
    }
}
