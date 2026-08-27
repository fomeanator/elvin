using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lvn.Tests
{
    /// <summary>
    /// СТРАЖ: каждый шейдер движка обязан СОБИРАТЬСЯ под графический бэкенд.
    ///
    /// Урок 28.08, оплаченный живым запуском: в шейдере створа портала
    /// локальная переменная называлась `line` — зарезервированное слово HLSL.
    /// Импортёр Unity об этом молчал (<c>isSupported</c> = true, сообщений 0),
    /// потому что варианты под бэкенд компилируются лениво; на живом Metal
    /// вариант падал, Unity подставляла встроенный error-шейдер, и створ
    /// радиусом во весь экран заливал кадр ядовито-розовым.
    ///
    /// Отсюда правило: спрашивать не «поддержан ли шейдер», а «собери его».
    /// </summary>
    public class ShaderVariantTests
    {
        private static readonly string[] EngineShaders =
        {
            "Hidden/LvnPortalDisk",
            "Hidden/LvnActorComposite",
            "Hidden/LvnSpriteFx",
            "Hidden/LvnFx",
            "Hidden/Lvn/Blur",
        };

        // Бэкенд по хозяину машины: мак собирает Metal, всё остальное — D3D11.
        private static ShaderCompilerPlatform Backend =>
            Application.platform == RuntimePlatform.OSXEditor
                ? ShaderCompilerPlatform.Metal
                : ShaderCompilerPlatform.D3D;

        [Test]
        public void EveryEngineShaderCompilesForTheGraphicsBackend()
        {
            var broken = new List<string>();
            foreach (var name in EngineShaders)
            {
                var shader = Shader.Find(name);
                if (shader == null) { broken.Add($"{name}: не найден"); continue; }

                var data = ShaderUtil.GetShaderData(shader);
                for (int s = 0; s < data.SubshaderCount; s++)
                {
                    var sub = data.GetSubshader(s);
                    for (int p = 0; p < sub.PassCount; p++)
                    {
                        var pass = sub.GetPass(p);
                        foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                        {
                            var info = pass.CompileVariant(
                                stage, new string[0], Backend,
                                EditorUserBuildSettings.activeBuildTarget);
                            if (info.Success) continue;
                            var why = string.Join("; ", (info.Messages ?? Enumerable.Empty<ShaderMessage>())
                                .Select(m => $"{m.message} (строка {m.line})"));
                            broken.Add($"{name} · проход {p} · {stage}: {why}");
                        }
                    }
                }
            }

            Assert.IsEmpty(broken,
                "Шейдер, который не собрался, Unity заменяет розовым error-материалом — "
              + "и узнаётся это только на живом экране:\n" + string.Join("\n", broken));
        }
    }
}
