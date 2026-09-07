using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Lvn.EditorTools
{
    /// <summary>
    /// ШЕЙДЕР, КОТОРЫЙ ПРОСЯТ ПО ИМЕНИ, В СБОРКУ САМ НЕ ПОПАДАЁТ.
    ///
    /// <para>Родня <see cref="LinkerPreserve"/>, та же болезнь на другом
    /// материале. <c>Shader.Find</c> ищет среди уже загруженного: в редакторе
    /// загружено всё, поэтому там находится всегда, а в сборке — только то,
    /// что кто-то потянул за собой ссылкой. Шейдер из НЕОБЯЗАТЕЛЬНОГО пакета
    /// не тянет никто: наш код зовёт его по имени, а имя ссылкой не является.
    /// Свои шейдеры движок кладёт в Runtime/Resources и грузит оттуда, но
    /// чужой пакет так не переложишь.</para>
    ///
    /// <para>ЗАМЕР 07.09, журнал с живого устройства (уехал на сервер через
    /// LvnLogShip): «[lvn-async] «ApplyActor» не удалось:
    /// ArgumentNullException: Value cannot be null. Parameter name: shader».
    /// На экране — «от актёра остался только задний фон»: фон обычная
    /// картинка, ему шейдер спайна не нужен, а скелет не собрался вовсе.
    /// Сборка при этом уже несла всю спайн-часть кода (LinkerPreserve), то
    /// есть код доехал, а шейдер нет.</para>
    ///
    /// <para>Кладём такие шейдеры в «всегда включённые» перед сборкой. Список
    /// имён берётся ИЗ КОДА, который их просит, — иначе имя разошлось бы с
    /// именем и отказ вернулся бы молча.</para>
    /// </summary>
    public sealed class ShaderPreserve : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        /// <summary>
        /// Шейдеры, которые движок просит ПО ИМЕНИ и которых иначе в сборке не
        /// будет. Новый необязательный пакет добавляется сюда — иначе он молча
        /// не доедет до устройства, а в редакторе будет работать.
        ///
        /// <para>Имя написано СТРОКОЙ, а не взято у просящего кода: пакет
        /// необязательный, и движок его сборку не видит по замыслу — сослаться
        /// не на что. Ровно так же перечисляет сборки <see
        /// cref="LinkerPreserve"/>. Расхождение этих двух списков с тем, что
        /// код просит на самом деле, стережёт qa/linker-seam-check.sh.</para>
        /// </summary>
        private static readonly string[] AskedByName =
        {
            "Spine/SkeletonGraphic",
        };

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            if (settings == null)
            {
                Debug.LogWarning("[lvn-build] настройки графики не читаются — шейдеры по имени не закреплены");
                return;
            }
            var so = new SerializedObject(settings);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null || !list.isArray)
            {
                Debug.LogWarning("[lvn-build] в настройках графики нет списка «всегда включённых» — шейдеры не закреплены");
                return;
            }

            var уже = new HashSet<Object>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var v = list.GetArrayElementAtIndex(i).objectReferenceValue;
                if (v != null) уже.Add(v);
            }

            int добавлено = 0;
            foreach (var name in AskedByName)
            {
                // В редакторе загружено всё: нет шейдера — значит нет и пакета,
                // и закреплять нечего. Необязательный пакет на то и
                // необязательный: без него сборка идёт как прежде.
                var shader = Shader.Find(name);
                if (shader == null || уже.Contains(shader)) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                уже.Add(shader);
                добавлено++;
            }
            if (добавлено > 0)
            {
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }
            Debug.Log($"[lvn-build] шейдеров по имени закреплено: {добавлено} (всего в списке {list.arraySize})");
        }
    }
}
