using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Lvn.Sandbox.Editor
{
    /// <summary>
    /// Прогон новеллы БЕЗ ЧЕЛОВЕКА: входит в Play, открывает главу, прыгает на
    /// метку, проматывает нужное число реплик и снимает кадр.
    ///
    /// <para>Зачем. Ошибки сцены («фигура не встала», «кадр не переснялся»)
    /// видны только в игре, а дойти до нужного места руками — минута кликов на
    /// каждую проверку. С таким циклом правка стоит дороже, чем поиск причины,
    /// и отладка сваливается в переписку «нажми, посмотри, скажи». Проба
    /// закрывает цикл: правка → кадр → вывод, без чужих рук.</para>
    ///
    /// <para>Всё через рефлексию НАМЕРЕННО: движок не должен обзаводиться
    /// публичным «запусти главу и прыгни на метку» ради своего же теста —
    /// это API для QA, а не для авторов, и жить ему в песочнице.</para>
    ///
    /// <code>
    /// PROBE_TITLE=knight-duel PROBE_LABEL=враг_костяк PROBE_STEPS=6 \
    /// PROBE_OUT=qa/play/skeleton.png \
    /// Unity -batchmode -projectPath sandbox \
    ///       -executeMethod Lvn.Sandbox.Editor.PlayProbe.Run
    /// </code>
    /// Без <c>-nographics</c> (иначе кадр пустой) и без <c>-quit</c> — из Play
    /// проба выходит сама.
    /// </summary>
    public static class PlayProbe
    {
        private const string FlagKey = "lvn.probe.active";

        // Список сцен для одного запуска: Unity стартует полторы минуты, и
        // платить их за каждый кадр — самая дорогая часть отладки. Один старт
        // на пять сцен превращает час ожидания в двенадцать минут.
        private static string[] _scenes = System.Array.Empty<string>();
        private static int _sceneIdx;

        private static double _t0;          // время старта текущего шага
        private static int _step;
        private static object _app;         // NovelApp
        private static object _title, _chapter;
        private static int _advances;

        public static void Run()
        {
            EditorPrefs.SetBool(FlagKey, true);
            Debug.Log("[probe] вход в Play");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>Сцены, которые надо снять за один запуск: пути .lvns через
        /// запятую в PROBE_SCENES. Каждая компилируется на локальный сервер,
        /// проигрывается и снимается — кадр ложится рядом с исходником.</summary>
        private static void LoadSceneList()
        {
            var list = Env("PROBE_SCENES", "");
            _scenes = string.IsNullOrEmpty(list)
                ? System.Array.Empty<string>()
                : list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            _sceneIdx = 0;
        }

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            if (!EditorPrefs.GetBool(FlagKey, false)) return;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static string Env(string k, string def)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? def : v;
        }

        private static void Fail(string why)
        {
            Debug.LogError("[probe] " + why);
            Finish(1);
        }

        private static void Finish(int code)
        {
            EditorPrefs.DeleteKey(FlagKey);
            EditorApplication.update -= Tick;
            EditorApplication.isPlaying = false;
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        private static double Since => EditorApplication.timeSinceStartup - _t0;

        private static void Advance(int next)
        {
            _step = next;
            _t0 = EditorApplication.timeSinceStartup;
        }

        private static void Tick()
        {
            if (!Application.isPlaying)
            {
                // Ещё не вошли (или уже вышли) — ждём вход не дольше минуты.
                if (_step == 0 && Since > 60) Fail("Play так и не запустился");
                return;
            }

            switch (_step)
            {
                case 0: // ждём приложение и манифест
                    if (Since > 90) { Fail("новелла не поднялась за 90 с"); return; }
                    _app = FindByTypeName("Lvn.UI.Screens.NovelApp") ?? FindByTypeName("NovelApp");
                    if (_app == null) return;
                    var manifest = Field(_app, "_manifest");
                    if (manifest == null) return;
                    LoadSceneList();
                    if (_scenes.Length > 0 && !NextScene()) { Fail("не удалось собрать первую сцену списка"); return; }
                    if (!PickChapter(manifest)) { Fail("в манифесте нет нужной главы"); return; }
                    Debug.Log($"[probe] глава найдена: {Field(_chapter, "id")}");
                    StartChapter();
                    Advance(1);
                    return;

                case 1: // ждём плеер
                    if (Since > 90) { Fail("плеер не появился за 90 с"); return; }
                    if (PlayerObj() == null) return;
                    Debug.Log("[probe] плеер жив, прыгаю на метку");
                    Advance(2);
                    return;

                case 2: // прыжок на метку боя
                {
                    if (Since < 2.0) return; // дать главе доиграть вступление
                    var player = PlayerObj();
                    if (player == null) { Fail("плеер исчез"); return; }
                    var label = Env("PROBE_LABEL", "");
                    if (!string.IsNullOrEmpty(label))
                    {
                        var go = player.GetType().GetMethod("GoTo", new[] { typeof(string) });
                        if (go == null) { Fail("нет LvnPlayer.GoTo(string)"); return; }
                        go.Invoke(player, new object[] { label });
                        Debug.Log("[probe] прыжок на метку " + label);
                    }
                    Advance(3);
                    return;
                }

                case 3: // промотать реплики
                {
                    int steps = int.TryParse(Env("PROBE_STEPS", "6"), out var s) ? s : 6;
                    if (_advances >= steps) { Advance(4); return; }
                    if (Since < 0.45) return;
                    var player = PlayerObj();
                    player?.GetType().GetMethod("Advance", Type.EmptyTypes)?.Invoke(player, null);
                    _advances++;
                    _t0 = EditorApplication.timeSinceStartup;
                    return;
                }

                case 4: // дать сцене устояться и снять кадр
                    if (Since < float.Parse(Env("PROBE_SETTLE", "3.0"),
                            System.Globalization.CultureInfo.InvariantCulture)) return;
                    var outPath = Env("PROBE_OUT", "qa/play/shot.png");
                    if (_scenes.Length > 0)
                    {
                        var name = Path.GetFileNameWithoutExtension(_scenes[_sceneIdx]);
                        outPath = Path.Combine(Path.GetDirectoryName(outPath) ?? "qa/play", name + ".png");
                    }
                    // От Assets, а не от рабочего каталога: в batchmode он —
                    // откуда запустили процесс, и кадр уезжает мимо репозитория.
                    var full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", outPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    ScreenCapture.CaptureScreenshot(full);   // экран целиком — только с графикой
                    ShootSet(full);                           // кадр набора — работает и в batchmode
                    ReportScene();
                    Advance(5);
                    return;

                case 5: // файл пишется через кадр-другой
                    if (Since < 2.0) return;
                    // Следующая сцена из списка — без перезапуска редактора.
                    if (_sceneIdx + 1 < _scenes.Length)
                    {
                        _sceneIdx++;
                        if (NextScene())
                        {
                            _advances = 0;
                            Advance(1);   // ждём плеер новой главы
                            return;
                        }
                    }
                    Debug.Log("[probe] готово");
                    Finish(0);
                    return;
            }
        }

        /// <summary>Снять КАДР НАБОРА: его камера рендерит в свою текстуру, и это
        /// работает даже в batchmode, где снимок экрана не пишется вовсе.
        /// Именно в этом кадре живут фигуры сцены — то, что мы и проверяем.</summary>
        private static void ShootSet(string outPath)
        {
            var backdrop = FindByTypeName("Lvn.UI.World.Lvn3DBackdrop");
            if (backdrop == null) { Debug.Log("[probe] набора в сцене нет"); return; }
            backdrop.GetType().GetMethod("ShootNow")?.Invoke(backdrop, null);
            if (!(Field(backdrop, "_rt") is RenderTexture rt))
            {
                Debug.Log("[probe] у набора нет буфера кадра");
                return;
            }
            // В batchmode экран всегда 640×480 — то есть ЛАНДШАФТ, а игра живёт
            // в портрете. Композицию по такому кадру не оценить: вертикальный
            // обзор тот же, а поля по бокам врут. Поэтому снимаем своей
            // текстурой телефонного размера.
            int w = int.TryParse(Env("PROBE_W", "720"), out var pw) ? pw : 720;
            int h = int.TryParse(Env("PROBE_H", "1560"), out var ph) ? ph : 1560;
            if (Field(backdrop, "_cam") is Camera setCam && w > 0 && h > 0)
            {
                var shot = new RenderTexture(w, h, 24) { antiAliasing = 4 };
                var keep = setCam.targetTexture;
                setCam.targetTexture = shot;
                setCam.Render();
                setCam.targetTexture = keep;
                rt = shot;
            }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var setPath = outPath.Replace(".png", "-set.png");
            File.WriteAllBytes(setPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log($"[probe] кадр набора {rt.width}×{rt.height} → {setPath}");
        }

        /// <summary>Что стоит в наборе — числом, а не на глаз: имя фигуры, её
        /// место и размер в метрах. Пустой список объясняет чёрный кадр быстрее
        /// любого разглядывания.</summary>
        private static void ReportScene()
        {
            var backdrop = FindByTypeName("Lvn.UI.World.Lvn3DBackdrop");
            if (backdrop == null) return;
            if (!(Field(backdrop, "_boards") is System.Collections.IDictionary boards))
            {
                Debug.Log("[probe] список фигур недоступен");
                return;
            }
            Debug.Log($"[probe] фигур в наборе: {boards.Count}");
            foreach (System.Collections.DictionaryEntry e in boards)
            {
                var t = e.Value as Transform;
                if (t == null) { Debug.Log($"[probe]   {e.Key}: пусто"); continue; }
                var mr = t.GetComponent<MeshRenderer>();
                var mat = mr != null ? mr.sharedMaterial : null;
                Debug.Log($"[probe]   {e.Key}: место {t.localPosition}, размер {t.localScale}, " +
                          $"шейдер={(mat != null ? mat.shader.name : "нет")}, " +
                          $"текстура={(mat != null && mat.mainTexture != null ? mat.mainTexture.width + "x" + mat.mainTexture.height : "нет")}, " +
                          $"виден={(mr != null && mr.enabled && t.gameObject.activeInHierarchy)}");
            }
            if (Field(backdrop, "_bodies") is System.Collections.IDictionary bodies)
            {
                Debug.Log($"[probe] тел сцены: {bodies.Count}");
                foreach (System.Collections.DictionaryEntry e in bodies)
                {
                    var t = e.Value as Transform;
                    if (t == null) { Debug.Log($"[probe]   тело {e.Key}: пусто"); continue; }
                    // Роща — родитель копий: у неё своего меша нет, и спрашивать
                    // его напрямую значит уронить отчёт на первом же посеве.
                    var mr = t.GetComponentInChildren<MeshRenderer>();
                    var mf = t.GetComponentInChildren<MeshFilter>();
                    int copies = t.childCount;
                    Debug.Log($"[probe]   тело {e.Key}: место {t.localPosition}, размер {t.localScale}, " +
                              (copies > 0 ? $"копий={copies}, " : "") +
                              $"меш={(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name + " " + mf.sharedMesh.vertexCount + "в" : "нет")}, " +
                              $"шейдер={(mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.shader.name : "нет")}, " +
                              $"цвет={(mr != null && mr.sharedMaterial != null ? ColorUtility.ToHtmlStringRGB(mr.sharedMaterial.color) : "—")}");
                }
            }
            if (Field(backdrop, "_lights") is System.Collections.IDictionary lights)
            {
                // Рассеянный свет — теперь его считают сами шейдеры, и увидеть
                // его значения иначе негде: в кадре он выглядит как «цвет
                // предмета», а не как источник.
                Debug.Log($"[probe] ambient: режим={RenderSettings.ambientMode}, " +
                          $"небо={ColorUtility.ToHtmlStringRGB(RenderSettings.ambientSkyColor)}, " +
                          $"горизонт={ColorUtility.ToHtmlStringRGB(RenderSettings.ambientEquatorColor)}, " +
                          $"земля={ColorUtility.ToHtmlStringRGB(RenderSettings.ambientGroundColor)}, " +
                          $"сила={RenderSettings.ambientIntensity:0.00}");
                Debug.Log($"[probe] источников света: {lights.Count}");
                foreach (System.Collections.DictionaryEntry e in lights)
                {
                    var l = e.Value as Light;
                    if (l == null) continue;
                    Debug.Log($"[probe]   свет {e.Key}: {l.type}, сила {l.intensity:0.00}, " +
                              $"цвет {ColorUtility.ToHtmlStringRGB(l.color)}, тени {l.shadows}");
                }
            }
            if (Field(backdrop, "_shadows") is System.Collections.IDictionary shadows)
            {
                Debug.Log($"[probe] теней: {shadows.Count}");
                foreach (System.Collections.DictionaryEntry e in shadows)
                {
                    var t = e.Value as Transform;
                    if (t == null) { Debug.Log($"[probe]   тень {e.Key}: пусто"); continue; }
                    var mr = t.GetComponent<MeshRenderer>();
                    Debug.Log($"[probe]   тень {e.Key}: место {t.localPosition}, размер {t.localScale}, " +
                              $"поворот {t.localEulerAngles}, " +
                              $"текстура={(mr?.sharedMaterial?.mainTexture != null ? "есть" : "нет")}, " +
                              $"виден={(mr != null && mr.enabled && t.gameObject.activeInHierarchy)}");
                }
            }
            var cam = Field(backdrop, "_cam") as Camera;
            if (cam != null)
                Debug.Log($"[probe] камера набора: место {cam.transform.localPosition}, " +
                          $"поворот {cam.transform.localEulerAngles}, обзор {cam.fieldOfView:0.0}");
        }

        /// <summary>Собрать очередную сцену списка на локальный сервер и
        /// запустить её. Компилируем внешним lvnconv — тем же, которым собирает
        /// автор, чтобы проверять ровно то, что он получит.</summary>
        private static bool NextScene()
        {
            if (_sceneIdx >= _scenes.Length) return false;
            var src = _scenes[_sceneIdx];
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var full = Path.IsPathRooted(src) ? src : Path.Combine(root, src);
            var target = Env("PROBE_TARGET", "");
            if (string.IsNullOrEmpty(target) || !File.Exists(full))
            {
                Debug.LogWarning($"[probe] сцену {src} собрать некуда (PROBE_TARGET) или её нет");
                return false;
            }

            var psi = new System.Diagnostics.ProcessStartInfo(Env("PROBE_LVNCONV", "/tmp/lvnconv"),
                $"convert -i \"{full}\" -o \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                var err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    Debug.LogError($"[probe] {src} не компилируется: {err}");
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(err)) Debug.Log($"[probe] {src}: {err.Trim()}");
            }

            Debug.Log($"[probe] сцена {_sceneIdx + 1}/{_scenes.Length}: {Path.GetFileName(src)}");
            // Глава перечитывается сама (NovelApp следит за сервером), но
            // надёжнее перезапустить её явно — иначе снимок может застать
            // предыдущую.
            StartChapter();
            return true;
        }

        // --- вспомогательное -------------------------------------------------

        private static UnityEngine.Object FindByTypeName(string full)
        {
            var t = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(full, false))
                .FirstOrDefault(x => x != null);
            return t == null ? null : UnityEngine.Object.FindAnyObjectByType(t);
        }

        private static object Field(object o, string name)
        {
            if (o == null) return null;
            var f = o.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f?.GetValue(o);
        }

        private static object PlayerObj()
        {
            var stage = Field(_app, "Stage") ?? _app?.GetType()
                .GetField("Stage", BindingFlags.Instance | BindingFlags.Public)?.GetValue(_app);
            if (stage == null) return null;
            return stage.GetType().GetProperty("Player")?.GetValue(stage);
        }

        /// <summary>Выбрать титул и главу из манифеста по PROBE_TITLE / PROBE_CHAPTER
        /// (по умолчанию — первый титул и его первая глава).</summary>
        private static bool PickChapter(object manifest)
        {
            var titles = Field(manifest, "titles") as System.Collections.IEnumerable;
            if (titles == null) return false;
            var wantTitle = Env("PROBE_TITLE", "");
            var wantChapter = Env("PROBE_CHAPTER", "");
            foreach (var t in titles)
            {
                var id = Field(t, "id") as string;
                if (!string.IsNullOrEmpty(wantTitle) && id != wantTitle) continue;
                var seasons = Field(t, "seasons") as System.Collections.IEnumerable;
                if (seasons == null) continue;
                foreach (var s in seasons)
                {
                    var chapters = Field(s, "chapters") as System.Collections.IEnumerable;
                    if (chapters == null) continue;
                    foreach (var c in chapters)
                    {
                        var cid = Field(c, "id") as string;
                        if (!string.IsNullOrEmpty(wantChapter) && cid != wantChapter) continue;
                        _title = t; _chapter = c;
                        return true;
                    }
                }
            }
            return false;
        }

        private static void StartChapter()
        {
            var m = _app.GetType().GetMethod("PlayChapterAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) { Fail("нет NovelApp.PlayChapterAsync"); return; }
            var name = Env("PROBE_NAME", "Проба");
            m.Invoke(_app, new object[] { _title, _chapter, name });
            Debug.Log("[probe] глава запущена");
        }
    }
}
