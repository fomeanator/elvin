using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Тела и свет сцены, собираемые ИЗ СКРИПТА (`o3d`, `light`).
    ///
    /// <para>Здесь заканчивается «3D-набор как готовый бандл» и начинается 3D
    /// как часть языка: автор ставит коробку, плоскость и фонарь строкой в
    /// `.lvns`, не открывая Unity. Ровно та же идея, что у примитивов Roblox —
    /// низкий порог входа важнее фотореализма: комнатой из шести плоскостей и
    /// десятком коробок место собирается за пять минут и без художника, а
    /// модели ложатся туда же потом, теми же метрами.</para>
    ///
    /// <para>Разделение с основным файлом — по смыслу: там КАДР (камера,
    /// съёмка, буфер), здесь СОДЕРЖИМОЕ.</para>
    /// </summary>
    public sealed partial class Lvn3DBackdrop
    {
        /// <summary>Что поставить в сцену. Пустые поля — «не менять»: команда
        /// `o3d id=камень color=#fff` не должна двигать камень с места.</summary>
        public struct Body
        {
            public string Shape;     // примитив
            public float Hills;      // размах высот земли, м (0 — ровная)
            public float HillSize;   // длина волны рельефа, м
            public float Detail;     // доля мелкой неровности, 0…1
            public int Cells;        // плотность сетки земли (0 — сама)
            public string Model;     // имя объекта в наборе (готовая модель)
            // Геометрия, ПРИШЕДШАЯ ТЕКСТОМ (`model="/content/models/камень.obj"`).
            // Разбирается до постановки — сюда попадает уже готовый меш.
            public Mesh Mesh;
            public Texture Sprite;   // плоская фигура, развёрнутая к зрителю
            public Vector3? Pos;
            public Vector3? Size;
            public Vector3? Rot;     // pitch, yaw, roll
            public Color? Tint;
            public Texture Texture;
            public float? Alpha;
            public float? Glow;
            public bool? Ground;     // посадить основанием на грунт
            public bool? Shadow;
            // Посев: одно тело — много копий. Лес, трава, камни, толпа.
            public int Count;        // 0/1 — обычное одиночное тело
            public Vector2 Area;     // площадь посева в метрах (ширина, глубина)
            public int Seed;         // одна и та же роща при каждом запуске
            public float ScaleVar;   // разброс размера, доля (0.3 = ±30%)
            public float YawVar;     // разброс поворота, градусы
            public float Gap;        // минимальный просвет между копиями, метры
            public string[] Kinds;   // несколько видов в одном посеве: ель, куст, валун
            public Color[] Tints;    // и несколько окрасов — по виду
            public float Wind;       // сила качания в метрах (0 — неподвижно)
            public float? Fade;      // с какого расстояния копии перестают рисоваться
            public string Shader;     // вид поверхности из каталога
            public Texture Normal;    // карта нормалей: рельеф поверхности
            public float? Bump;       // сила рельефа
            public float? Tiling;     // метров на повтор текстуры (трипланар)
            // Дорога — не просто ещё одна текстура земли. Эти три числа
            // управляют её силуэтом и состоянием, не требуя отдельных масок:
            // край рвётся процедурно, колеи выводятся из поперечной координаты,
            // влажность живёт только внутри них.
            public float? RoadEdge;   // насколько глубоко рвётся край, 0…1
            public float? RoadRuts;   // выраженность двух колей, 0…1
            public float? RoadWet;    // влажность колей, 0…1
            public AudioClip Sound;   // звук, идущий ОТ этого тела
            public float? SoundRange; // с какого расстояния он слышен, метры
            public float? SoundVolume;
            // Контровой ободок НА ЭТОМ ТЕЛЕ. Обычно он задаётся сценой целиком
            // (`bg3d rim=`), и это правильно: свет один на всех. Но ободок —
            // ещё и средство выделения: герою он нужен, дальнему камню нет, а
            // измерительному образцу мешает вовсе. Пустое поле — «как у сцены».
            public float? Rim;
            public float? Outline;    // толщина обводки в метрах (0 — без неё)
            public Color? OutlineTint;
            // Точные места копий: «x,z;x,z;…». Так ставит карта — там место
            // каждой стены и каждого дерева известно, а не разбрасывается.
            public Vector2[] Spots;
            // Движение: переезд за секунды и постоянные движения.
            public float Dur;         // 0 — мгновенно
            public float? Dissolve;   // 0…1 — растворение (шейдер dissolve)
            public float? Spin;       // градусов в секунду вокруг своей оси
            public Vector3? Bob;      // покачивание, метры
            public float? BobSpeed;
            public float? Pulse;      // пульсация размера, доля
            public float? PulseSpeed;
        }

        private readonly Dictionary<string, Transform> _bodies = new Dictionary<string, Transform>();
        // ЧТО ПРОСИЛИ поставить, а не что получилось. Набор приезжает по сети и
        // нередко ПОЗЖЕ команд сцены: тело с `model=` в этот момент моделью
        // стать не может и встаёт коробкой-заглушкой. Заглушка потом никем не
        // заменяется — перенос содержимого в пришедший набор спасает место
        // тела, но не его геометрию, и кадр остаётся набором белых кубов.
        // Держим исходные описания, чтобы собрать такие тела заново, когда
        // модели наконец появятся.
        private readonly Dictionary<string, Body> _bodySpecs = new Dictionary<string, Body>();
        // Метки перехода по нажатию: id тела → куда прыгать. Держим отдельно от
        // самих тел, потому что кликабельность — свойство сценария, а не сцены.
        private readonly Dictionary<string, string> _clicks = new Dictionary<string, string>();
        private readonly Dictionary<string, Light> _lights = new Dictionary<string, Light>();

        /// <summary>Сколько тел стоит в сцене — с копиями посева.</summary>
        public int BodyCount
        {
            get
            {
                int n = 0;
                foreach (var kv in _bodies)
                    if (kv.Value != null) n += Mathf.Max(1, kv.Value.childCount);
                return n;
            }
        }

        /// <summary>Пустая сцена: набор без префаба, который наполняет скрипт.
        /// Возвращает false, если сцена уже стоит — повторный `bg3d build=1`
        /// не должен сносить то, что автор уже построил.</summary>
        public bool BuildEmpty()
        {
            if (_set != null) return false;
            EnsureCamera();
            EnsureTarget();   // буфер кадра: без него снимать некуда
            _set = new GameObject("lvn-3d-built");
            _set.transform.position = Far;
            _groundVerts = null; _groundFlatY = 0f; // пол пустой сцены — уровень 0
            _hasTerrain = false; _groundMeshIds.Clear(); _scatterTotal = 0;
            Shoot();
            return true;
        }

        /// <summary>Пересобрать тела, которым нужны модели ПРИШЕДШЕГО набора.
        ///
        /// <para>Вызывается сразу после постановки набора. Трогаем только те,
        /// что просили модель и её тогда не нашли: обычное тело из примитивов
        /// уже стоит правильно, и пересобирать его — терять его состояние.</para></summary>
        private void RebuildBodiesNeedingSet()
        {
            if (_set == null || _bodySpecs.Count == 0) return;
            List<string> retry = null;
            foreach (var kv in _bodySpecs)
            {
                var b = kv.Value;
                bool wantsModel = !string.IsNullOrEmpty(b.Model)
                                  || (b.Kinds != null && b.Kinds.Length > 0);
                if (!wantsModel) continue;
                // Модели всё ещё нет — пересборка ничего не даст.
                if (!string.IsNullOrEmpty(b.Model) && FindInSet(b.Model) == null) continue;
                // Тело УЖЕ собрано из модели — не трогаем: пересборка стёрла бы
                // его движение, клик и всё, что успело на него навеситься.
                // Заглушку узнаём по мешу: примитивы движка зовутся `lvn-shape-*`.
                if (_bodies.TryGetValue(kv.Key, out var cur) && cur != null)
                {
                    var mf = cur.GetComponent<MeshFilter>();
                    bool stub = mf != null && mf.sharedMesh != null
                                && mf.sharedMesh.name.StartsWith("lvn-shape-");
                    bool grove = cur.childCount > 0;   // посев: проверяем по копии
                    if (grove)
                    {
                        var child = cur.GetChild(0).GetComponent<MeshFilter>();
                        stub = child != null && child.sharedMesh != null
                               && child.sharedMesh.name.StartsWith("lvn-shape-");
                    }
                    if (!stub) continue;
                }
                (retry ??= new List<string>()).Add(kv.Key);
            }
            if (retry == null) return;

            foreach (var id in retry)
            {
                var spec = _bodySpecs[id];
                if (_bodies.TryGetValue(id, out var old) && old != null) Kill(old.gameObject);
                _bodies.Remove(id);
                _groundMeshIds.Remove(id);
                SetBody(id, spec);
            }
            Debug.Log($"[lvn-3d] пересобрано по моделям набора: {retry.Count} тел(а)");
        }

        /// <summary>Если в текстуре есть прозрачность — вырезать её порогом.
        ///
        /// <para>Автор пишет `texture="/content/textures/листва.png"` и вправе
        /// ожидать листву, а не чёрный прямоугольник. Прозрачность — свойство
        /// самой картинки, движок видит его сам, и требовать отдельного слова
        /// значило бы требовать знания о том, как устроен файл.</para></summary>
        private static void ApplyCutout(Material m, Texture tex)
        {
            if (m == null || !LvnTextures.HasAlpha(tex)) return;
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            // Лист плоский: у повёрнутой изнанкой карточки лицевых граней нет,
            // и половина кроны просто исчезла бы.
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.renderQueue = 2450;
        }

        // Что сказал о свете СКРИПТ. Держим отдельно от того, что стоит
        // сейчас: набор может прийти позже и перебить.
        private struct ScriptLight
        {
            public bool Off;
            public Color? Top, Bottom, Tint;
            public float? Near, Far, Power;
        }
        private ScriptLight? _scriptSky, _scriptFog;

        private void RememberScriptLight(string kind, bool off, Color? top, Color? bottom,
                                         Color? color, float? near, float? far, float? power, float dur)
        {
            var v = new ScriptLight { Off = off, Top = top, Bottom = bottom,
                                      Tint = color, Near = near, Far = far, Power = power };
            if (kind == "sky") _scriptSky = v;
            else if (kind == "fog") _scriptFog = v;
        }

        /// <summary>Повторить свет, заданный скриптом, поверх паспорта набора.
        /// Вызывается сразу после постановки набора.</summary>
        private void ReapplyScriptLight()
        {
            if (_set == null) return;
            if (_scriptSky is ScriptLight sky)
            {
                EnsureEnv().SetSky(!sky.Off, sky.Top, sky.Bottom, sky.Tint, 0f);
                ApplySkybox(sky.Top, sky.Bottom, sky.Tint, sky.Power, sky.Off);
            }
            if (_scriptFog is ScriptLight fog)
                EnsureEnv().SetFog(!fog.Off, fog.Tint, fog.Near, fog.Far, 0f);
            if (_scriptShadowDist is float sd)
            {
                EnsureEnv().shadowDistance = sd;
                EnsureEnv().Reapply();
            }
            if (_scriptSky != null || _scriptFog != null)
                Debug.Log("[lvn-3d] свет скрипта восстановлен поверх паспорта набора");
        }

        /// <summary>Дальность теней в метрах (`bg3d shadows=`).
        ///
        /// <para>Как и остальной свет, это слово АВТОРА: набор приносит своё
        /// умолчание, но сцена, которая знает, что тянется на сотню метров,
        /// вправе попросить больше.</para></summary>
        public void SetShadowDistance(float meters)
        {
            _scriptShadowDist = Mathf.Clamp(meters, 5f, 300f);
            if (_set == null) return;
            EnsureEnv().shadowDistance = _scriptShadowDist.Value;
            EnsureEnv().Reapply();
            Shoot();
        }

        private float? _scriptShadowDist;

        /// <summary>Земля ли это — форма, которая несёт рельеф.</summary>
        private static bool IsGround(string shape)
        {
            switch ((shape ?? "").Trim().ToLowerInvariant())
            {
                case "ground": case "земля": case "terrain": return true;
                default: return false;
            }
        }

        // Рельеф последней поставленной земли: по нему считаются высоты под
        // телами. Одна земля на сцену — как в жизни.
        private Lvn3DTerrain.Spec _terrain;
        private bool _hasTerrain;
        private Vector3 _terrainOrigin;

        private Mesh BuildGround(string id, in Body b)
        {
            var size = b.Size ?? new Vector3(60f, 1f, 60f);
            float sx = Mathf.Max(0.5f, Mathf.Abs(size.x));
            float sz = Mathf.Max(0.5f, Mathf.Abs(size.z));

            RememberScriptGround(b);

            var mesh = Lvn3DTerrain.Build(sx, sz, _terrain, b.Cells);
            // Меш уже в метрах — масштабу тела здесь делать нечего.
            _groundMeshIds.Add(id);
            return mesh;
        }

        /// <summary>Восстановить формулу процедурной земли после смены
        /// сетевого набора. SetSet сбрасывает пол пришедшего префаба, но сама
        /// земля скрипта переезжает вместе с остальными телами; если забыть её
        /// паспорт, последующая пересборка моделей посадит их на пол бандла
        /// (у кладбища это было −49.6 м), а не на видимый грунт.</summary>
        private void RestoreScriptGround()
        {
            foreach (var pair in _bodySpecs)
            {
                if (!IsGround(pair.Value.Shape)) continue;
                RememberScriptGround(pair.Value);
                return; // одна земля на сцену — контракт поля выше
            }
        }

        /// <summary>Повторно посадить перенесённые тела на восстановленную
        /// землю. Не все они пересобираются после прихода набора: уже найденная
        /// в кэше модель остаётся прежней, но перепривязка transform могла
        /// сохранить её старую мировую высоту. Посев исправляем по каждой
        /// копии — рельеф под ними разный.</summary>
        private void RegroundScriptBodies()
        {
            foreach (var pair in _bodySpecs)
            {
                var b = pair.Value;
                if (!_bodies.TryGetValue(pair.Key, out var root) || root == null) continue;
                bool scattered = b.Count > 1 || (b.Spots != null && b.Spots.Length > 0);
                if (scattered)
                {
                    if (b.Ground == false) continue;
                    var origin = root.localPosition;
                    for (int i = 0; i < root.childCount; i++)
                    {
                        var child = root.GetChild(i);
                        var p = child.localPosition;
                        var at = new Vector3(origin.x + p.x, 0f, origin.z + p.z);
                        p.y = GroundHeightAt(at) - origin.y;
                        child.localPosition = p;
                    }
                }
                else if (b.Ground == true && b.Pos is Vector3 p)
                {
                    p.y = GroundHeightAt(p);
                    root.localPosition = p;
                }
            }
        }

        private void RememberScriptGround(in Body b)
        {
            _terrain = new Lvn3DTerrain.Spec
            {
                Hills = Mathf.Max(0f, b.Hills),
                // Холм шириной с саму землю — это не холм, а наклон. Двадцать
                // метров: пологая волна, по которой можно пройти, и на которой
                // виден и гребень, и ложбина.
                HillSize = b.HillSize > 0.01f ? b.HillSize : 20f,
                Detail = b.Detail > 0f ? Mathf.Clamp01(b.Detail) : 0.35f,
                Seed = b.Seed,
            };
            _hasTerrain = !_terrain.Flat;
            _eyeHeightKnown = false;   // земля сменилась — рост глаз меряем заново
            _terrainOrigin = b.Pos ?? Vector3.zero;
        }

        // Земля строится по своему размеру, поэтому масштаб к ней не применяем.
        private readonly HashSet<string> _groundMeshIds = new HashSet<string>();

        /// <summary>Сколько копий посева движок ставит на всю сцену. Полторы
        /// тысячи — это плотное поле или густой лес; больше на телефоне даёт
        /// рваный кадр, ради которого никакая новелла не пишется.</summary>
        private const int MaxScatterTotal = 1500;
        private int _scatterTotal;
        // Собственная ориентация моделей: её нельзя терять при повороте.
        private readonly Dictionary<string, Quaternion> _modelRot = new Dictionary<string, Quaternion>();

        /// <summary>Поставить или изменить тело. false — не удалось (нет сцены
        /// или не нашлось ни формы, ни модели).</summary>
        public bool SetBody(string id, in Body b)
        {
            if (string.IsNullOrEmpty(id) || _set == null) return false;
            _bodySpecs[id] = b;
            if (b.Count > 1 || (b.Spots != null && b.Spots.Length > 0)) return Scatter(id, b);

            if (!_bodies.TryGetValue(id, out var t) || t == null)
            {
                GameObject go = null;
                if (!string.IsNullOrEmpty(b.Model))
                {
                    // Модель из набора: берём готовый объект и КЛОНИРУЕМ его,
                    // чтобы автор мог поставить три сундука там, где художник
                    // положил один.
                    var src = FindInSet(b.Model);
                    if (src != null)
                    {
                        go = Object.Instantiate(src, _set.transform);
                        RestoreMeshes(go, src.transform);
                        _modelRot[id] = src.transform.localRotation;   // склейка набора не должна ехать с копией
                        go.name = "lvn-body-" + id;
                        go.SetActive(true);
                    }
                }
                if (go == null)
                {
                    // Земля с холмами строится по размеру САМОГО тела: рельеф
                    // живёт в вершинах, и растягивать его масштабом нельзя —
                    // вместе с землёй растянулись бы и холмы.
                    // Порядок важен: своя геометрия сильнее и формы, и земли —
                    // если автор дал модель файлом, он именно её и хочет.
                    var mesh = b.Mesh != null
                        ? b.Mesh
                        : (IsGround(b.Shape) ? BuildGround(id, b) : Lvn3DShapes.Get(b.Shape));
                    if (mesh == null) return false;
                    go = new GameObject("lvn-body-" + id, typeof(MeshFilter), typeof(MeshRenderer));
                    go.transform.SetParent(_set.transform, false);
                    go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    go.GetComponent<MeshRenderer>().sharedMaterial =
                        LitMaterial(b.Tint ?? Color.white, b.Shader);
                }
                t = go.transform;
                _bodies[id] = t;
            }

            Vector3? goPos = null;
            if (b.Pos is Vector3 p)
            {
                if (b.Ground == true) p.y = GroundHeightAt(p);
                goPos = p;
            }
            bool moves = b.Dur > 0f || b.Dissolve != null || b.Spin != null
                         || b.Bob != null || b.Pulse != null;
            if (moves)
            {
                // Движение живёт во времени и идёт САМО, пока идёт диалог:
                // дверь открывается, факел качается, призрак растворяется.
                var motion = Lvn3DMotion.Ensure(t, this);
                motion.enabled = true;
                motion.SetLoops(b.Spin, b.Bob, b.BobSpeed, b.Pulse, b.PulseSpeed);
                motion.MoveTo(goPos, b.Rot, b.Size, b.Dissolve, b.Dur);
            }
            else
            {
                if (goPos is Vector3 gp) t.localPosition = gp;
                if (b.Rot is Vector3 r)
                {
                    // Та же причина, что и в посеве: поворот автора — ПОВЕРХ
                    // ориентации модели, иначе она ложится набок.
                    var own = _modelRot.TryGetValue(id, out var ownRot) ? ownRot : Quaternion.identity;
                    t.localRotation = Quaternion.Euler(r.x, r.y, r.z) * own;
                }
                // Земля уже построена в метрах: растянуть её масштабом значило бы
                // растянуть и холмы, а высота холма — величина осмысленная.
                if (b.Size is Vector3 s && !_groundMeshIds.Contains(id))
                    t.localScale = string.IsNullOrEmpty(b.Model) ? s : ScaleForHeight(t.gameObject, s);
            }

            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (b.Tint is Color c || b.Texture != null || b.Alpha is float || b.Glow is float)
                {
                    // Материал СВОЙ у каждого тела, у которого свой вид: общий
                    // на всех превратил бы смену цвета одной коробки в смену
                    // цвета всей сцены.
                    var mat = mr.material;
                    // СВОЯ ТЕКСТУРА НА ЧУЖОЙ ГЕОМЕТРИИ — всегда трипланаром.
                    //
                    // Развёртка купленной модели сделана под ЕЁ атлас: у кита
                    // это крошечный квадрат общей палитры. Наша текстура,
                    // положенная по такой развёртке, растягивает несколько
                    // пикселей на всю стену — получаются полосы, и выглядит это
                    // как «плохая текстура», хотя дело в чужих UV.
                    //
                    // Автор про развёртки знать не обязан: он сказал «камень» —
                    // должен увидеть камень. Поэтому подменяем способ наложения
                    // молча, а явный shader= у автора всегда сильнее.
                    if (b.Texture != null && !string.IsNullOrEmpty(b.Model)
                        && string.IsNullOrEmpty(b.Shader))
                    {
                        var tri = FindShader("triplanar");
                        if (tri != null) mat.shader = tri;
                    }
                    if (b.Tint is Color tint)
                    {
                        var col = mat.color;
                        mat.color = new Color(tint.r, tint.g, tint.b, col.a);
                    }
                    if (b.Texture != null)
                    {
                        mat.mainTexture = b.Texture;
                        ApplyCutout(mat, b.Texture);
                    }
                    if (b.Normal != null && mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", b.Normal);
                    if (b.Bump is float bs && mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", bs);
                    if (b.Tiling is float tl && mat.HasProperty("_Tiling")) mat.SetFloat("_Tiling", tl);
                    ApplyRoad(mat, b);
                    // Обводка стоит ВТОРОГО вызова отрисовки на объект, поэтому
                    // включается штучно — на том, что близко и важно.
                    if (b.Rim is float rimv && mat.HasProperty("_RimStrength")) mat.SetFloat("_RimStrength", rimv);
                    if (mat.HasProperty("_PlantHeight"))
                        mat.SetFloat("_PlantHeight", Mathf.Max(0.05f, b.Size?.y ?? 1f));
                    if (b.Outline is float ol && mat.HasProperty("_Outline")) mat.SetFloat("_Outline", ol);
                    if (b.OutlineTint is Color oc && mat.HasProperty("_OutlineColor")) mat.SetColor("_OutlineColor", oc);
                    if (b.Alpha is float a) SetAlpha(mat, a);
                    if (b.Glow is float g) SetGlow(mat, g, b.Tint ?? mat.color);
                    // Дым — тот же материал огня, но серый и тяжёлый: одна
                    // реализация вместо двух почти одинаковых шейдеров.
                    if (IsSmoke(b.Shader) && mat.HasProperty("_Smoke")) mat.SetFloat("_Smoke", 1f);
                }
                if (b.Shadow is bool sh)
                    mr.shadowCastingMode = sh
                        ? UnityEngine.Rendering.ShadowCastingMode.On
                        : UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // ЗВУК ОТ ТЕЛА. Костёр трещит там, где горит, — половина
            // присутствия в сцене, которую осматривают свайпом: без привязки к
            // месту звук читается как радио в комнате, а не как этот костёр.
            if (b.Sound != null && _cam != null)
                Lvn3DSound.Ensure(t.gameObject, t, _cam)
                    .Play(b.Sound, b.SoundVolume ?? 1f, b.SoundRange ?? 12f);

            // Текстура земли грузится по сети, а остальные o3d уже могут быть
            // поставлены на временный пол набора. Как только настоящая земля
            // готова, пересаживаем их по её формуле: результат больше не
            // зависит от того, что быстрее — бандл, albedo или normal map.
            if (IsGround(b.Shape)) RegroundScriptBodies();

            Shoot();
            return true;
        }

        /// <summary>ПОСЕВ: одно описание — целая роща.
        ///
        /// <para>Лес нельзя написать построчно: сто деревьев — это сто строк,
        /// которые никто не станет ни писать, ни править. Поэтому автор задаёт
        /// ЧТО, СКОЛЬКО и НА КАКОЙ ПЛОЩАДИ, а места разбрасывает движок.</para>
        ///
        /// <para>Разброс ДЕТЕРМИНИРОВАННЫЙ: одно и то же зерно даёт одну и ту же
        /// рощу при каждом запуске. Иначе сцена меняется от захода к заходу, и
        /// автор не может ни поставить в ней персонажа, ни снять кадр дважды
        /// одинаково.</para>
        ///
        /// <para>Копии делят ОДИН меш и ОДИН материал — Unity сливает такие в
        /// один вызов отрисовки (инстансинг). Сто деревьев стоят почти как
        /// одно; сто РАЗНОЦВЕТНЫХ стоили бы сотню вызовов, поэтому разброс идёт
        /// по размеру и повороту, а не по цвету.</para></summary>
        private bool Scatter(string id, in Body b)
        {
            // Пересев — с нуля: менять число копий на живой роще дороже и
            // запутаннее, чем построить заново (это делается раз на сцену).
            RemoveBody(id);

            // Несколько видов в одном посеве. Роща из одинаковых конусов
            // читается как узор, а не как лес: глаз ловит повтор мгновенно.
            // Виды перечисляются через запятую и раздаются копиям по кругу со
            // сдвигом от зерна — тогда смесь равномерная, а не пятнами.
            var kinds = (b.Kinds != null && b.Kinds.Length > 0)
                ? b.Kinds
                : new[] { string.IsNullOrEmpty(b.Model) ? b.Shape : b.Model };
            var meshes = new Mesh[kinds.Length];
            var samples = new GameObject[kinds.Length];
            bool anyKind = false;
            for (int i = 0; i < kinds.Length; i++)
            {
                var k = (kinds[i] ?? "").Trim();
                samples[i] = FindInSet(k);                       // сперва модель набора
                if (samples[i] == null) meshes[i] = Lvn3DShapes.Get(k); // иначе примитив
                anyKind |= samples[i] != null || meshes[i] != null;
            }
            if (!anyKind) return false;

            var root = new GameObject("lvn-grove-" + id);
            root.transform.SetParent(_set.transform, false);
            if (b.Pos is Vector3 rp) root.transform.localPosition = rp;
            _bodies[id] = root.transform;

            // Материал на ОКРАС, а не на копию: два-три оттенка листвы стоят
            // два-три вызова отрисовки, а сто разных цветов стоили бы сотню.
            // Цвет: если автор его не задал, НАСЛЕДУЕМ от модели — у кита это
            // общая палитра, и подстановка белого превращала надгробия в белые
            // кубы. Белый по умолчанию честен только для примитива, которому
            // взять цвет неоткуда.
            Color inherited = Color.white;
            foreach (var sm in samples)
            {
                if (sm == null) continue;
                var r0 = sm.GetComponentInChildren<MeshRenderer>(true);
                if (r0 != null && r0.sharedMaterial != null) { inherited = r0.sharedMaterial.color; break; }
            }
            var tints = (b.Tints != null && b.Tints.Length > 0)
                ? b.Tints
                : new[] { b.Tint ?? inherited };
            // Своя текстура на моделях набора — трипланаром: их развёртка
            // сделана под чужой атлас и нашу текстуру растягивает в полосы
            // (та же причина, что у одиночного тела в SetBody).
            bool anyModel = false;
            foreach (var sm in samples) anyModel |= sm != null;
            var shaderKind = (b.Texture != null && anyModel && string.IsNullOrEmpty(b.Shader))
                ? "triplanar" : b.Shader;

            var mats = new Material[tints.Length];
            for (int i = 0; i < tints.Length; i++)
            {
                mats[i] = LitMaterial(tints[i], shaderKind);
                if (mats[i] == null) continue;
                mats[i].enableInstancing = true;
                if (b.Texture != null)
                {
                    mats[i].mainTexture = b.Texture;
                    ApplyCutout(mats[i], b.Texture);
                }
                if (b.Normal != null && mats[i].HasProperty("_BumpMap")) mats[i].SetTexture("_BumpMap", b.Normal);
                if (b.Bump is float bs && mats[i].HasProperty("_BumpScale")) mats[i].SetFloat("_BumpScale", bs);
                if (b.Tiling is float tl && mats[i].HasProperty("_Tiling")) mats[i].SetFloat("_Tiling", tl);
                ApplyRoad(mats[i], b);
                if (b.Rim is float rimv && mats[i].HasProperty("_RimStrength")) mats[i].SetFloat("_RimStrength", rimv);
                // Рост растения — чтобы ветер гнул его относительно СЕБЯ, а не
                // относительно метра: иначе низкая трава почти не шевелится.
                if (mats[i].HasProperty("_PlantHeight"))
                    mats[i].SetFloat("_PlantHeight", Mathf.Max(0.05f, b.Size?.y ?? 1f));
            }

            var area = b.Area == Vector2.zero ? new Vector2(10f, 10f) : b.Area;
            var rnd = new System.Random(b.Seed);
            float Rnd() => (float)rnd.NextDouble();
            var taken = new List<Vector2>();
            // Модель набора уже несёт правильную UV-развёртку и текстуру.
            // Если автор просит только shader=wind, нельзя заменять её одним
            // белым материалом на весь посев. Перекладываем КАЖДЫЙ исходный
            // материал через Windify и кешируем перевод: сотни копий снова
            // делят несколько материалов и остаются GPU-instanced.
            var windMaterials = new Dictionary<Material, Material>();
            bool windModels = string.Equals(b.Shader, "wind", System.StringComparison.OrdinalIgnoreCase);
            bool byMap = b.Spots != null && b.Spots.Length > 0;
            // Карта задаёт места точно; посев — числом на площадь. Дальше код
            // общий: и там, и там это много копий одного вида.
            // ПОТОЛОК ПОСЕВА — на всю сцену, а не на одну команду.
            //
            // Две тысячи копий в одной строке движок переживал, но десять таких
            // строк — уже нет, а автор пишет их по одной и обрыва не замечает:
            // каждая по отдельности выглядит скромно. Считаем общий счёт сцены
            // и, дойдя до предела, честно говорим об этом в лог, а не роняем
            // кадр молча.
            int room = Mathf.Max(0, MaxScatterTotal - _scatterTotal);
            int asked = byMap ? b.Spots.Length : b.Count;
            int count = Mathf.Min(asked, Mathf.Min(2000, room));
            if (count < asked)
                LvnPlayer.Log?.Invoke($"[lvn-o3d] '{id}': поставлено {count} из {asked} — " +
                                      $"в сцене уже {_scatterTotal} копий при пределе {MaxScatterTotal}");
            _scatterTotal += count;

            // Собственный поворот образца: у примитива он единичный, у модели
            // из набора — тот, что задал её автор.
            var srcRot = Quaternion.identity;

            for (int i = 0; i < count; i++)
            {
                Vector2 spot = byMap ? b.Spots[i] : Vector2.zero;
                if (byMap) goto placed;
                // Просвет между копиями: без него деревья слипаются в кашу.
                // Пробуем несколько раз и сдаёмся — лучше чуть реже, чем висеть.
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    spot = new Vector2((Rnd() - 0.5f) * area.x, (Rnd() - 0.5f) * area.y);
                    if (b.Gap <= 0f) break;
                    bool clear = true;
                    for (int k = 0; k < taken.Count; k++)
                        if ((taken[k] - spot).sqrMagnitude < b.Gap * b.Gap) { clear = false; break; }
                    if (clear) break;
                }
                if (b.Gap > 0f) taken.Add(spot);
                placed:

                int kind = (i + b.Seed) % kinds.Length;
                GameObject go;
                if (samples[kind] != null)
                {
                    go = Object.Instantiate(samples[kind], root.transform);
                    RestoreMeshes(go, samples[kind].transform);
                    srcRot = samples[kind].transform.localRotation;
                    go.SetActive(true);
                    // Клон модели несёт СВОЙ материал (у кита это общая
                    // палитра). Но если автор задал цвет или текстуру, он ждёт,
                    // что они применятся — молча игнорировать их значит
                    // оставить его гадать, почему надгробия остались прежними.
                    // Список окрасов — такое же основание подменить материал, как
                    // и одиночный цвет. Проверялся только `color=`, поэтому
                    // `colors="#6e6a63,#5f5c56"` на моделях набора молча не
                    // работал: камни оставались того цвета, в какой их покрасил
                    // автор пакета.
                    if (b.Tint != null || b.Texture != null || b.Normal != null
                        || (b.Tints != null && b.Tints.Length > 0))
                        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                            r.sharedMaterial = mats[kind % mats.Length];
                    else if (windModels)
                    {
                        foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            var sourceMats = r.sharedMaterials;
                            for (int mi = 0; mi < sourceMats.Length; mi++)
                            {
                                var sourceMat = sourceMats[mi];
                                if (sourceMat == null) continue;
                                if (!windMaterials.TryGetValue(sourceMat, out var windMat))
                                {
                                    windMat = Lvn3DStyle.Windify(sourceMat, b.Wind);
                                    windMaterials[sourceMat] = windMat;
                                }
                                sourceMats[mi] = windMat;
                            }
                            r.sharedMaterials = sourceMats;
                        }
                    }
                }
                else
                {
                    go = new GameObject("copy", typeof(MeshFilter), typeof(MeshRenderer));
                    go.transform.SetParent(root.transform, false);
                    go.GetComponent<MeshFilter>().sharedMesh = meshes[kind];
                    var r = go.GetComponent<MeshRenderer>();
                    r.sharedMaterial = mats[kind % mats.Length];
                    if (b.Shadow == false) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                go.name = "copy" + i;

                var local = new Vector3(spot.x, 0f, spot.y);
                var world = (b.Pos ?? Vector3.zero) + local;
                if (b.Ground != false) local.y = GroundHeightAt(world) - (b.Pos?.y ?? 0f);
                go.transform.localPosition = local;

                float k2 = 1f + (Rnd() - 0.5f) * 2f * b.ScaleVar;
                var want = b.Size ?? Vector3.one;
                // Модель меряем по габаритам, примитив — как есть: «два метра»
                // должно значить два метра для любого тела.
                go.transform.localScale = (samples[kind] != null ? ScaleForHeight(go, want) : want) * k2;
                // Разворот — ПОВЕРХ собственной ориентации модели, а не вместо неё.
                //
                // Модели из FBX почти всегда приходят повёрнутыми: в редакторах
                // вверх смотрит ось Z, в Unity — Y, и разница в девяносто
                // градусов живёт в самом префабе. Записывая свой поворот
                // напрямую, мы стирали её — и дерево ЛОЖИЛОСЬ на бок. В кадре
                // это выглядело как «посев не работает», хотя копии стояли на
                // местах, просто плашмя.
                go.transform.localRotation =
                    Quaternion.Euler(0f, (Rnd() - 0.5f) * 2f * b.YawVar, 0f) * srcRot;
            }

            if (b.Fade is float fade && fade > 0f)
                Lvn3DFade.Attach(root.transform, this, fade);
            if (b.Wind > 0f)
                foreach (var m in mats)
                {
                    if (m == null || !m.HasProperty("_Wind")) continue;
                    m.SetFloat("_Wind", b.Wind);
                }
            Debug.Log($"[lvn-o3d] {(byMap ? "карта" : "посев")} '{id}': {count} копий" +
                      (byMap ? ", места из карты" : $" на {area.x:0}×{area.y:0} м") + ", " +
                      $"видов {kinds.Length}, окрасов {tints.Length}, зерно {b.Seed}" +
                      (b.Wind > 0f ? $", качание {b.Wind:0.0}°" : ""));
            if (IsSmoke(b.Shader))
                foreach (var m in mats)
                    if (m != null && m.HasProperty("_Smoke")) m.SetFloat("_Smoke", 1f);
            if (ShaderAnimates(b.Shader) || b.Wind > 0f) SetLive(true);
            Shoot();
            return true;
        }

        /// <summary>Пометить тело кликабельным: нажатие уведёт на метку.</summary>
        public void SetBodyClick(string id, string label)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (string.IsNullOrEmpty(label)) _clicks.Remove(id);
            else _clicks[id] = label;
        }

        /// <summary>Что нажали в кадре набора. Точка — доля кадра (0…1).
        ///
        /// <para>ЛУЧОМ ПО ГАБАРИТАМ, а не физикой. Коллайдеры из сборки
        /// вырезаются стриппингом (в логе устройства это «Could not produce
        /// class with ID 64»), и Physics.Raycast там молча не находит ничего —
        /// в редакторе клик работает, на телефоне нет. Тел в сцене немного, и
        /// перебрать их габариты дешевле, чем тащить физику ради нажатий.</para>
        ///
        /// <para>Возвращает метку перехода или null.</para></summary>
        public string PickAt(Vector2 viewport)
        {
            if (_cam == null || _clicks.Count == 0) return null;
            var ray = _cam.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
            string best = null;
            float bestDist = float.MaxValue;
            foreach (var kv in _clicks)
            {
                if (!_bodies.TryGetValue(kv.Key, out var t) || t == null) continue;
                var rends = t.GetComponentsInChildren<Renderer>(true);
                if (rends.Length == 0) continue;
                var box = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) box.Encapsulate(rends[i].bounds);
                if (!box.IntersectRay(ray, out float dist)) continue;
                if (dist < bestDist) { bestDist = dist; best = kv.Value; }
            }
            return best;
        }

        public void RemoveBody(string id)
        {
            if (id == null || !_bodies.TryGetValue(id, out var t)) return;
            _bodies.Remove(id);
            _clicks.Remove(id);
            if (t != null) Kill(t.gameObject);
            Shoot();
        }

        /// <summary>Свет и атмосфера. Вид — sun / fill / point / fog / sky.</summary>
        public void SetLight(string kind, string id, Vector2? angle, Vector3? pos,
            Color? color, float? power, float? range, float? near, float? far,
            Color? top, Color? bottom, bool off, float dur = 0f, float flicker = 0f)
        {
            if (_set == null) { Debug.Log($"[lvn-light] {kind}: сцены нет, пропуск"); return; }
            kind = (kind ?? "sun").ToLowerInvariant();

            // СКРИПТ СИЛЬНЕЕ ПАСПОРТА НАБОРА. Набор приезжает по сети и несёт
            // собственную атмосферу — это правильно как УМОЛЧАНИЕ: художник
            // собрал место и знает, каким оно задумано. Но команда автора
            // выполняется раньше, а набор встаёт позже и переписывает её
            // молча: сцена, где сказано «ясная ночь», получала туман из
            // бандла. Запоминаем сказанное скриптом и повторяем после
            // постановки набора.
            RememberScriptLight(kind, off, top, bottom, color, near, far, power, dur);

            if (kind == "fog")
            {
                // Туман — главный инструмент композиции: он превращает дальний
                // план в задник, на котором читается фигура. Живёт в карточке
                // атмосферы набора, чтобы уезжать вместе с ним.
                EnsureEnv().SetFog(!off, color, near, far, dur);
                Shoot();
                return;
            }
            if (kind == "sky")
            {
                EnsureEnv().SetSky(!off, top, bottom, color, dur);
                // САМО НЕБО, а не только рассеянный свет. Раньше здесь менялся
                // лишь ambient, и над каждой сценой стоял стандартный skybox
                // Unity — по нему сразу видно, что небом никто не занимался, а
                // занимает оно половину кадра.
                ApplySkybox(top, bottom, color, power, off);
                Shoot();
                return;
            }

            var key = string.IsNullOrEmpty(id) ? kind : kind + ":" + id;
            if (off)
            {
                if (_lights.TryGetValue(key, out var dead))
                {
                    _lights.Remove(key);
                    if (dead != null) Kill(dead.gameObject);
                }
                Shoot();
                return;
            }

            if (!_lights.TryGetValue(key, out var light) || light == null)
            {
                var go = new GameObject("lvn-light-" + key);
                go.transform.SetParent(_set.transform, false);
                light = go.AddComponent<Light>();
                _lights[key] = light;
            }

            switch (kind)
            {
                case "point":
                    light.type = LightType.Point;
                    light.range = range ?? 8f;
                    light.shadows = LightShadows.None; // точечные тени дороги и тут не видны
                    if (pos is Vector3 lp) light.transform.localPosition = lp;
                    break;
                case "spot":
                    // Прожектор: фонарь, луч из окна, свет фар. Даёт то, чего не
                    // даст ни солнце, ни лампа — НАПРАВЛЕННОЕ пятно, то есть
                    // возможность показать пальцем, куда смотреть.
                    light.type = LightType.Spot;
                    light.range = range ?? 14f;
                    light.spotAngle = Mathf.Clamp(near ?? 45f, 5f, 170f);
                    light.shadows = LightShadows.Soft;
                    if (pos is Vector3 sp) light.transform.localPosition = sp;
                    light.transform.localRotation = Quaternion.Euler(
                        angle?.x ?? 45f, angle?.y ?? 0f, 0f);
                    break;
                case "fill":
                    // Заполняющий — не «реализм», а читаемость: без него теневая
                    // сторона проваливается в чёрное пятно.
                    light.type = LightType.Directional;
                    light.shadows = LightShadows.None;
                    light.transform.localRotation = Quaternion.Euler(
                        angle?.x ?? 140f, angle?.y ?? 200f, 0f);
                    break;
                default: // sun
                    light.type = LightType.Directional;
                    light.shadows = LightShadows.Soft;
                    light.shadowBias = 0.02f;
                    light.transform.localRotation = Quaternion.Euler(
                        angle?.x ?? 50f, angle?.y ?? -30f, 0f);
                    break;
            }
            // Смена времени суток идёт НА ГЛАЗАХ, если автор задал `dur`:
            // рассвет за один кадр читается сбоем, а не рассветом.
            if (dur > 0.01f)
            {
                Lvn3DLightFade.Run(this, light, color, power, dur);
            }
            else
            {
                if (color is Color lc) light.color = lc;
                light.intensity = power ?? (kind == "fill" ? 0.35f : 1f);
            }
            // Мерцание: живой огонь не горит ровно, и ровно горящий костёр
            // читается лампочкой. Своё поле, а не `far`: у того уже есть смысл
            // «дальняя граница тумана», и одно имя на две вещи путало и автора,
            // и проверки — «far=0.35» у лампы выглядело ошибкой в метрах.
            if (flicker > 0f)
            {
                var flick = light.gameObject.GetComponent<Lvn3DFlicker>()
                            ?? light.gameObject.AddComponent<Lvn3DFlicker>();
                flick.Bind(this, light.intensity, flicker);
                SetLive(true);
            }
            Debug.Log($"[lvn-light] {key}: {light.type}, сила {light.intensity:0.00}" +
                      (range != null ? $", радиус {light.range:0.0}" : "") +
                      (far is float f2 && f2 > 0f ? $", мерцание {f2:0.00}" : ""));
            Shoot();
        }

        // --- внутреннее ------------------------------------------------------

        private Material _skyMat;

        /// <summary>Наше небо: градиент, светило там же, откуда светит солнце
        /// сцены, звёзды по темноте и дымка у горизонта.</summary>
        private void ApplySkybox(Color? top, Color? bottom, Color? mid, float? stars, bool off)
        {
            if (off)
            {
                RenderSettings.skybox = null;
                return;
            }
            var shader = Resources.Load<Shader>("LvnSky");
            if (shader == null) return;
            if (_skyMat == null || _skyMat.shader != shader)
                _skyMat = new Material(shader) { name = "lvn-sky" };
            if (top is Color t) _skyMat.SetColor("_Top", t);
            if (bottom is Color b) _skyMat.SetColor("_Horizon", b);
            if (mid is Color m) _skyMat.SetColor("_Ground", m);
            else if (bottom is Color b2) _skyMat.SetColor("_Ground", b2 * 0.45f);
            if (stars is float st) _skyMat.SetFloat("_Stars", Mathf.Clamp01(st));
            RenderSettings.skybox = _skyMat;
            DynamicGI.UpdateEnvironment();
        }

        private GameObject FindInSet(string name)
        {
            if (_set == null || string.IsNullOrEmpty(name)) return null;
            foreach (var tr in _set.GetComponentsInChildren<Transform>(true))
                if (tr != null && tr.name == name) return tr.gameObject;
            return null;
        }

        private Lvn3DSetEnv EnsureEnv()
        {
            if (_env != null) return _env;
            _env = _set.GetComponent<Lvn3DSetEnv>() ?? _set.AddComponent<Lvn3DSetEnv>();
            return _env;
        }

        /// <summary>Освещаемый материал того пайплайна, что реально есть в
        /// проекте. Без него сцена выходит magenta — и автор решает, что сломан
        /// движок, а не что шейдер не собрался.</summary>
        private static Material LitMaterial(Color c, string kind = null)
        {
            var shader = FindShader(kind);
            if (shader == null) return null;
            var m = new Material(shader) { color = c, enableInstancing = true };
            return m;
        }

        /// <summary>Параметры дороги применяются по наличию свойства, поэтому
        /// обычные материалы их просто игнорируют, а язык не знает о типах
        /// конкретных шейдеров.</summary>
        private static void ApplyRoad(Material mat, in Body b)
        {
            if (mat == null) return;
            if (b.RoadEdge is float edge && mat.HasProperty("_Edge"))
                mat.SetFloat("_Edge", Mathf.Clamp01(edge));
            if (b.RoadRuts is float ruts && mat.HasProperty("_Ruts"))
                mat.SetFloat("_Ruts", Mathf.Clamp01(ruts));
            if (b.RoadWet is float wet && mat.HasProperty("_Wet"))
                mat.SetFloat("_Wet", Mathf.Clamp01(wet));
        }

        /// <summary>Шейдер по имени из скрипта. НАШИ шейдеры грузятся через
        /// Resources, а не через Shader.Find: Find находит только то, что уже
        /// попало в сборку, и в собранной игре молча возвращает null — объект
        /// выходит magenta или невидимым. Ресурсы пакета едут в билд всегда.</summary>
        private static Shader FindShader(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "wind":     return Ours("LvnWind");
                case "fire":     return Ours("LvnFire");
                case "smoke":    return Ours("LvnSmoke");
                case "triplanar":
                case "any":      return Ours("LvnTriplanar");
                case "road":     return Ours("LvnRoad");
                case "metal":    return Ours("LvnMetal");
                case "cloth":    return Ours("LvnCloth");
                case "toon":     return Ours("LvnToon");
                case "aura":     return Ours("LvnAura");
                case "water":    return Ours("LvnWater");
                case "glass":
                case "crystal":
                case "ice":      return Ours("LvnGlass");
                case "dissolve": return Ours("LvnDissolve");
                case "unlit":    return Shader.Find("Unlit/Texture") ?? Fallback();
                // Пустое поле — НЕ «как получится», а наш стиль: любой объект
                // сцены по умолчанию выглядит одинаково. В этом весь смысл
                // библиотеки шейдеров как стиля игры (см. Lvn3DStyle).
                default:         return Ours("LvnToon");
            }
        }

        private static Shader Ours(string name) => Resources.Load<Shader>(name) ?? Fallback();

        /// <summary>Имена шейдеров каталога — для подсказок и проверки.</summary>
        public static readonly string[] ShaderKinds =
            { "toon", "wind", "aura", "water", "glass", "dissolve", "fire", "smoke",
              "triplanar", "road", "metal", "cloth", "unlit" };

        private static Shader Fallback()
            => Shader.Find("Universal Render Pipeline/Lit")
            ?? Shader.Find("Standard")
            ?? Shader.Find("HDRP/Lit")
            ?? Shader.Find("Sprites/Default");

        /// <summary>Анимированные шейдеры (ветер, вода) двигают вершины по
        /// времени. Такую сцену нельзя снять один раз: кадр обязан обновляться,
        /// иначе автор включит ветер и увидит застывшее дерево.</summary>

        /// <summary>Масштаб модели по ЗАДАННОМУ РОСТУ В МЕТРАХ.
        ///
        /// <para>Для примитивов `size` — это метры: наши формы единичные, и
        /// масштаб совпадает с размером. Для чужой модели он совпадать не
        /// обязан: у одного кита надгробие занимает пол-юнита, у другого —
        /// шесть. Пока `size` понимался как множитель, `size="3,3,3"` на склепе
        /// давало двадцатиметровую стену, и автор не мог этого предвидеть — он
        /// не знает, в чём измерял модель художник.</para>
        ///
        /// <para>Поэтому меряем габариты и подгоняем: «три метра» значит три
        /// метра, чем бы ни была модель.</para></summary>
        private static Vector3 ScaleForHeight(GameObject go, Vector3 wanted)
        {
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return wanted;
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            var size = b.size;
            if (size.y < 0.0001f) return wanted;
            // Ведём по высоте: она у декораций осмысленнее ширины, и по ней
            // автор представляет масштаб («надгробие мне по пояс»).
            float k = wanted.y / size.y;
            return new Vector3(k * (wanted.x / Mathf.Max(wanted.y, 0.0001f)) * 1f, k, k)
                   * 1f;
        }
        private static bool IsSmoke(string kind)
            => (kind ?? "").ToLowerInvariant() == "smoke";

        private static bool ShaderAnimates(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "wind": case "water": case "aura":
                case "fire": case "smoke": return true;
                default: return false;
            }
        }

        private static void SetAlpha(Material m, float a)
        {
            var c = m.color; c.a = Mathf.Clamp01(a); m.color = c;
            if (a >= 0.999f) return;
            // Перевод в прозрачный режим: у Standard и URP/Lit это РАЗНЫЕ
            // ключевые слова, и половинчатая настройка даёт объект, который
            // рисуется поверх всего либо не рисуется вовсе.
            m.SetFloat("_Surface", 1f);            // URP: Transparent
            m.SetFloat("_Mode", 3f);               // Standard: Transparent
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private static void SetGlow(Material m, float glow, Color tint)
        {
            // СВЕЧЕНИЕ У РАЗНЫХ ПОВЕРХНОСТЕЙ ЗОВЁТСЯ ПО-РАЗНОМУ. У обычной это
            // `_EmissionColor`, а у ауры, огня и прочего света — собственная
            // `_Power`: там свечение не добавка к поверхности, а сама она.
            //
            // Раньше писали только в `_EmissionColor`, и `glow=` на ауре не
            // делал РОВНО НИЧЕГО — молча, потому что материал такого свойства
            // просто не имеет, а Unity на это не жалуется. Шкала эмиссии
            // 1/2/4/8 показывала четыре одинаковых шара, и это было первым
            // внешним признаком.
            if (m == null) return;
            if (m.HasProperty("_Power"))
            {
                m.SetFloat("_Power", Mathf.Max(0f, glow));
                return;
            }
            if (glow <= 0f)
            {
                m.DisableKeyword("_EMISSION");
                return;
            }
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", tint * glow);
        }
    }
}
