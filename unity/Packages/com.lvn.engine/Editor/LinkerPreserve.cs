using System;
using System.IO;
using System.Text;
using UnityEditor.Build;          // IUnityLinkerProcessor
using UnityEditor.Build.Reporting; // BuildReport
using UnityEditor.UnityLinker;     // UnityLinkerBuildPipelineData

namespace Lvn.EditorTools
{
    /// <summary>
    /// СБОРКА, НА КОТОРУЮ НИКТО НЕ ССЫЛАЕТСЯ, ДЛЯ ЛИНКЕРА МЕРТВА.
    ///
    /// <para>Необязательные части движка подключены ШВОМ: ядро о них не знает,
    /// а реализация цепляется сама из <c>[RuntimeInitializeOnLoadMethod]</c> и
    /// заполняет делегаты моста (см. <c>LvnSpineBridge</c>). Ссылок на неё в
    /// коде нет ни одной — в этом и смысл шва. Но ровно поэтому UnityLinker
    /// считает такую сборку недостижимой и выбрасывает целиком.</para>
    ///
    /// <para>ЗАМЕР 07.09 по готовому APK (grep по global-metadata.dat):
    /// <c>LvnSpineBridge</c> в пакете есть, а <c>LvnSpineBootstrap</c>,
    /// <c>LvnSpineFit</c>, <c>LvnSpineFader</c>, <c>Spine.Unity</c>,
    /// <c>SkeletonGraphic</c> и <c>AtlasAssetBase</c> — ни одного. В логе той
    /// же сборки все три сборки скомпилированы и скопированы в
    /// PlayerScriptAssemblies: до линкера доезжают, дальше нет. На устройстве
    /// это выглядит как «спайны не работают» и ничего больше — мост отвечает
    /// <c>Available == false</c>, и сцена молча идёт по ветке «пакета нет».
    /// В редакторе вырезания не бывает, поэтому там всё исправно.</para>
    ///
    /// <para>ПОЧЕМУ НЕ ПРОСТО ФАЙЛ. Первым заходом <c>link.xml</c> был положен
    /// в сам пакет (<c>com.lvn.engine.spine/Runtime/link.xml</c>). Unity его
    /// ИМПОРТИРОВАЛА — это видно в логе, — а линкеру не передала: среди его
    /// <c>--include-link-xml</c> остались только три файла самой Unity
    /// (TypesInScenes, SerializedTypes, AndroidNativeLink). Замерено на
    /// пересборке: список сохранённых типов не изменился ни на один. Поэтому
    /// список отдаётся официальным швом сборки, а не файлом в пакете.</para>
    ///
    /// <para>Корень у линкера ОДИН — сборка проекта (<c>--include-unity-root-
    /// assembly</c>), и всё остальное живо лишь по ссылкам из неё.</para>
    /// </summary>
    public sealed class LinkerPreserve : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        /// <summary>
        /// Сборки, до которых линкер не дойдёт по ссылкам, потому что ссылок
        /// нет по замыслу. Спайн-рантаймы сохраняются целиком: скелет
        /// собирается из ДАННЫХ во время игры (json + атлас), и какие типы
        /// понадобятся, до запуска не знает никто.
        ///
        /// <para>Новый необязательный шов добавляется сюда — иначе он молча
        /// не доедет до устройства, а в редакторе будет работать.</para>
        /// </summary>
        private static readonly string[] SeamAssemblies =
        {
            "Lvn.Engine.Spine",
            "spine-unity",
            "spine-csharp",
        };

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var xml = new StringBuilder();
            xml.AppendLine("<!-- составлен Lvn.EditorTools.LinkerPreserve -->");
            xml.AppendLine("<linker>");
            int kept = 0;
            foreach (var name in SeamAssemblies)
            {
                // Только те, что в проекте ЕСТЬ: имя несуществующей сборки
                // линкер вправе счесть ошибкой, а необязательный пакет на то и
                // необязательный — без него сборка обязана идти как прежде.
                if (!Present(name)) continue;
                xml.AppendLine($"  <assembly fullname=\"{name}\" preserve=\"all\"/>");
                kept++;
            }
            xml.AppendLine("</linker>");

            var dir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "LvnLinkerPreserve.xml");
            File.WriteAllText(path, xml.ToString());
            UnityEngine.Debug.Log($"[lvn-build] сохраняем от линкера сборок: {kept}");
            return path;
        }

        private static bool Present(string assemblyName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
