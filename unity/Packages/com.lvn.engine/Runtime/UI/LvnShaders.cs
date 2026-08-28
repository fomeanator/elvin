using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ВЗЯТЬ ШЕЙДЕР ЭФФЕКТА — и сказать, если его нет.
    ///
    /// <para>Пять мест грузили шейдер одинаково и реагировали на пропажу
    /// ПО-РАЗНОМУ: стекло, размытие и эффекты кадра молча поднимали флаг
    /// «шейдера нет» и дальше рисовали без эффекта; композит слоёв и грим актёра
    /// писали предупреждение — на разных языках и разными словами.</para>
    ///
    /// <para>Молчание тут дороже, чем кажется. Шейдер пропадает не «иногда, у
    /// кого-то»: он не попадает в сборку, если его забыли в списке всегда
    /// включённых, и не поддерживается на части устройств. Игрок при этом видит
    /// не поломку, а РОВНУЮ картинку без эффекта — и жалуется на «скучно
    /// выглядит», а не на ошибку. В логе, куда посмотрели бы первым делом, нет
    /// ничего.</para>
    ///
    /// <para>Материал заводится тут же: временный материал эффекта обязан нести
    /// <c>HideAndDontSave</c>, иначе он попадает в сцену и переживает
    /// её. Правило повторялось шестью строками подряд и держалось на
    /// внимательности.</para>
    /// </summary>
    public static class LvnShaders
    {
        private static readonly HashSet<string> _told = new HashSet<string>();

        /// <summary>Шейдер из ресурсов пакета; <c>null</c>, если его нет или он
        /// не поддержан. Жалуется ОДИН раз на имя: доклад раз в кадр превратил
        /// бы лог в шум ровно там, где его читают.</summary>
        public static Shader Load(string name)
        {
            var shader = Resources.Load<Shader>(name);
            if (shader != null && shader.isSupported) return shader;
            if (_told.Add(name))
                Debug.LogWarning($"[lvn-fx] шейдер «{name}» "
                                 + (shader == null ? "не найден в сборке" : "не поддержан этим устройством")
                                 + " — эффект выключен, картинка останется без него");
            return null;
        }

        /// <summary>Материал по шейдеру, спрятанный от сцены. <c>null</c>, если
        /// шейдера нет — вызывающий обязан это пережить.</summary>
        public static Material Material(string name)
        {
            var shader = Load(name);
            return shader == null ? null : new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
    }
}
