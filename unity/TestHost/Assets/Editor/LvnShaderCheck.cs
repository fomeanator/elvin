using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

// ЧЕСТНАЯ ПРОВЕРКА ШЕЙДЕРОВ ДВИЖКА, а не опрос импортёра.
//
// Прежняя версия спрашивала isSupported и GetShaderMessages — и говорила
// «поддержан, сообщений 0» про шейдер, который на живом Metal рисовал
// ядовито-розовым (28.08). Причина простая: `-batchmode -nographics` не
// компилирует вариант под графический бэкенд, поэтому ошибка варианта
// (tex2D в дивергентном потоке) там просто не возникает.
//
// Здесь варианты КОМПИЛИРУЮТСЯ явно — вершинный и фрагментный, под целевую
// платформу редактора, — и падение любого из них печатается с текстом.
// Запуск: Unity -batchmode -executeMethod LvnShaderCheck.Check
public static class LvnShaderCheck
{
    public static void Check()
    {
        bool bad = false;
        foreach (var name in new[] { "Hidden/LvnPortalDisk", "Hidden/LvnActorComposite",
                                     "Hidden/LvnSpriteFx", "Hidden/LvnFx", "Hidden/Lvn/Blur" })
        {
            var sh = Shader.Find(name);
            if (sh == null) { Debug.Log($"ШЕЙДЕР {name}: НЕ НАЙДЕН"); bad = true; continue; }

            int n = ShaderUtil.GetShaderMessageCount(sh);
            Debug.Log($"ШЕЙДЕР {name}: поддержан={sh.isSupported} сообщений импортёра={n}");
            if (!sh.isSupported) bad = true;
            if (n > 0)
                foreach (var m in ShaderUtil.GetShaderMessages(sh))
                {
                    Debug.Log($"   [{m.severity}] {m.message} | {m.messageDetails} (строка {m.line}, {m.platform})");
                    if (m.severity == ShaderCompilerMessageSeverity.Error) bad = true;
                }

            // Сборка вариантов под реальную платформу — то, чего не делает -nographics.
            var target = EditorUserBuildSettings.activeBuildTarget;
            var data = ShaderUtil.GetShaderData(sh);
            for (int s = 0; s < data.SubshaderCount; s++)
            {
                var sub = data.GetSubshader(s);
                for (int p = 0; p < sub.PassCount; p++)
                {
                    var pass = sub.GetPass(p);
                    foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                    {
                        var info = pass.CompileVariant(stage, new string[0], ShaderCompilerPlatform.Metal, target);
                        if (info.Success)
                        {
                            Debug.Log($"   вариант {stage} (Metal): собран, {info.ShaderData?.Length ?? 0} байт");
                            continue;
                        }
                        bad = true;
                        Debug.Log($"   вариант {stage} (Metal): НЕ СОБРАН");
                        foreach (var m in info.Messages ?? Enumerable.Empty<ShaderMessage>())
                            Debug.Log($"      [{m.severity}] {m.message} | {m.messageDetails} (строка {m.line})");
                    }
                }
            }
        }
        Debug.Log(bad ? "ИТОГ ШЕЙДЕРОВ: ЕСТЬ ПРОБЛЕМЫ" : "ИТОГ ШЕЙДЕРОВ: всё собирается");
        EditorApplication.Exit(0);
    }
}
