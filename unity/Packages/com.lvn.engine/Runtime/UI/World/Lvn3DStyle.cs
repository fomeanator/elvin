using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// ЕДИНЫЙ ВИД сцены: любой 3D-объект, откуда бы он ни пришёл, приводится к
    /// одному кинематографичному стилю новеллы.
    ///
    /// <para>Зачем это в основании движка, а не «на вкус автора». Модели
    /// приходят отовсюду: реалистичная кузница с фотограмметрии, мультяшное
    /// дерево из бесплатного набора, примитив, сгенерированная болванка. У
    /// каждой свои материалы, своя гамма, свой блеск. Поставленные в один кадр,
    /// они читаются как коллаж — и это первое, что отличает любительскую сцену
    /// от изданной. Никакая настройка света коллаж не спасает: чинить надо
    /// материалы.</para>
    ///
    /// <para>Поэтому движок не показывает объект «как есть». Он перекладывает
    /// его на СВОЙ шейдер, сохраняя то, что несёт смысл (текстуру, базовый
    /// цвет, прозрачность) и отбрасывая то, что несёт чужой стиль (блеск,
    /// микрорельеф, чужую модель освещения). Автор получает единую картинку
    /// бесплатно, а не собирает её вручную по объекту.</para>
    ///
    /// <para>Стиль выбран рисованный, а не реалистичный. Так выглядят
    /// корейские и японские 3D-новеллы, и на то есть причина: рисованная
    /// картинка прощает дешёвую геометрию, а реалистичная её подчёркивает.
    /// Один и тот же конус в стилизованном свете читается елью, а в
    /// физкорректном — конусом.</para>
    /// </summary>
    public static class Lvn3DStyle
    {
        /// <summary>Настройки вида. Значения по умолчанию — наш стандарт:
        /// холодная цветная тень, две ступени света, тонкий ободок.</summary>
        public struct Profile
        {
            public Color ShadowTint;   // цвет теневой стороны
            public float Steps;        // ступеней света: 2 — рисованно, 4 — мягче
            public float Softness;     // мягкость границы света и тени
            public Color RimColor;     // контровой ободок по силуэту
            public float RimStrength;
            public float RimPower;
            /// <summary>Тёплая кайма на границе света и тени. Приём японских и
            /// китайских 3D-игр, но в НОЧНОЙ сцене он же — первый источник
            /// пересвета: тепло по каждому краю складывается с фонарём, и
            /// камень становится розовым пятном.</summary>
            public float WarmEdge;

            public static Profile Default => new Profile
            {
                ShadowTint = new Color(0.35f, 0.42f, 0.60f),
                Steps = 2f,
                Softness = 0.05f,
                RimColor = new Color(0.70f, 0.85f, 1f),
                RimStrength = 0.55f,
                RimPower = 3f,
                WarmEdge = 0.18f,
            };
        }

        private static Profile _profile = Profile.Default;
        private static bool _enabled = true;

        public static Profile Current => _profile;
        public static bool Enabled => _enabled;

        public static void SetProfile(Profile p)
        {
            _profile = p;
            // Профиль меняет вид УЖЕ СТОЯЩИХ тел: автор правит стиль сцены и
            // ждёт, что изменится кадр, а не следующая сцена.
            foreach (var kv in _converted)
            {
                var m = kv.Value;
                if (m == null) continue;
                ApplyProfile(m);
            }
        }
        public static void SetEnabled(bool on) => _enabled = on;

        // Переведённые материалы помним: один и тот же материал встречается на
        // десятках объектов набора, и переводить его каждый раз значит плодить
        // копии — то есть вызовы отрисовки, ровно то, чего мы избегаем.
        private static readonly Dictionary<Material, Material> _converted
            = new Dictionary<Material, Material>();

        /// <summary>Привести к общему виду всё, что висит под этим корнем.
        /// Возвращает число переложенных рендереров.</summary>
        public static int Apply(GameObject root)
        {
            if (!_enabled || root == null) return 0;
            var toon = Resources.Load<Shader>("LvnToon");
            if (toon == null) return 0;

            int touched = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                // Плоские фигуры сцены (спрайты персонажей) НЕ трогаем: их арт
                // уже нарисован со своим светом, и «улучшать» его светом сцены
                // значит его испортить.
                if (r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                    r.sharedMaterial.shader.name.StartsWith("Unlit")) continue;
                // Частицы (дым, искры) — по имени типа, а не по типу: модуль
                // частиц в сборке может быть выключен, и прямая ссылка тогда
                // ломает КОМПИЛЯЦИЮ всего движка ради одной проверки.
                if (r.GetType().Name == "ParticleSystemRenderer") continue;

                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    // Материал уже наш: сохраняем его специализацию (земля
                    // останется triplanar, вода — водой), но регистрируем для
                    // последующих смен погоды. Раньше любой Lvn-материал из
                    // bundle безусловно превращался обратно в обычный toon.
                    if (m.shader == toon || (m.shader != null &&
                        m.shader.name.StartsWith("Lvn/", System.StringComparison.Ordinal)))
                    {
                        ApplyProfile(m);
                        _converted[m] = m;
                        continue;
                    }
                    mats[i] = Convert(m, toon);
                    changed = true;
                }
                if (changed) { r.sharedMaterials = mats; touched++; }
            }
            return touched;
        }

        private static void ApplyProfile(Material m)
        {
            if (m == null) return;
            if (m.HasProperty("_ShadowTint")) m.SetColor("_ShadowTint", _profile.ShadowTint);
            if (m.HasProperty("_Steps")) m.SetFloat("_Steps", _profile.Steps);
            if (m.HasProperty("_Softness")) m.SetFloat("_Softness", _profile.Softness);
            if (m.HasProperty("_RimColor")) m.SetColor("_RimColor", _profile.RimColor);
            if (m.HasProperty("_RimStrength")) m.SetFloat("_RimStrength", _profile.RimStrength);
            if (m.HasProperty("_RimPower")) m.SetFloat("_RimPower", _profile.RimPower);
            if (m.HasProperty("_WarmEdge")) m.SetFloat("_WarmEdge", _profile.WarmEdge);
        }

        /// <summary>Переложить один материал на наш шейдер, сохранив смысл.</summary>
        private static Material Convert(Material src, Shader toon)
        {
            if (_converted.TryGetValue(src, out var done) && done != null) return done;

            var m = new Material(toon) { name = src.name + " (стиль)" };
            // Текстура и цвет — ЭТО и есть объект. Всё остальное (блеск,
            // металличность, микрорельеф) — чужой стиль, и он отбрасывается.
            m.mainTexture = FindTexture(src);
            m.color = FindColor(src);

            CarryCutout(src, m);
            CarryVertexAO(src, m);

            m.SetColor("_ShadowTint", _profile.ShadowTint);
            m.SetFloat("_Steps", _profile.Steps);
            m.SetFloat("_Softness", _profile.Softness);
            m.SetColor("_RimColor", _profile.RimColor);
            m.SetFloat("_RimStrength", _profile.RimStrength);
            m.SetFloat("_RimPower", _profile.RimPower);
            m.SetFloat("_WarmEdge", _profile.WarmEdge);
            m.enableInstancing = true;

            _converted[src] = m;
            return m;
        }

        /// <summary>Найти текстуру объекта, КАК БЫ ЕЁ НИ ЗВАЛИ.
        ///
        /// <para>Мы обещаем принять любую модель, а «любая» означает и чужой
        /// шейдер: у пакетов растительности он почти всегда свой, со своим
        /// ветром и своими именами полей. Искать только <c>_MainTex</c> — значит
        /// не найти ничего у половины наборов и поставить объект белым; ровно
        /// так побелела хвоя дуэльного леса.</para>
        ///
        /// <para>Сначала пробуем знакомые имена (это дёшево и покрывает
        /// Standard, URP и HDRP), а если не вышло — перебираем поля шейдера и
        /// берём первую текстуру, которая не является служебной картой. Порядок
        /// полей в шейдере не случаен: основная текстура почти всегда объявлена
        /// первой.</para></summary>
        private static Texture FindTexture(Material src)
        {
            if (src == null) return null;
            foreach (var n in KnownTextureNames)
                if (src.HasProperty(n))
                {
                    var t = src.GetTexture(n);
                    if (t != null) return t;
                }

            var sh = src.shader;
            if (sh == null) return null;
            int count = sh.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var name = sh.GetPropertyName(i);
                if (IsServiceMap(name)) continue;
                var t = src.GetTexture(name);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Найти цвет объекта — та же задача и та же беда с именами.</summary>
        private static Color FindColor(Material src)
        {
            if (src == null) return Color.white;
            foreach (var n in KnownColorNames)
                if (src.HasProperty(n)) return src.GetColor(n);

            var sh = src.shader;
            if (sh == null) return Color.white;
            int count = sh.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                var name = sh.GetPropertyName(i);
                // Цвет свечения и цвет подсветки — не цвет самого объекта.
                if (name.IndexOf("Emiss", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("Spec", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return src.GetColor(name);
            }
            return Color.white;
        }

        private static readonly string[] KnownTextureNames =
        {
            "_MainTex",        // Standard и почти всё написанное вручную
            "_BaseMap",        // URP
            "_BaseColorMap",   // HDRP
            "_MainTexture", "_Albedo", "_AlbedoMap", "_Diffuse", "_DiffuseMap", "_Texture",
        };

        private static readonly string[] KnownColorNames =
        {
            "_Color", "_BaseColor", "_MainColor", "_TintColor", "_Tint",
        };

        // Служебные карты несут не вид, а данные о поверхности: подставить их
        // вместо текстуры — значит покрасить дерево картой шероховатости.
        private static bool IsServiceMap(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string[] marks = { "Bump", "Normal", "Mask", "Metal", "Smooth", "Rough",
                               "Occlusion", "AO", "Emiss", "Height", "Parallax", "Detail",
                               "Spec", "Gloss", "Noise", "Ramp" };
            foreach (var mk in marks)
                if (name.IndexOf(mk, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Перенести признак «затенение лежит в вершинах».
        ///
        /// <para>Без этого переноса запечённое AO терялось ровно там, где оно
        /// нужнее всего: конвертер создаёт НОВЫЙ материал, и флаг, поднятый на
        /// исходном, до него не доезжает. Дефект при этом молчит — сцена просто
        /// теряет контакт с землёй, и объяснить это нечем.</para></summary>
        private static void CarryVertexAO(Material src, Material m)
        {
            if (src == null || m == null) return;
            if (!src.HasProperty("_VertexAO") || !m.HasProperty("_VertexAO")) return;
            m.SetFloat("_VertexAO", src.GetFloat("_VertexAO"));
        }

        /// <summary>Перенести ПРОЗРАЧНОСТЬ оригинала.
        ///
        /// <para>Крона дерева, решётка ворот, цепь, волосы — в чужих наборах это
        /// не геометрия, а плоскости с прозрачной текстурой. Наш toon по природе
        /// непрозрачен, и без этого переноса он рисует такую плоскость целиком:
        /// вместо ветвей в кадре висят белые квадраты. Именно так выглядела
        /// листва дуэльного леса.</para>
        ///
        /// <para>Как узнаём. Прямого признака «здесь вырезание» у материала нет,
        /// поэтому смотрим на три следа, которые оставляет любой конвейер:
        /// метку типа поверхности, собственный порог у исходного шейдера и
        /// очередь отрисовки. Хватает любого — лишний порог безвреден
        /// (у непрозрачной текстуры альфа равна единице и ничего не режет),
        /// а пропущенный виден в кадре сразу.</para></summary>
        private static void CarryCutout(Material src, Material m)
        {
            if (src == null || m == null) return;

            bool cut = false;
            var type = src.GetTag("RenderType", false, "");
            if (type == "TransparentCutout" || type == "Transparent" || type == "TreeLeaf")
                cut = true;
            if (!cut && src.HasProperty("_Cutoff") && src.GetFloat("_Cutoff") > 0.001f)
                cut = true;
            // 2450 — граница «вырезание», 3000 — «полупрозрачное». И то и другое
            // мы рисуем порогом: сортировать сотню плоскостей кроны по глубине
            // невозможно, а резкий край листу идёт больше мягкого.
            if (!cut && src.renderQueue >= 2450) cut = true;

            if (!cut) return;

            float cutoff = src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0f;
            m.SetFloat("_Cutoff", cutoff > 0.001f ? cutoff : 0.5f);
            m.SetFloat("_Cull", 0f);            // лист виден с обеих сторон
            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.renderQueue = 2450;
        }

        /// <summary>Переложить материал ветра, сохранив качание: у растительности
        /// смысл несёт не только цвет, но и движение.</summary>
        public static Material Windify(Material src, float wind)
        {
            var shader = Resources.Load<Shader>("LvnWind");
            if (shader == null) return src;
            var m = new Material(shader) { name = (src != null ? src.name : "wind") + " (ветер)" };
            if (src != null)
            {
                if (src.HasProperty("_MainTex")) m.mainTexture = src.mainTexture;
                if (src.HasProperty("_Color")) m.color = src.color;
                // Ветер чаще всего и достаётся кроне — прозрачность ей нужнее,
                // чем кому-либо ещё.
                CarryCutout(src, m);
            CarryVertexAO(src, m);
            }
            m.SetFloat("_Wind", wind);
            m.enableInstancing = true;
            return m;
        }

        /// <summary>Забыть переводы: набор сменился, старые материалы уходят
        /// вместе с ним, и держать их — течь памяти на каждой сцене.</summary>
        public static void Forget() => _converted.Clear();
    }
}
