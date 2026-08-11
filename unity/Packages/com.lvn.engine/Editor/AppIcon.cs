using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Lvn.EditorTools
{
    /// <summary>
    /// Иконка приложения из <c>Assets/Icon/</c>: обычная (<c>app-icon.png</c>) и
    /// пара для адаптивной андроидной (<c>app-icon-fg.png</c> поверх
    /// <c>app-icon-bg.png</c>).
    ///
    /// <para>Жила эта логика в песочнице движка, и потому её не получал никто,
    /// кроме неё самой: экспортированный проект собирался с кубиком Unity на
    /// рабочем столе. Здесь она едет вместе с пакетом, а <see cref="CliBuild"/>
    /// зовёт её сам — иконка появляется в сборке ровно потому, что картинки
    /// лежат в проекте, без отдельного шага в инструкции.</para>
    ///
    /// Руками: <c>Unity -batchmode -quit -executeMethod Lvn.EditorTools.AppIcon.Apply</c>
    /// </summary>
    public static class AppIcon
    {
        private const string Dir = "Assets/Icon/";

        /// <summary>Ставит иконки; отсутствие файлов — ошибка (ручной вызов).</summary>
        public static void Apply()
        {
            if (!ApplyIfPresent())
            {
                Debug.LogError("[icon] нет " + Dir + "app-icon.png");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Ставит иконки, если картинки есть. Вернёт false, если ставить
        /// нечего — сборке это не повод падать, у проекта может не быть своей
        /// иконки вовсе.</summary>
        public static bool ApplyIfPresent()
        {
            var legacy = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "app-icon.png");
            if (legacy == null) return false;

            var fg = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "app-icon-fg.png");
            var bg = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "app-icon-bg.png");

            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { legacy }, IconKind.Any);

            // Адаптивная иконка — пара слоёв; без фона андроид рисует её на
            // прозрачном, и на светлой теме от иконки остаётся силуэт.
            if (fg != null && bg != null) SetAndroid(AndroidPlatformIconKind.Adaptive, bg, fg);
            SetAndroid(AndroidPlatformIconKind.Round, legacy, null);
            SetAndroid(AndroidPlatformIconKind.Legacy, legacy, null);

            AssetDatabase.SaveAssets();
            Debug.Log("[icon] иконки применены" + (fg != null && bg != null ? " (включая адаптивную)" : ""));
            return true;
        }

        private static void SetAndroid(PlatformIconKind kind, Texture2D a, Texture2D b)
        {
            var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
            foreach (var icon in icons)
                icon.SetTextures(b == null ? new[] { a } : new[] { a, b });
            PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kind, icons);
        }
    }
}
