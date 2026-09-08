using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lvn.UiLab.Editor
{
    /// <summary>
    /// ФОТОГРАФ ЛАБОРАТОРИИ — снимки экранов оболочки из терминала, без рук.
    ///
    /// <para>Раз в секунду пишет пульс редактора в <c>ui-lab/.editor-pulse</c>
    /// (играет ли, компилирует ли). По флагу <c>ui-lab/.shots</c>
    /// («приставка|ширина|высота|…») ставит Game View в нужное разрешение и
    /// входит в Play; дальше снимает рантайм-драйвер (<c>StageShotsDriver</c>),
    /// а по маркеру <c>.shots-done</c> редактор выходит из Play. Флаг
    /// <c>.editor-refresh</c> заставляет перечитать ассеты — редактор без
    /// фокуса правок скриптов сам не видит.</para>
    /// </summary>
    public static class UiLabShots
    {
        private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string Flag => Path.Combine(Root, ".shots");
        private static string Done => Path.Combine(Root, ".shots-done");
        private static string Pulse => Path.Combine(Root, ".editor-pulse");
        private static string Refresh => Path.Combine(Root, ".editor-refresh");
        private static double _next;

        private static Vector2Int RequestedSize()
        {
            const int defaultWidth = 1080, defaultHeight = 1920;
            try
            {
                var parts = File.ReadAllText(Flag).Trim().Split('|');
                if (parts.Length >= 3
                    && int.TryParse(parts[1], out var width)
                    && int.TryParse(parts[2], out var height)
                    && width > 0 && height > 0)
                    return new Vector2Int(width, height);
            }
            catch (Exception) { }
            return new Vector2Int(defaultWidth, defaultHeight);
        }

        private static int _ticks;
        private static string Errors => Path.Combine(Root, ".editor-errors");

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            // Ошибки компиляции — в файл рядом с пульсом: лог редактора из
            // терминала не всегда доступен (файл может быть снесён), а
            // «собралось или нет» нужно знать до входа в Play.
            UnityEditor.Compilation.CompilationPipeline.compilationStarted -= OnCompileStart;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnCompileStart;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished -= OnAssemblyDone;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += OnAssemblyDone;
            // РЕДАКТОР БЕЗ ФОКУСА ДРЕМЛЕТ: «Interaction Mode: Default» роняет
            // тик редактора до одного в секунду, и цикл игрока, который мы
            // просим из тика, идёт с той же частотой. Снимкам из терминала
            // нужен ход без дросселя — ставим режим и просим редактор его
            // перечитать (метод внутренний, отсюда отражение).
            try
            {
                if (EditorPrefs.GetInt("InteractionMode", 0) != 1)
                {
                    EditorPrefs.SetInt("InteractionMode", 1);
                    typeof(EditorApplication).GetMethod("UpdateInteractionModeSettings",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                        ?.Invoke(null, null);
                }
            }
            catch (Exception e) { Debug.LogWarning("[shots] interaction mode: " + e.Message); }
        }

        private static void OnCompileStart(object _)
        {
            try { File.WriteAllText(Errors, "compiling\n"); } catch (Exception) { }
        }

        private static void OnAssemblyDone(string assembly, UnityEditor.Compilation.CompilerMessage[] messages)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var m in messages)
                    if (m.type == UnityEditor.Compilation.CompilerMessageType.Error)
                        sb.Append(m.file).Append('(').Append(m.line).Append("): ").Append(m.message).Append('\n');
                var prev = File.Exists(Errors) ? File.ReadAllText(Errors) : "";
                if (prev.StartsWith("compiling")) prev = "";
                File.WriteAllText(Errors, prev + sb);
            }
            catch (Exception) { }
        }

        private static void Tick()
        {
            _ticks++;
            // РЕДАКТОР В ФОНЕ НЕ КРУТИТ КАДРЫ. Окно закрыто чужими окнами —
            // и Game View перестаёт перерисовываться: Time.frameCount стоит,
            // корутины драйвера не идут, переходы сцены «залипают» (сторож
            // композита снимал куклу). Просим цикл игрока и перерисовку сами —
            // на каждый тик редактора, а не раз в секунду.
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 1.0;
            if (EditorApplication.isPlaying && EditorApplication.isPaused && File.Exists(Pulse))
                EditorApplication.isPaused = false;
            try
            {
                File.WriteAllText(Pulse,
                    $"playing={EditorApplication.isPlaying} compiling={EditorApplication.isCompiling} " +
                    $"updating={EditorApplication.isUpdating} paused={EditorApplication.isPaused} " +
                    $"frame={Time.frameCount} ticks/s={_ticks} mode={EditorPrefs.GetInt("InteractionMode", -1)} t={DateTime.Now:HH:mm:ss}\n");
                _ticks = 0;
            }
            catch (Exception) { }
            if (File.Exists(Done))
            {
                File.Delete(Done);
                if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
                return;
            }
            if (File.Exists(Refresh) && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                File.Delete(Refresh);
                AssetDatabase.Refresh();
                return;
            }
            if (!File.Exists(Flag) || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                var size = RequestedSize();
                PlayModeWindow.SetViewType(PlayModeWindow.PlayModeViewTypes.GameView);
                PlayModeWindow.SetCustomRenderingResolution((uint)size.x, (uint)size.y,
                    $"shots-{size.x}x{size.y}");
            }
            catch (Exception e) { Debug.LogWarning("[shots] gameview size: " + e.Message); }
            EditorApplication.EnterPlaymode();
            Debug.Log("[shots] вход в Play — дальше снимает рантайм-драйвер");
        }

        [MenuItem("Tools/UiLab Shots")]
        public static void Run() => File.WriteAllText(Flag, "manual|1170|2532");
    }
}
