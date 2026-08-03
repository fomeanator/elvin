using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// A live 3D set standing in for the flat background: the scene's art is a
    /// prefab (a room, a street, a car interior), a private camera films it, and
    /// the frame becomes the backdrop texture the stage already knows how to
    /// show. So one built set replaces a folder of painted angles — the script
    /// moves the camera instead of asking an artist for another picture.
    ///
    /// <para>Why a render texture rather than pointing the main camera at the
    /// set: everything above the background — characters, dialogue, the whole
    /// effect stack — is 2D drawn on the stage canvas, and the canvas renders
    /// THROUGH the main camera (see <see cref="WorldStage"/>). Filming the set
    /// separately keeps that arrangement untouched, so 3D backdrops cost the
    /// rest of the pipeline nothing and `fx`/`blur` still grade the whole
    /// frame.</para>
    ///
    /// <para>The set is instantiated far from the origin (see <see cref="Far"/>)
    /// so the main camera can never catch it in frame — no layer bookkeeping,
    /// no project setup the game has to remember.</para>
    /// </summary>
    public sealed partial class Lvn3DBackdrop : MonoBehaviour
    {
        /// <summary>Where sets are built: far enough that nothing else sees them.</summary>
        // Далеко — но не дальше, чем нужно. Десять километров от начала
        // координат съедают точность float у самих вершин: на таком удалении
        // шаг представимых чисел подбирается к миллиметру, и грань, стоящая на
        // земле, дрожит уже от этого. Триста метров одинаково недосягаемы для
        // главной камеры (она смотрит в плоскость канвы) и в тридцать раз
        // ближе к нулю.
        private static readonly Vector3 Far = new Vector3(0f, -300f, 0f);

        /// <summary>Во сколько раз крупнее экрана снимается НЕПОДВИЖНЫЙ кадр.
        ///
        /// <para>Ровно 2, и это принципиально: при показе кадр ужимается
        /// билинейным фильтром, а тот усредняет ЧЕТЫРЕ соседних тексела. Только
        /// при целом двукратном масштабе эта четвёрка ложится точно на один
        /// экранный пиксель — получается честное усреднение. На дробном (1.5)
        /// фильтр берёт соседей вразнобой, и часть лесенки доживает до экрана:
        /// именно это и было видно на устройстве.</para>
        ///
        /// Для неподвижного кадра цена — один рендер, не каждый кадр.</summary>
        private const float Supersample = 2f;

        /// <summary>Сглаживание, пока камера едет: тайловые мобильные GPU берут
        /// за 4x порядка 10–15% кадра — за время проезда это приемлемо.</summary>
        private const int MovingSamples = 4;

        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _set;
        private Lvn3DSetEnv _env;      // the set's own sky/fog/ambient, if it brought any
        private readonly List<Material> _snapshotMaterials = new List<Material>();

        // Current framing, and where a tween is taking it.
        private Vector3 _pos, _posTo;
        private Vector3 _rot, _rotTo;   // euler: pitch (x), yaw (y)
        private float _fov = 60f, _fovTo = 60f;
        private float _speed;           // 1/seconds; 0 = snap
        private bool _live;             // the set animates: film every frame
        private Vector2 _echoRot;       // camera-rig shake/pan, as degrees
        // Дыхание камеры: съёмка «с рук». Кадр, стоящий абсолютно неподвижно,
        // выдаёт картинку как фотографию, а не как место, где что-то
        // происходит — особенно рядом с живой анимацией спрайтов. Амплитуда
        // мизерная (доли градуса), но именно она отличает мёртвый кадр от
        // живого, а привязанные к сцене спрайты едут вместе с ней и дают
        // настоящий параллакс.
        private float _swayAmp;         // degrees; 0 = off
        private float _swaySpeed = 1f;  // full cycles per second, roughly
        private float _swayClock;       // own clock: pauses when the set is struck
        private Vector2 _swayRot;
        // Проявление набора. Построенная сцена встаёт МГНОВЕННО — и на глаз это
        // читается как сбой: только что был рисованный фон, и вдруг, без всякой
        // причины, другой мир. Тестеры так и сказали: «резко меняется, непонятно
        // что происходит». Полсекунды выхода из черноты превращают подмену в
        // смену плана.
        private float _reveal = 1f, _revealSpeed;
        // Однократная диагностика постановки фигуры: где набор, где камера, где
        // грунт. Печатается один раз на набор — этого хватает, чтобы поймать
        // рассинхрон пространств, и не хватает, чтобы засорить лог.
        private bool _diagBoard = true;
        // Осмотр: игрок водит по сцене свайпом. Это НЕ кадрирование — авторский
        // ракурс остаётся заданным, поворот живёт поверх него и снимается любой
        // командой `bg3d`. Потолка по умолчанию НЕТ: набор всё равно окружён
        // лесом и небом со всех сторон, а упираться в невидимую стену посреди
        // осмотра неприятнее, чем увидеть край декорации. Ограничить можно
        // явно — через SetLookLimit.
        private Vector2 _lookRot;
        private float _lookLimit = 0f;   // 0 = без предела
        // Глубина резкости. Фокус задаётся В МЕТРАХ — тем же языком, каким
        // ставятся тела и камера: «резко в шести метрах». Ноль силы — выключено,
        // и тогда лишнего прохода по кадру нет вовсе.
        private float _dofFocus = 6f, _dofRange = 4f, _dofPower;
        private float _echoZoom = 1f;   // camera-rig zoom, as an fov divisor
        private bool _echoLive;         // the rig moved this frame

        /// <summary>The filmed frame — hand it to the stage background.</summary>
        public RenderTexture Texture => _rt;

        /// <summary>Насколько движок уже уступил резкостью ради плавности.</summary>
        public float BudgetScale => _budgetScale;
        /// <summary>Снимается ли кадр каждый кадр.</summary>
        public bool IsLive => _live;
        /// <summary>Камера, которая снимает набор — «глаза» игрока в сцене.</summary>
        public Camera SetCamera => _cam;

        /// <summary>Raised when rotation/resizing replaces the frame buffer, so
        /// the RawImage never keeps displaying a released RenderTexture.</summary>
        public event System.Action<RenderTexture> TextureChanged;

        /// <summary>True while a set is loaded and filming.</summary>
        public bool Active => _set != null;

        /// <summary>Stand up a backdrop renderer that lives as long as
        /// <paramref name="owner"/> does.
        ///
        /// <para>The backdrop is a ROOT object on purpose. Parenting it under the
        /// stage canvas looks tidier and is quietly fatal: a canvas scales its
        /// children to the screen, so the set and its camera would be resized by
        /// whatever device the game runs on — the same shot framed one way in the
        /// editor and another on a phone, usually as an empty sky. Instead the
        /// owner carries a keeper that tears the backdrop down with it, which is
        /// what parenting was for.</para></summary>
        public static Lvn3DBackdrop Ensure(Transform owner)
        {
            var go = new GameObject("lvn-3d-backdrop");
            var backdrop = go.AddComponent<Lvn3DBackdrop>();
            if (owner != null) owner.gameObject.AddComponent<Keeper>().backdrop = backdrop;
            return backdrop;
        }

        /// <summary>Rides on the stage and takes the backdrop down with it.</summary>
        private sealed class Keeper : MonoBehaviour
        {
            public Lvn3DBackdrop backdrop;
            private void OnDestroy()
            {
                if (backdrop != null) Kill(backdrop.gameObject);
            }
        }

        /// <summary>Build <paramref name="prefab"/> as the current set, replacing
        /// any previous one. Passing null tears the set down (the stage then goes
        /// back to flat backgrounds).</summary>
        public void SetSet(GameObject prefab)
        {
            if (_set != null) Debug.Log($"[lvn-3d] набор заменён (SetSet {(prefab != null ? prefab.name : "null")})");

            // УХОД ИЗ 3D — ЭТО И ОСВОБОЖДЕНИЕ ПАМЯТИ.
            //
            // Новелла живёт часами: игрок проходит главу за главой, трёхмерные
            // сцены сменяются рисованными фонами и обратно. Кэши текстур
            // поверхности и разобранных моделей до сих пор не чистились НИКОГДА
            // — они копились всю сессию, и каждая новая сцена добавляла к ним
            // свои мегабайты. На телефоне это кончается вылетом, причём далеко
            // от места, где память заняли, и потому необъяснимым.
            //
            // Чистим именно здесь — при `bg3d off`, когда 3D больше не нужно.
            // При СМЕНЕ одного набора на другой не трогаем: сцена может
            // вернуться, а перекачка текстур по сети дороже удержанной памяти.
            if (prefab == null)
            {
                LvnTextures.Clear();
                LvnObjMesh.Clear();
                _bodySpecs.Clear();
                _modelRot.Clear();
                _setMeshes.Clear();
                _scatterTotal = 0;
                Lvn3DStyle.Forget();
                Debug.Log("[lvn-3d] 3D выключено — кэши текстур и моделей освобождены");
            }
            _swayAmp = 0f; _swayRot = Vector2.zero; _swayClock = 0f; _lookRot = Vector2.zero;
            _diagBoard = true;
            foreach (var kv in _boards) if (kv.Value != null) Kill(kv.Value.gameObject);
            _boards.Clear();
            foreach (var kv in _shadows) if (kv.Value != null) Kill(kv.Value.gameObject);
            _shadows.Clear();
            // ТЕЛА, ПОСТАВЛЕННЫЕ СКРИПТОМ, ПЕРЕЕЗЖАЮТ, А НЕ ГИБНУТ.
            //
            // Набор приходит ПО СЕТИ, а команды скрипта не ждут: пока бандл
            // качается, `o3d` уже расставили сорок надгробий в пустой сцене.
            // Первая версия убивала их при постановке набора — кладбище
            // оказывалось пустым, и причину было не видно: логи показывали, что
            // посев отработал. Поэтому содержимое от автора переносим в новый
            // набор; уходит вместе с местом только то, что принадлежало старому.
            var carry = new List<Transform>();
            foreach (var kv in _bodies) if (kv.Value != null) carry.Add(kv.Value);
            var carryLights = new List<Light>();
            foreach (var kv in _lights) if (kv.Value != null) carryLights.Add(kv.Value);
            foreach (var t in carry) t.SetParent(null, true);
            foreach (var l in carryLights) l.transform.SetParent(null, true);
            _groundVerts = null; _groundFlatY = null;   // новый набор — новый пол
            _hasTerrain = false;                        // и свой рельеф
            if (_set != null)
            {
                if (_env != null) { _env.Restore(); _env = null; }
                Kill(_set);
                _set = null;
            }
            ReleaseSnapshotMaterials();
            if (prefab == null)
            {
                Release();
                return;
            }
            EnsureCamera();
            // Parent the set to this component (itself a root object): a set left
            // loose in the scene outlives the stage that stood it, and the next
            // novel opens with the previous one's room still being filmed.
            _set = Instantiate(prefab, transform);
            _set.transform.localPosition = Far;
            _set.transform.localRotation = Quaternion.identity;
            _set.name = "lvn-3d-set:" + prefab.name;

            // Does anything in this set MOVE? A still room is filmed once and
            // costs nothing after that; a set with swaying trees, running water
            // or particles has to be filmed every frame like any 3D game. Decide
            // by what the set actually contains, so an author never has to
            // declare it — and never gets a frozen waterfall by forgetting to.
            // A set brings its own air (sky, fog, ambient bounce). Without it a
            // stylised kit renders flat and grey — the geometry and shaders are
            // fine, the atmosphere simply is not project-wide state we may keep.
            _env = _set.GetComponent<Lvn3DSetEnv>();
            if (_env != null) _env.Apply();

            // ЕДИНЫЙ ВИД. Набор приходит с чужими материалами — реалистичными,
            // мультяшными, какими угодно. Показывать их «как есть» значит
            // собирать кадр-коллаж. Перекладываем на наш шейдер, сохраняя
            // текстуру и цвет: сцена становится одного стиля бесплатно для
            // автора (см. Lvn3DStyle).
            // Возвращаем перенесённое содержимое в новый набор: место сменилось,
            // но надгробия, которые поставил автор, стоят там же, где стояли.
            // СТИЛЬ — ДО возврата тел, и это принципиально. Конвертер
            // перекладывает материалы на наш toon, и всё, что окажется внутри
            // набора в этот момент, он переложит тоже. Тела автора уже одеты по
            // его словам — своим шейдером, своей текстурой, своим трипланаром;
            // пропустить их через конвертер значит стереть сказанное. Пока они
            // снаружи, стиль достаётся ровно тому, для кого затевался: чужой
            // геометрии пришедшего набора.
            Lvn3DStyle.Forget();
            int styled = Lvn3DStyle.Apply(_set);
            if (styled > 0) Debug.Log($"[lvn-style] приведено к общему виду: {styled} объект(ов)");

            // Возвращаем перенесённое содержимое: место сменилось, но
            // надгробия, которые поставил автор, стоят там же, где стояли.
            foreach (var t in carry) if (t != null) t.SetParent(_set.transform, true);
            foreach (var l in carryLights) if (l != null) l.transform.SetParent(_set.transform, true);

            // SetSet выше обязан забыть пол СТАРОГО набора, но вместе с телами
            // сюда переехала процедурная земля скрипта. Вернём её формулу ДО
            // пересборки моделей: ground=1 должен дать один результат и при
            // мгновенном, и при позднем приходе сетевого бандла.
            RestoreScriptGround();

            // Перенос сохранил МЕСТО тел, но не их геометрию: те, что просили
            // модель набора до его приезда, стоят коробками-заглушками. Теперь
            // модели есть — собираем их заново.
            RebuildBodiesNeedingSet();
            RegroundScriptBodies();
            ReapplyScriptLight();   // паспорт набора — умолчание, слово автора сильнее

            _live = SetAnimates(_set);
            _cam.enabled = _live;

            // A visual-novel backdrop is a composed shot, even while its camera
            // glides to the next mark. Time-driven vendor wind otherwise moves
            // alpha-cutout leaves in BOTH the colour and ShadowCaster passes on
            // every moving frame. On a mobile shadow map that reads as crawling,
            // flickering leaf shadows. Clone only the few wind materials used by
            // this instance and pin them to one pose; source assets and other
            // scenes keep their animation.
            if (_env == null || _env.freezeShaderWind)
                FreezeSnapshotShaderWind(_set);

            // Набор ставится один раз и дальше НЕ двигается — значит его
            // геометрию можно слепить в общие буферы. Без этого каждый камень
            // и каждая ёлка уходят отдельным вызовом отрисовки: у лесного
            // набора это 450 вызовов на кадр, отсюда рывки по секунде.
            // …но СНАЧАЛА запоминаем, из чего он слеплен. Склейка подменяет
            // каждому объекту меш на общий, и копия модели, взятой из набора
            // после этого, тащит за собой весь набор целиком — в кадре вместо
            // надгробия вырастает белая гора. Каталог мешей стоит словаря на
            // сцену и снимает выбор между «быстро» и «можно брать модели».
            RememberSetMeshes();
            // Тела, которые скрипт успел поставить ДО прихода набора, лежат уже
            // внутри него — и склейка забрала бы их вместе с декорацией. Для
            // живого тела это смерть: его двигают, красят и убирают, а в общих
            // буферах оно перестаёт быть отдельным объектом и начинает рисовать
            // весь набор целиком. Вынимаем их на время склейки.
            var parked = ParkBodies();
            try
            {
                StaticBatchingUtility.Combine(_set);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[bg3d] набор не удалось слепить: " + e.Message);
            }
            UnparkBodies(parked);

            // A set authored around its own origin lands around Far; the camera
            // frames it in the set's local space, so authors keep thinking in
            // ordinary coordinates.
            Apply(); // one frame, so the set appears immediately
        }

        // Меши набора ДО склейки: путь в иерархии → исходный меш. Путь, а не
        // ссылка на объект, потому что копию делают с объекта, а восстанавливать
        // приходится и у его потомков.
        private readonly Dictionary<string, Mesh> _setMeshes = new Dictionary<string, Mesh>();

        // Тело, вынутое из набора на время склейки, вместе со своим местом:
        // возвращать надо ровно туда, откуда взяли.
        private struct ParkedBody
        {
            public Transform T;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
        }

        private List<ParkedBody> ParkBodies()
        {
            var list = new List<ParkedBody>();
            if (_set == null) return list;
            foreach (var kv in _bodies)
            {
                var t = kv.Value;
                if (t == null || t.parent != _set.transform) continue;
                list.Add(new ParkedBody
                {
                    T = t, Pos = t.localPosition, Rot = t.localRotation, Scale = t.localScale,
                });
                // Без сохранения мирового положения: место тела задано В
                // КООРДИНАТАХ НАБОРА, а набор стоит за десять километров от
                // нуля — пересчёт через мир только потерял бы точность.
                t.SetParent(null, false);
            }
            return list;
        }

        private void UnparkBodies(List<ParkedBody> parked)
        {
            if (parked == null || _set == null) return;
            foreach (var p in parked)
            {
                if (p.T == null) continue;
                p.T.SetParent(_set.transform, false);
                p.T.localPosition = p.Pos;
                p.T.localRotation = p.Rot;
                p.T.localScale = p.Scale;
            }
        }

        private void RememberSetMeshes()
        {
            _setMeshes.Clear();
            if (_set == null) return;
            foreach (var mf in _set.GetComponentsInChildren<MeshFilter>(true))
                if (mf != null && mf.sharedMesh != null)
                    _setMeshes[PathIn(_set.transform, mf.transform)] = mf.sharedMesh;
        }

        /// <summary>Вернуть копии модели её СОБСТВЕННЫЕ меши вместо общего.
        /// Вызывается сразу после копирования — до того, как копию покажут.</summary>
        private void RestoreMeshes(GameObject clone, Transform origin)
        {
            if (clone == null || origin == null || _setMeshes.Count == 0) return;
            string root = PathIn(_set.transform, origin);
            var filters = clone.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in filters)
            {
                // Путь потомка внутри копии совпадает с путём внутри оригинала —
                // копия иерархию не меняет.
                string rel = PathIn(clone.transform, mf.transform);
                string full = string.IsNullOrEmpty(rel) ? root : root + "/" + rel;
                if (_setMeshes.TryGetValue(full, out var mesh) && mesh != null)
                    mf.sharedMesh = mesh;
            }
            // Копия ЖИВАЯ: её двигают, красят и масштабируют. Признак статики,
            // унаследованный от набора, оставил бы её в общих буферах склейки.
            foreach (var t in clone.GetComponentsInChildren<Transform>(true))
                t.gameObject.isStatic = false;

            // УРОВНИ ДЕТАЛИЗАЦИИ ЧУЖОЙ МОДЕЛИ — ВОН. Пакеты растительности несут
            // LODGroup, настроенную на свой исходный размер. Копию мы двигаем и
            // МАСШТАБИРУЕМ, а границы группы при этом не пересчитываются: она
            // продолжает считать, что объект занимает столько же пикселей, что и
            // в исходной сцене. На маленьком буфере (а он у нас падает вместе с
            // бюджетом кадра) выходит, что объект «слишком мелкий», и группа
            // прячет ВСЕ уровни разом — модель просто исчезает, хотя стоит на
            // месте и в логах числится.
            //
            // Свои уровни детализации мы всё равно считаем иначе (см. fade=), а
            // сцена новеллы не то место, где экономят на трёх соснах.
            foreach (var lod in clone.GetComponentsInChildren<LODGroup>(true))
            {
                // Перед удалением включаем всё, что группа успела погасить.
                foreach (var l in lod.GetLODs())
                    foreach (var r in l.renderers)
                        if (r != null) r.enabled = true;
                Object.DestroyImmediate(lod);
            }
        }

        private static string PathIn(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (var p = t; p != null && p != root; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>True when the set has anything that moves on its own.</summary>
        private static bool SetAnimates(GameObject set)
        {
            // Animators and particles are named, not typed: the engine package
            // deliberately does not reference Unity's Animation and ParticleSystem
            // modules (a game that ships neither should not have to carry them),
            // so asking for the type by name keeps this check dependency-free.
            foreach (var c in set.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                switch (c.GetType().Name)
                {
                    case "Animator":
                    case "Animation":
                    case "ParticleSystem":
                    case "VisualEffect":
                        return true;
                }
                // Scrolling UVs, vertex-animated foliage and the like live in
                // scripts the engine can't name; anything the set author attached
                // beyond a plain renderer counts as motion. The engine's own
                // carry-on components (the set's sky/fog card) are NOT motion —
                // counting them filmed every still set per-frame, for nothing.
                if (c is Lvn3DSetEnv) continue;
                if (c is MonoBehaviour) return true;
            }
            return false;
        }

        /// <summary>Pin time-driven vegetation and its shadow caster to one pose.
        /// One clone is shared by every renderer that used the same source
        /// material, so a forest with thousands of instances creates only a
        /// handful of materials.</summary>
        private void FreezeSnapshotShaderWind(GameObject set)
        {
            ReleaseSnapshotMaterials();
            var replacements = new Dictionary<Material, Material>();
            int bindings = 0;
            foreach (var renderer in set.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null ||
                        (!source.HasProperty("_CUSTOMWIND") &&
                         !source.HasProperty("_CUSTOMWIND1")))
                        continue;

                    if (!replacements.TryGetValue(source, out var frozen))
                    {
                        frozen = Instantiate(source);
                        frozen.name = source.name + " (snapshot)";
                        // Keep the keyword variant the bundle was built with.
                        // Disabling a shader_feature at runtime is unsafe on
                        // Android: Unity may have stripped that unused variant.
                        // Zero speed keeps the authored bend but removes _Time
                        // from subsequent frames. Strength is the fallback for
                        // other compatible vegetation shaders.
                        if (frozen.HasProperty("_WindMovement"))
                            frozen.SetFloat("_WindMovement", 0f);
                        else if (frozen.HasProperty("_WindStrength"))
                            frozen.SetFloat("_WindStrength", 0f);
                        replacements.Add(source, frozen);
                        _snapshotMaterials.Add(frozen);
                    }
                    materials[i] = frozen;
                    changed = true;
                    bindings++;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            if (replacements.Count > 0)
                Debug.Log($"[bg3d-shadow] frozen wind in {replacements.Count} material(s), " +
                          $"{bindings} renderer binding(s)");
        }

        private void ReleaseSnapshotMaterials()
        {
            foreach (var material in _snapshotMaterials) Kill(material);
            _snapshotMaterials.Clear();
        }

        /// <summary>What the 2D camera rig is doing, so the set moves with it:
        /// a shake becomes a real jolt of the shot, a pan a turn of the head, a
        /// zoom a change of focal length. <paramref name="offset"/> is in canvas
        /// units and <paramref name="width"/> is the canvas' logical width, so
        /// the same op reads the same on every screen.</summary>
        public void Echo(Vector2 offset, float zoom, float width)
        {
            if (width <= 0f) return;
            // The world swings the way the 2D layer slid, scaled by the shot's
            // own field of view — so both layers read as one filmed scene.
            // Horizontal motion maps against the HORIZONTAL fov (the camera
            // stores the vertical one), vertical against the logical height.
            float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            float hfov = 2f * Mathf.Atan(Mathf.Tan(_fov * 0.5f * Mathf.Deg2Rad) * sw / sh) * Mathf.Rad2Deg;
            float height = width * sh / sw;
            // Content shifted right (+x) → the camera turns LEFT to carry the
            // world along; shifted up (+y) → the camera dips DOWN. Unity euler:
            // +pitch looks down, +yaw turns right.
            var rot = new Vector2(offset.y / height * _fov, -offset.x / width * hfov);
            bool moved = (rot - _echoRot).sqrMagnitude > 0.000001f
                         || Mathf.Abs(zoom - _echoZoom) > 0.0001f;
            _echoRot = rot;
            _echoZoom = zoom <= 0f ? 1f : zoom;
            if (!moved && !_echoLive) return;
            _echoLive = rot.sqrMagnitude > 0.000001f || Mathf.Abs(_echoZoom - 1f) > 0.0001f;
            Apply();      // re-film with the new offset; a still shot pays only while it moves
        }

        /// <summary>Глубина резкости: расстояние до плоскости фокуса и её
        /// толщина в МЕТРАХ, сила размытия (0 — выключить совсем).
        ///
        /// <para>Метры, а не доли буфера, потому что автор уже мыслит метрами:
        /// он ставит камеру на 1.7 и врага в шести. «Резко в шести метрах» —
        /// это то же самое предложение, а не перевод в чужую систему.</para></summary>
        public void SetDof(float? focus, float? range, float? power)
        {
            if (focus is float f) _dofFocus = Mathf.Max(0.1f, f);
            if (range is float r) _dofRange = Mathf.Max(0.05f, r);
            if (power is float p) _dofPower = Mathf.Max(0f, p);
            EnsureCamera();
            Lvn3DPostStack.Ensure(_cam)?.SetDof(_dofFocus, _dofRange, _dofPower);
            Debug.Log($"[lvn-dof] фокус {_dofFocus:0.0} м, глубина {_dofRange:0.0} м, сила {_dofPower:0.00}");
            Shoot();
        }

        // Тональная компрессия кадра набора. Значения держим здесь, потому что
        // компонент на камере переживает не всякую пересборку буфера, а поля
        // `bg3d` липкие: заданная сцене экспозиция должна оставаться заданной.
        private Lvn3DPostStack.Tone _tone = Lvn3DPostStack.Tone.Off;
        private float _exposureEV, _toneSat = 1f, _toneContrast = 1f, _toneDither = 1f;

        /// <summary>Форма плеча: где начинается сжатие светов и к какой яркости
        /// оно стремится. Трогать нужно редко — но у ночной сцены и у полудня
        /// разная «точка белого», и подобрать её иногда важнее самой кривой.</summary>
        public void SetToneCurve(float? knee, float? white)
        {
            EnsureCamera();
            var stack = Lvn3DPostStack.Ensure(_cam);
            if (stack == null) return;
            if (knee is float k) _toneKnee = k;
            if (white is float w) _toneWhite = w;
            stack.SetCurve(_toneKnee, _toneWhite);
        }

        private float _toneKnee = 0.65f, _toneWhite = 1.6f;
        private float _bloom, _bloomThreshold = 1.2f, _bloomKnee = 0.5f;

        /// <summary>Свечение ярких мест. Порог задан в единицах СВЕТА: выше
        /// единицы светится только то, что действительно ярче белого — пламя,
        /// раскалённый металл, магия, — а не любая светлая рубаха.</summary>
        public void SetBloom(float? power, float? threshold, float? knee)
        {
            if (power is float p) _bloom = Mathf.Max(0f, p);
            if (threshold is float th) _bloomThreshold = Mathf.Max(0.05f, th);
            if (knee is float k) _bloomKnee = Mathf.Max(0f, k);

            EnsureCamera();
            var stack = Lvn3DPostStack.Ensure(_cam);
            stack?.SetBloom(_bloom, _bloomThreshold, _bloomKnee);
            if (_cam != null) _cam.allowHDR = stack != null && stack.NeedsHdr;
            EnsureTarget();
            Shoot();
        }

        /// <summary>Как сжимать света и как править цвет кадра НАБОРА.
        ///
        /// <para>Умолчание — «никак»: движок обновляется у людей, чьи новеллы
        /// уже собраны и выглядят так, как автор их принял. Менять им картинку
        /// молча нельзя, поэтому кривая включается явно — <c>bg3d tone=…</c>.</para></summary>
        public void SetTone(Lvn3DPostStack.Tone? tone, float? exposureEV,
                            float? saturation, float? contrast, float? dither)
        {
            if (tone is Lvn3DPostStack.Tone t) _tone = t;
            if (exposureEV is float ev) _exposureEV = Mathf.Clamp(ev, -6f, 6f);
            if (saturation is float s) _toneSat = Mathf.Clamp(s, 0f, 2f);
            if (contrast is float c) _toneContrast = Mathf.Clamp(c, 0f, 2f);
            if (dither is float d) _toneDither = Mathf.Clamp(d, 0f, 2f);

            EnsureCamera();
            // HDR нужен ровно тогда, когда есть что сжимать: без него всё ярче
            // белого уже срезано до того, как кривая увидит кадр, и она лишь
            // притушит и без того плоское пятно.
            var stack = Lvn3DPostStack.Ensure(_cam);
            stack?.SetTone(_tone, _exposureEV, _toneSat, _toneContrast, _toneDither);
            if (_cam != null) _cam.allowHDR = stack != null && stack.NeedsHdr;
            EnsureTarget();   // диапазон сменился — буфер тоже
            Debug.Log($"[lvn-tone] кривая {_tone}, экспозиция {_exposureEV:0.00} EV, " +
                      $"насыщенность {_toneSat:0.00}, контраст {_toneContrast:0.00}, HDR={_cam?.allowHDR}");
            Shoot();
        }

        /// <summary>Force the filming mode instead of letting the set decide:
        /// `bg3d live=1` films every frame (a set whose motion the engine can't
        /// detect — a shader that scrolls water, say), `live=0` pins it to a
        /// still shot even if it could animate.</summary>
        public void SetLive(bool live)
        {
            _live = live;
            EnsureCamera();
            if (_cam != null) _cam.enabled = live && _cam.targetTexture != null;
            if (live) enabled = true;
        }

        /// <summary>Frame the shot. Coordinates are the SET's own — as authored in
        /// the prefab — so a script says "stand at the door" the same way the
        /// artist built it. Any argument left null keeps its current value;
        /// <paramref name="seconds"/> above zero glides instead of cutting.</summary>
        /// <summary>Спроецировать точку НАБОРА в кадр: доли экрана (0..1, Y сверху)
        /// и дистанцию до камеры. Через это спрайт «стоит» в сцене — камера едет,
        /// а он остаётся у своей колонны, вместо того чтобы плыть по экрану.
        /// null — набора/камеры нет (плоский фон), звать нечего.</summary>
        /// <summary>Кадр камеры сдвинулся — привязанным к сцене спрайтам пора
        /// пересчитать место. Ставит сцена; движение камеры плавное, поэтому
        /// одного вызова на команду мало.</summary>
        public System.Action CameraMoved;

        /// <summary>Насколько набор проявлен (0..1) — фон красит этим свою
        /// яркость, пока сцена выходит из черноты.</summary>
        public System.Action<float> RevealChanged;

        /// <summary>Проявить набор за <paramref name="seconds"/> (0 — сразу).</summary>
        /// <summary>Сцена СТРОИТСЯ: держим кадр закрытым, пока команды ставят
        /// тела и свет.
        ///
        /// <para>Без этого автор видит ровно то, на что жалуются игроки: пустое
        /// чёрное пространство, в которое по одному влетают земля, деревья и
        /// костёр. Команды сцены идут пачкой и часть из них ждёт сети, поэтому
        /// «показать сразу» означает показать полуфабрикат. Держим до первой
        /// паузы — реплики или выбора: к этому моменту место собрано.</para></summary>
        public void HoldReveal()
        {
            _reveal = 0f;
            _revealSpeed = 0f;
            _revealHeld = true;
            RevealChanged?.Invoke(0f);
        }

        /// <summary>Место собрано — показать его. Повторный вызов не мешает:
        /// отпускать нечего, если ничего не держали.</summary>
        public void ReleaseReveal(float seconds)
        {
            if (!_revealHeld) return;
            _revealHeld = false;
            Shoot();          // снимаем ГОТОВУЮ сцену, а не ту, что была в начале
            Reveal(seconds);
        }

        public bool RevealHeld => _revealHeld;
        private bool _revealHeld;
        private float _holdClock;
        // Когда в сцену в последний раз что-то ставили. Пачка команд идёт в
        // одном кадре скрипта; как только она иссякла, место собрано.
        private float _lastBuildAt;

        public void Reveal(float seconds)
        {
            if (seconds <= 0f) { _reveal = 1f; _revealSpeed = 0f; RevealChanged?.Invoke(1f); return; }
            _reveal = 0f;
            _revealSpeed = 1f / seconds;
            RevealChanged?.Invoke(0f);
            enabled = true;
        }

        public bool TryProject(Vector3 world, out Vector2 viewport, out float distance)
        {
            viewport = default; distance = 0f;
            if (_cam == null) return false;
            // Точка приходит в координатах САМОГО НАБОРА — так, как автор его
            // строил, и так же, как задаётся кадр в `bg3d`. А стоит набор далеко
            // от нуля сцены (см. Far), чтобы главная камера не поймала его в
            // объектив. Без этого перевода «точка (0,1,0)» означала место в
            // десяти километрах от набора: проекция уходила за спину камеры,
            // привязка тихо не срабатывала и спрайт оставался приклеенным к
            // экрану. TransformPoint, а не Far + point, — набор может быть ещё
            // и повёрнут или отмасштабирован.
            var wp = _set != null ? _set.transform.TransformPoint(world) : world;
            var vp = _cam.WorldToViewportPoint(wp);
            if (vp.z <= 0.001f) return false;      // за спиной камеры
            viewport = new Vector2(vp.x, 1f - vp.y); // экранные доли сверху вниз
            distance = vp.z;
            return true;
        }

        // ── БИЛЛБОРДЫ: спрайт как ОБЪЕКТ СЦЕНЫ ───────────────────────────
        // Плоская фигура, стоящая в наборе и всегда развёрнутая к камере. Это
        // честнее любой проекции: перспективу, порядок с деревьями, туман и
        // тени считает сам рендер, а не наша арифметика поверх экранных долей.
        // Раньше спрайт жил на канвасе и «догонял» сцену пересчётом — отсюда и
        // великаний рост рядом с соснами, и прыжок на первом кадре.
        private readonly Dictionary<string, Transform> _boards = new Dictionary<string, Transform>();

        /// <summary>Поставить (или подвинуть) спрайт в сцене. Высота — В МЕТРАХ
        /// мира, точка — основание фигуры в координатах набора.</summary>
        /// <returns>true — фигура стоит в сцене; false — поставить не удалось,
        /// и звать её должен обычный канвасный путь.</returns>
        public bool SetBillboard(string id, Texture tex, Vector3 pos, float heightM, bool flip)
        {
            if (string.IsNullOrEmpty(id) || _set == null) return false;
            if (tex == null)
            {
                // НЕТ АРТА — НЕ СНОСИМ. Место объекта ставится дважды: сперва
                // пустым слотом (арт ещё летит по сети), потом с артом. Снос на
                // первом вызове выбивал бойца из сцены при КАЖДОЙ смене позы —
                // а в бою поза меняется каждый такт. Держим прежнюю картинку и
                // обновляем только место; убирает фигуру пусть тот, кто её
                // действительно убирает (RemoveBillboard).
                if (!_boards.TryGetValue(id, out var kept) || kept == null) return false;
                tex = kept.GetComponent<MeshRenderer>()?.sharedMaterial?.mainTexture;
                if (tex == null) return false;
            }
            if (!_boards.TryGetValue(id, out var t) || t == null)
            {
                // Плоскость строим САМИ, а не через CreatePrimitive.
                //
                // CreatePrimitive тянет встроенные ресурсы Unity и требует, чтобы
                // проект ссылался на MeshFilter, MeshRenderer И коллайдер —
                // иначе они вырезаются сборщиком, и в плеере примитив либо не
                // создаётся, либо приезжает без меша. У нас коллайдеров нет
                // намеренно (фону не с чем сталкиваться), и в логе устройства
                // это видно как «Could not produce class with ID 64». В
                // редакторе при этом всё работает — самый дорогой сорт
                // расхождения. Четыре вершины и два треугольника не зависят ни
                // от чего.
                var go = new GameObject("lvn-board-" + id, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(_set.transform, false);
                go.GetComponent<MeshFilter>().sharedMesh = BoardMesh();
                t = go.transform;
                _boards[id] = t;
            }
            // Ориентация — Y-БИЛЛБОРД, как в Doom: фигура крутится ТОЛЬКО
            // вокруг своей вертикали и всегда стоит перпендикулярно земле.
            // Без этого при обходе видно, что это плоскость; с полным
            // слежением (вместе с наклоном) она заваливалась вслед за
            // камерой и меняла размер. Верно ровно одно: вертикаль
            // неподвижна, курс — на зрителя.
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                // Материал СВОЙ: у самодельного объекта его нет, а брать
                // Default-Material значит зависеть от тех же встроенных ресурсов.
                if (mr.sharedMaterial == null)
                {
                    var shader = Shader.Find("Unlit/Transparent")
                                 ?? Shader.Find("Sprites/Default")
                                 ?? Shader.Find("Unlit/Texture");
                    if (shader == null)
                    {
                        Debug.LogWarning("[lvn-board] нет шейдера прозрачности в сборке — " +
                                         "фигура не встанет в сцену");
                        return false;
                    }
                    mr.material = new Material(shader) { name = "lvn-board-" + id };
                }
                var mat = mr.material;
                // Unlit + прозрачность: фигура не должна ловить свет сцены
                // лицом — арт уже отрисован с собственным освещением.
                // Порядок предпочтения, и ни один не обязателен: шейдер,
                // взятый только через Find, может не попасть в сборку. Если не
                // нашёлся ни один — оставляем материал как есть, но говорим об
                // этом вслух: молча невидимая фигура — худший исход.
                var sh = Shader.Find("Unlit/Transparent")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Unlit/Texture");
                if (sh != null) { if (mat.shader != sh) mat.shader = sh; }
                else if (_diagBoard) Debug.LogWarning("[lvn-board] шейдер прозрачности не найден в сборке");
                mat.mainTexture = tex;
                mat.color = Color.white;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            float h = Mathf.Max(0.05f, heightM);
            // НА ЗЕМЛЮ — БЕЗ ФИЗИКИ. Луч сюда просится сам собой, но в
            // собранном плеере его некуда пускать: классы коллайдеров
            // вырезаются стриппингом (в логе устройства это «Could not produce
            // class with ID 64»), и Raycast молча не находит ничего. Поэтому
            // высоту берём из САМОЙ ГЕОМЕТРИИ — по вершинам меша пола, один раз
            // на постановку. Дешевле любого коллайдера и работает везде.
            pos.y = GroundHeightAt(pos);

            t.localPosition = pos + Vector3.up * (h * 0.5f);
            t.localScale = new Vector3(h * aspect * (flip ? -1f : 1f), h, 1f);
            SetContactShadow(id, pos, h);
            // ПЕРЕСНЯТЬ. Кадр набора статичен между движениями камеры — в этом
            // весь смысл 3D-задника. Но фигура, вставшая в сцену, меняет кадр не
            // меньше, чем поворот камеры, а съёмку раньше заказывало ТОЛЬКО
            // движение. Неподвижный набор при этом гасит свой Update, и боец
            // честно стоял в сцене, которую больше никто не снимал: на экране
            // оставался старый снимок — пустая тропа. Ровно это читалось как
            // «а где скелет?».
            Shoot();
            return true;
        }

        // Высота пола под точкой набора: ближайшая вершина меша, у которого
        // самая большая площадь в плане. «Земля» — это не имя объекта, а
        // свойство: самая широкая горизонтальная поверхность сцены. Так работает
        // и с чужими наборами, где пол называется как угодно.
        private float GroundHeightAt(Vector3 local)
        {
            // Наша земля с холмами знает свою высоту ТОЧНО — она задана
            // формулой. Спрашиваем её, а не ближайшую вершину: перебор сетки
            // в двадцать тысяч точек на каждое из сорока надгробий стоил бы
            // заметной паузы, да и ближайшая вершина всё равно врёт на полклетки.
            if (_hasTerrain)
                return _terrainOrigin.y + Lvn3DTerrain.Height(
                    local.x - _terrainOrigin.x, local.z - _terrainOrigin.z, _terrain);

            if (_groundVerts == null) CacheGround();
            // Вершины пола недоступны — грунт ровный по определению: берём его
            // высоту из габаритов. Так работает и с мешем, закрытым на чтение.
            if (_groundVerts == null || _groundVerts.Length == 0)
                return _groundFlatY ?? local.y;
            float best = float.MaxValue, y = local.y;
            for (int i = 0; i < _groundVerts.Length; i++)
            {
                var v = _groundVerts[i];
                float dx = v.x - local.x, dz = v.z - local.z;
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; y = v.y; }
            }
            return y;
        }

        private Vector3[] _groundVerts;
        // Запасная высота грунта: верх габаритов пола в координатах набора.
        // Нужна, когда вершины меша читать нельзя — а в бандле это норма, а не
        // исключение: Unity отдаёт пустой массив вершин у меша, импортированного
        // без Read/Write, и рельеф молча «исчезает». Раньше это давало фигуре
        // высоту 0 независимо от того, где на самом деле пол.
        private float? _groundFlatY;

        private void CacheGround()
        {
            if (_set == null) return;
            MeshFilter widest = null;
            float area = 0f;
            foreach (var mf in _set.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds.size;
                float a = b.x * b.z;                 // площадь В ПЛАНЕ, не объём
                if (b.y > b.x || b.y > b.z) continue; // столб — не пол
                if (a > area) { area = a; widest = mf; }
            }
            if (widest == null) { _groundVerts = new Vector3[0]; return; }

            // Габариты берём у рендерера (мировые) и переводим в координаты
            // набора — они доступны всегда, в отличие от вершин.
            var rend = widest.GetComponent<MeshRenderer>();
            if (rend != null)
            {
                var inv0 = _set.transform.worldToLocalMatrix;
                var top = rend.bounds.center + Vector3.up * rend.bounds.extents.y;
                _groundFlatY = inv0.MultiplyPoint3x4(top).y;
            }

            var verts = widest.sharedMesh.isReadable ? widest.sharedMesh.vertices : null;
            if (verts == null || verts.Length == 0)
            {
                _groundVerts = new Vector3[0];
                Debug.Log($"[lvn-board] пол '{widest.name}': вершины закрыты, " +
                          $"высота по габаритам = {_groundFlatY:0.00}");
                return;
            }
            var m = widest.transform.localToWorldMatrix;
            var inv = _set.transform.worldToLocalMatrix;
            _groundVerts = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                _groundVerts[i] = inv.MultiplyPoint3x4(m.MultiplyPoint3x4(verts[i]));
            Debug.Log($"[lvn-board] пол найден: {widest.name}, {verts.Length} вершин, площадь {area:0}");
        }

        private readonly Dictionary<string, Transform> _shadows = new Dictionary<string, Transform>();
        private static Texture2D _shadowTex;

        /// <summary>Контактная тень — тёмное пятно на земле под фигурой.
        ///
        /// <para>Без неё персонаж выглядит НАКЛЕЕННЫМ на задник, даже когда
        /// стоит в сцене абсолютно верно: глаз определяет касание земли именно
        /// по тени, а не по совпадению координат. Собственную тень плоскость
        /// отбрасывать не может — она дала бы плоский силуэт на снегу, поэтому
        /// пятно рисуем отдельно. Приём старый и честный: так «приклеивали»
        /// спрайты ещё в первых играх с 2.5D-сценой.</para>
        ///
        /// <para>Тень НЕ ребёнок фигуры: масштаб доски растянут по росту и
        /// пропорции арта, и наследование сплющило бы пятно вместе с ним.</para></summary>
        private void SetContactShadow(string id, Vector3 groundPos, float height)
        {
            if (!_shadows.TryGetValue(id, out var s) || s == null)
            {
                var go = new GameObject("lvn-shadow-" + id, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(_set.transform, false);
                go.GetComponent<MeshFilter>().sharedMesh = BoardMesh();
                var mr = go.GetComponent<MeshRenderer>();
                var sh = Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default");
                if (sh == null) { Kill(go); return; }
                mr.material = new Material(sh) { name = "lvn-shadow", mainTexture = ShadowTex() };
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                s = go.transform;
                _shadows[id] = s;
            }
            // Плашмя на земле, чуть выше пола — иначе пятно и грунт дерутся за
            // один и тот же пиксель (z-fighting) и тень мерцает полосами.
            s.localRotation = Quaternion.Euler(90f, 0f, 0f);
            s.localPosition = groundPos + Vector3.up * 0.02f;
            float w = height * 0.55f;
            s.localScale = new Vector3(w, w * 0.62f, 1f); // короче поперёк — перспектива
        }

        // Мягкое круглое пятно: чёрный с альфой, спадающей к краю. Генерируем
        // сами — ассет ради 128×128 градиента тащить в каждый набор незачем.
        private static Texture2D ShadowTex()
        {
            if (_shadowTex != null) return _shadowTex;
            const int n = 128;
            _shadowTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x - n * 0.5f) / (n * 0.5f), dy = (y - n * 0.5f) / (n * 0.5f);
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - r);
                    a = a * a * 0.55f;                       // мягкий край, не круг-нашлёпка
                    px[y * n + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                }
            _shadowTex.SetPixels32(px);
            _shadowTex.Apply();
            return _shadowTex;
        }

        // Единичный квад в плоскости XY, с началом в центре: один меш на все
        // фигуры сцены — их может быть много, а геометрия у всех одна.
        private static Mesh _boardMesh;

        private static Mesh BoardMesh()
        {
            if (_boardMesh != null) return _boardMesh;
            _boardMesh = new Mesh { name = "lvn-board-quad" };
            _boardMesh.SetVertices(new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),   new Vector3(0.5f, -0.5f, 0f),
            });
            _boardMesh.SetUVs(0, new System.Collections.Generic.List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(1f, 1f), new Vector2(1f, 0f),
            });
            // Обход по часовой при взгляде вдоль −Z: лицевая сторона смотрит
            // ТУДА ЖЕ, куда её потом развернёт Y-биллборд.
            _boardMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _boardMesh.RecalculateNormals();
            _boardMesh.RecalculateBounds();
            return _boardMesh;
        }

        public void RemoveBillboard(string id)
        {
            if (id == null || !_boards.TryGetValue(id, out var t)) return;
            _boards.Remove(id);
            if (t != null) Kill(t.gameObject);
            if (_shadows.TryGetValue(id, out var s))
            {
                _shadows.Remove(id);
                if (s != null) Kill(s.gameObject);
            }
            Shoot(); // ушедшая фигура тоже меняет кадр — иначе останется призраком
        }


        /// <summary>Пройти по сцене: сдвиг в МЕТРАХ относительно направления
        /// взгляда (x — вбок, y — вверх, z — вперёд).
        ///
        /// <para>Горизонталь считается по курсу, а не по полному направлению
        /// камеры: иначе «иду вперёд, глядя вверх» поднимает над сценой, и
        /// человек улетает в небо, не поняв, что произошло. Вертикаль ходит
        /// отдельной осью — там, где она вообще нужна.</para></summary>
        public void Walk(Vector3 delta)
        {
            EnsureCamera();
            float yaw = (_rot.y + _lookRot.y) * Mathf.Deg2Rad;
            var forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            var right = new Vector3(forward.z, 0f, -forward.x);
            _pos += right * delta.x + forward * delta.z;

            // ХОДЬБА ПО РЕЛЬЕФУ. Земля у нас перестала быть плоской, а камера
            // об этом не знала: игрок проходил СКВОЗЬ пригорок, оставаясь на
            // своей высоте, и рельеф превращался в рисунок на полу. Между
            // «холмы есть» и «по холмам можно ходить» лежит ровно этот запрос
            // высоты.
            //
            // Спрашиваем не геометрию, а формулу — ту же, по которой построен
            // меш. Никаких коллайдеров, лучей и физики: рельеф задан числами,
            // и высота в точке считается напрямую, за несколько умножений.
            if (_walkGrounded)
            {
                float ground = GroundHeightAt(_pos);
                if (!_eyeHeightKnown)
                {
                    _eyeHeightKnown = true;
                    _eyeHeight = Mathf.Clamp(_pos.y - ground, 0.4f, 6f);
                    Debug.Log($"[lvn-3d] ходьба по рельефу: рост глаз {_eyeHeight:0.00} м");
                }
                float want = ground + _eyeHeight;
                // Сглаживание, а не прыжок: на ступеньке и на камне высота
                // меняется скачком, и мгновенный подъём читается рывком кадра.
                // Полметра в кадре — быстро для глаза и достаточно плавно.
                _pos.y = Mathf.Abs(want - _pos.y) > 2.5f
                    ? want
                    : Mathf.MoveTowards(_pos.y, want, Time.unscaledDeltaTime * 6f);
            }
            else
            {
                _pos.y += delta.y;
            }

            _posTo = _pos;      // ходьба перебивает недоехавший переезд камеры
            _rotTo = _rot;
            _speed = 0f;
            Apply();
            CameraMoved?.Invoke();
        }

        // Идёт ли камера по земле. Включается вместе с ходьбой: пока сцену
        // ставит автор командами `bg3d y=`, камера обязана стоять ровно там,
        // где сказано, — даже в воздухе, если так задумано.
        private bool _walkGrounded;
        private float _eyeHeight = 1.7f;
        // Рост глаз меряется ОТ ЗЕМЛИ, а земля появляется позже команды `walk=1`:
        // `bg3d` стоит первой строкой сцены, `o3d земля` — следующей. Меряя
        // сразу, мы меряли от пустоты и получали упор в потолок. Поэтому
        // откладываем до первого шага, когда земля заведомо на месте.
        private bool _eyeHeightKnown;

        /// <summary>Привязать камеру к земле (или отвязать). Высота глаз
        /// запоминается в момент включения: автор уже поставил камеру на нужную
        /// высоту, и менять её на свою мы не вправе.</summary>
        public void SetWalkGrounded(bool on)
        {
            if (on == _walkGrounded) return;
            _walkGrounded = on;
            _eyeHeightKnown = false;   // померим, когда под ногами будет земля
        }

        /// <summary>Текущее положение камеры в координатах набора — чтобы автор
        /// мог списать найденный ракурс прямо в скрипт.</summary>
        // Разворот фигур на зрителя — перед каждым съёмом кадра.
        private void FaceBoards()
        {
            if (_boards.Count == 0 || _cam == null) return;
            var camPos = _cam.transform.localPosition;
            foreach (var kv in _boards)
            {
                var t = kv.Value;
                if (t == null) continue;
                var d = t.localPosition - camPos;
                d.y = 0f;                       // вертикаль не трогаем никогда
                if (d.sqrMagnitude > 0.0001f)
                    t.localRotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            }
        }

        public Vector3 CameraPos => _pos;
        public Vector2 CameraRot => new Vector2(_rot.x + _lookRot.x, _rot.y + _lookRot.y);

        /// <summary>Осмотреться: сдвиг взгляда в градусах ОТНОСИТЕЛЬНО текущего,
        /// с жёстким потолком. Кадр переснимается сразу, привязанные к сцене
        /// спрайты едут вместе с ним.</summary>
        public void Look(float dPitch, float dYaw)
        {
            EnsureCamera();
            var next = new Vector2(_lookRot.x + dPitch, _lookRot.y + dYaw);
            if (_lookLimit > 0f)
                next = new Vector2(Mathf.Clamp(next.x, -_lookLimit, _lookLimit),
                                   Mathf.Clamp(next.y, -_lookLimit, _lookLimit));
            // Вертикаль всё же держим: перевернуться через макушку — не осмотр,
            // а поломка. 85° почти отвесно вниз и вверх, дальше смысла нет.
            next.x = Mathf.Clamp(next.x, -85f, 85f);
            _lookRot = next;
            Apply();
            CameraMoved?.Invoke();
        }

        /// <summary>Вернуть взгляд к авторскому кадру. Мгновенно — сцене нельзя
        /// оставаться в позе, которой автор не выбирал.</summary>
        public void LookReset()
        {
            if (_lookRot.sqrMagnitude < 0.0001f) return;
            _lookRot = Vector2.zero;
            EnsureCamera();
            Apply();
            CameraMoved?.Invoke();
        }

        /// <summary>Потолок осмотра в градусах; 0 — без ограничений.</summary>
        public void SetLookLimit(float degrees) => _lookLimit = Mathf.Max(0f, degrees);

        /// <summary>Дыхание камеры: <paramref name="amplitude"/> в градусах
        /// (0 — выключить), <paramref name="speed"/> — примерные циклы в
        /// секунду. Держит камеру в постоянном движении, поэтому кадр
        /// переснимается каждый кадр — это единственная цена.</summary>
        public void SetSway(float? amplitude, float? speed)
        {
            EnsureCamera();
            if (amplitude != null) _swayAmp = Mathf.Max(0f, amplitude.Value);
            if (speed != null) _swaySpeed = Mathf.Max(0.01f, speed.Value);
            EnsureTarget(); // режим буфера зависит от качания — переключаем сразу
            if (_swayAmp <= 0f)
            {
                // Возвращаем кадр ровно туда, где он был бы без качания:
                // иначе набор замирал бы в случайной фазе.
                _swayRot = Vector2.zero;
                Apply();
                CameraMoved?.Invoke();
            }
            else enabled = true;
        }

        // Две несоизмеримые частоты на ось: одна синусоида читается как
        // механический маятник, пара — как дыхание живого оператора.
        private void AdvanceSway()
        {
            _swayClock += Time.unscaledDeltaTime * _swaySpeed;
            float t = _swayClock * Mathf.PI * 2f;
            _swayRot = new Vector2(
                (Mathf.Sin(t * 0.37f) * 0.6f + Mathf.Sin(t * 0.83f) * 0.4f) * _swayAmp * 0.6f,
                (Mathf.Sin(t * 0.29f) * 0.6f + Mathf.Sin(t * 0.61f) * 0.4f) * _swayAmp);
        }

        public void Frame(float? x, float? y, float? z,
                          float? pitch, float? yaw, float? fov, float seconds)
        {
            EnsureCamera();
            // Автор задал ракурс — свободный осмотр игрока снимается: иначе
            // «камера в дверь» после его свайпа смотрела бы в кусты.
            _lookRot = Vector2.zero;
            _posTo = new Vector3(x ?? _posTo.x, y ?? _posTo.y, z ?? _posTo.z);
            _rotTo = new Vector3(pitch ?? _rotTo.x, yaw ?? _rotTo.y, 0f);
            _fovTo = fov ?? _fovTo;
            _speed = seconds > 0f ? 1f / seconds : 0f;
            if (_speed <= 0f)
            {
                _pos = _posTo; _rot = _rotTo; _fov = _fovTo;
                Apply();
            }
            enabled = true;
        }

        /// <summary>Tear the set down and free the frame buffer.</summary>
        public void Release()
        {
            if (_set != null) Debug.Log("[lvn-3d] набор снят (Release)");
            _swayAmp = 0f; _swayRot = Vector2.zero; _swayClock = 0f; _lookRot = Vector2.zero;
            _diagBoard = true;
            foreach (var kv in _boards) if (kv.Value != null) Kill(kv.Value.gameObject);
            _boards.Clear();
            foreach (var kv in _shadows) if (kv.Value != null) Kill(kv.Value.gameObject);
            _shadows.Clear();
            // Здесь сцена уходит совсем — с ней уходит и всё, что в ней стояло.
            foreach (var kv in _bodies) if (kv.Value != null) Kill(kv.Value.gameObject);
            _bodies.Clear();
            foreach (var kv in _lights) if (kv.Value != null) Kill(kv.Value.gameObject);
            _lights.Clear();
            _groundVerts = null; _groundFlatY = null;   // новый набор — новый пол
            _hasTerrain = false;                        // и свой рельеф
            if (_set != null)
            {
                if (_env != null) { _env.Restore(); _env = null; }
                Kill(_set);
                _set = null;
            }
            ReleaseSnapshotMaterials();
            if (_cam != null) _cam.enabled = false;
            if (_rt != null)
            {
                if (_cam != null) _cam.targetTexture = null;
                _rt.Release();
                Kill(_rt);
                _rt = null;
                TextureChanged?.Invoke(null);
            }
        }

        /// <summary>Destroy that works outside play mode too: in the editor a
        /// plain Destroy only marks the object and warns, which turns an ordinary
        /// teardown into a test failure.</summary>
        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private void EnsureCamera()
        {
            // The target is ensured OUTSIDE the create-once guard: a painted `bg`
            // tears the backdrop down (Release kills the frame buffer) and the next
            // `bg3d` arrives with the camera still alive. Returning early here left
            // that camera without a targetTexture — and an enabled camera with no
            // target renders straight to the SCREEN, painting the set over every
            // sprite on the stage. One line of ordering, a whole battle invisible.
            if (_cam != null) { EnsureTarget(); return; }
            var go = new GameObject("lvn-3d-camera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.Skybox;
            _cam.backgroundColor = Color.black;
            // ТОЧНОСТЬ ГЛУБИНЫ. Буфер глубины распределён не равномерно, а по
            // ОТНОШЕНИЮ дальней плоскости к ближней: почти вся его точность
            // уходит на первые метры, и чем меньше ближняя, тем меньше остаётся
            // на всё остальное. При 0.05 и 500 отношение — десять тысяч, и на
            // десяти метрах разрешения уже не хватает, чтобы отличить основание
            // склепа от земли под ним: край начинает дрожать и мерцать.
            //
            // Двадцать сантиметров — безопасно: ближе камера к предмету в сцене
            // новеллы не подходит, даже когда игрок идёт вплотную. Полторы
            // сотни метров хватает с запасом — дальше всё съедает туман.
            // Отношение падает до 750, то есть точность растёт больше чем на
            // порядок.
            _cam.nearClipPlane = 0.2f;
            _cam.farClipPlane = 150f;
            // Filming only: never contribute to the on-screen frame directly and
            // never listen — the stage's own camera stays the one that hears.
            var listener = go.GetComponent<AudioListener>();
            if (listener != null) Kill(listener);
            // Never renders on its own — see Shoot(). Unity only auto-renders
            // ENABLED cameras, so a disabled one is free until asked.
            _cam.enabled = false;
            EnsureTarget();
        }

        /// <summary>Нужен ли кадру расширенный диапазон. Спрашиваем у самого
        /// стека, а не выводим из одной кривой: свечению с порогом выше белого
        /// HDR нужен ровно так же, и забыть об этом — значит получить ореол,
        /// которому нечего ловить.</summary>
        private bool NeedsHdr()
        {
            if (_cam == null) return false;
            var stack = _cam.GetComponent<Lvn3DPostStack>();
            return stack != null && stack.NeedsHdr;
        }

        // ФОРМАТ КАДРА. Восьми бит на канал хватает, чтобы показать картинку, и
        // не хватает, чтобы её посчитать: всё ярче белого в них просто не
        // помещается. Но платить за диапазон вдвое не обязательно —
        // B10G11R11 хранит положительные числа с плавающей точкой в те же 32
        // бита на пиксель, что и обычный кадр. Альфа заднику не нужна: набор
        // непрозрачен по определению, значит третий канал можно ужать до
        // десяти бит, а спасённые биты отдать под порядок величины.
        //
        // Поддержку СПРАШИВАЕМ. Она разная у Mali, Adreno и настольных карт, и
        // «обычно работает» здесь недостаточно: на устройстве, где формат не
        // renderable, кадр стал бы чёрным без единого сообщения.
        private static UnityEngine.Experimental.Rendering.GraphicsFormat _hdrFormat;
        private static bool _hdrProbed;

        private static RenderTextureFormat PickColorFormat(bool hdr)
        {
            if (!hdr) return RenderTextureFormat.Default;
            if (!_hdrProbed)
            {
                _hdrProbed = true;
                var packed = UnityEngine.Experimental.Rendering.GraphicsFormat.B10G11R11_UFloatPack32;
                var half4 = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                if (SystemInfo.IsFormatSupported(packed,
                        UnityEngine.Experimental.Rendering.GraphicsFormatUsage.Render))
                    _hdrFormat = packed;
                else if (SystemInfo.IsFormatSupported(half4,
                        UnityEngine.Experimental.Rendering.GraphicsFormatUsage.Render))
                    _hdrFormat = half4;
                else
                    _hdrFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
                Debug.Log($"[lvn-hdr] формат кадра: {_hdrFormat}");
            }
            if (_hdrFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.B10G11R11_UFloatPack32)
                return RenderTextureFormat.RGB111110Float;
            if (_hdrFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat)
                return RenderTextureFormat.ARGBHalf;
            // Устройство не умеет ни того, ни другого — работаем как раньше.
            // Кривая при этом не сломается, просто сжимать будет нечего.
            return RenderTextureFormat.Default;
        }

        private void EnsureTarget()
        {
            // No graphics device (headless server, batch tests) — there is nothing
            // to film into and asking for a frame buffer would take the process
            // down. The set still stands and framing still records, so a headless
            // run stays honest about state without touching the GPU.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

            // Сглаживание задаём САМИ, а не спрашиваем QualitySettings: на
            // Android активен уровень «Medium», где antiAliasing=0 — набор
            // снимался вообще без сглаживания, отсюда лесенка на каждом ребре
            // и мерцание листвы при проездах камеры.
            //
            // Кадр новеллы статичен между движениями камеры, поэтому неподвижный
            // снимаем С ЗАПАСОМ разрешения и ужимаем при показе (суперсэмплинг):
            // платим один раз за кадр, а качество выше любого MSAA. Пока камера
            // едет — обычный размер с MSAA, чтобы не жечь батарею каждый кадр.
            // ДВИЖЕНИЕ — это и дыхание камеры, и осмотр удержанием, а не только
            // переезд между ракурсами. Без них кадр считался неподвижным и
            // снимался с суперсэмплингом ×2 (вчетверо больше пикселей) —
            // КАЖДЫЙ кадр, пока камера дышит. Именно отсюда просадки в размене:
            // к постоянной пересъёмке 8 мегапикселей добавлялся снап клевка.
            bool moving = _live || _speed > 0f || _swayAmp > 0f
                          || _lookRot.sqrMagnitude > 0.0001f;
            // ЖИВОЙ МИР — обычный режим, а не исключение. Замирание кадра
            // остаётся тем, чем и было: экономией на диалоговой паузе, где
            // ничего не происходит. Поэтому живой режим не «терпим», а
            // обеспечен бюджетом: масштаб буфера подстраивается под то, что
            // устройство реально успевает (см. _budgetScale), и красивая
            // картинка не превращается в рваную.
            float ss = moving ? _budgetScale : Supersample;
            int aa = moving ? MovingSamples : 1;
            int w = Mathf.Max(256, Mathf.RoundToInt(Screen.width * ss));
            int h = Mathf.Max(256, Mathf.RoundToInt(Screen.height * ss));
            var want = PickColorFormat(NeedsHdr());
            if (_rt != null && _rt.width == w && _rt.height == h
                && _rt.antiAliasing == aa && _rt.format == want)
            {
                // A camera without a target draws directly to the display. Even
                // if another component cleared it, restore the invariant here.
                if (_cam.targetTexture != _rt) _cam.targetTexture = _rt;
                return;
            }
            if (_rt != null)
            {
                _cam.targetTexture = null;
                _rt.Release();
                Kill(_rt);
            }
            // MSAA спрашиваем ОТДЕЛЬНО от формата и ИМЕННО ДЛЯ НЕГО. То, что
            // формат годится как цель отрисовки, ещё не значит, что для него
            // есть многосэмпловая поверхность: у части мобильных GPU
            // упакованный HDR renderable, но не multisample-совместим. Молча
            // получить кадр без сглаживания легко, заметить — нет.
            var probe = new RenderTextureDescriptor(w, h, want, 24) { msaaSamples = aa };
            int okAA = SystemInfo.GetRenderTextureSupportedMSAASampleCount(probe);
            int useAA = Mathf.Max(1, Mathf.Min(aa, okAA));

            _rt = new RenderTexture(w, h, 24, want)
            {
                name = "lvn-3d-frame",
                antiAliasing = useAA,
                filterMode = FilterMode.Bilinear, // мягко ужимаем крупный кадр
            };
            _cam.targetTexture = _rt;

            // Одной строкой и ФАКТИЧЕСКИМИ значениями — теми, что буфер отдал
            // после создания, а не теми, что мы просили. Просьба и результат
            // расходятся именно там, где потом ищут неделю.
            bool wantedHdr = NeedsHdr();
            bool degraded = wantedHdr && want == RenderTextureFormat.Default;
            Debug.Log($"[lvn-hdr] format={_rt.graphicsFormat} size={_rt.width}×{_rt.height} " +
                      $"requestedMSAA={aa} supportedMSAA={okAA} actualMSAA={_rt.antiAliasing} " +
                      $"colorSpace={QualitySettings.activeColorSpace} created={_rt.IsCreated()} " +
                      $"fallback={degraded.ToString().ToLowerInvariant()}");
            if (degraded)
            {
                // ВЫРОЖДЕННЫЙ РЕЖИМ. Кадр всё ещё рисуется, и на глаз он может
                // выглядеть приемлемо — тем и опасен: свечение с порогом выше
                // белого здесь не найдёт НИЧЕГО, потому что всё ярче единицы
                // срезано до того, как его успели посмотреть. Говорим об этом
                // прямо, иначе проверка пройдёт на картинке, потерявшей смысл.
                Debug.LogWarning("[lvn-hdr] расширенный диапазон недоступен: " +
                                 "кривая сжимает уже срезанное, свечение выше белого не работает");
            }
            TextureChanged?.Invoke(_rt);
            Shoot(); // the buffer is fresh and empty — fill it at once
        }

        private void Apply()
        {
            if (_cam == null) return;
            FaceBoards();   // фигуры поворачиваются к зрителю, не заваливаясь
            _cam.transform.localPosition = Far + _pos;
            _cam.transform.localRotation = Quaternion.Euler(
                _rot.x + _echoRot.x + _swayRot.x + _lookRot.x,
                _rot.y + _echoRot.y + _swayRot.y + _lookRot.y, 0f);
            _cam.fieldOfView = Mathf.Clamp(_fov / _echoZoom, 5f, 120f);
            Shoot();
        }

        /// <summary>Film ONE frame. A visual novel's shot is static between
        /// camera moves — re-filming the identical picture sixty times a second
        /// is the whole cost of the feature for none of the benefit. So the
        /// camera stays disabled and takes a single frame whenever the framing
        /// changes; a still shot then costs nothing at all.</summary>
        private bool _needShot;

        /// <summary>Пометить кадр устаревшим. Сам съём — в конце кадра.
        ///
        /// <para>Раньше здесь стоял прямой Render с защитой «один раз за кадр»,
        /// и это было ошибкой: команды сцены идут ПАЧКОЙ в одном кадре
        /// (поставить набор, задать ракурс, качнуть камеру), первая же снимала
        /// картинку, а все последующие глушились. На экран уходил снимок из
        /// позиции по умолчанию — из центра земли, где видно только небо и
        /// горизонт. Отложенный съём решает обе задачи разом: за кадр ровно
        /// один Render, и он с ФИНАЛЬНЫМ состоянием камеры.</para></summary>
        private void Shoot()
        {
            _lastBuildAt = Time.unscaledTime;   // сцену тронули — пачка ещё идёт
            if (_cam == null || _rt == null || _cam.targetTexture != _rt)
            {
                if (_cam != null) _cam.enabled = false;
                return;
            }
            if (_cam.enabled) return;   // живой набор Unity снимает сам
            _needShot = true;
            enabled = true;             // нужен LateUpdate, даже если набор спит
        }

        /// <summary>Снять кадр немедленно — для редакторских проверок, где
        /// цикла обновления нет.</summary>
        public void ShootNow()
        {
            if (_cam == null || _rt == null || _cam.targetTexture != _rt || _cam.enabled) return;
            _needShot = false;
            _cam.Render();
        }

        private void LateUpdate()
        {
            if (!_needShot) return;
            _needShot = false;
            if (_cam != null && _rt != null && _cam.targetTexture == _rt && !_cam.enabled)
            {
                _cam.Render();
            }
        }

        /// <summary>Масштаб буфера живого кадра, 0.6…1 от экрана.
        ///
        /// <para>Красивая картинка — это в первую очередь ПЛАВНАЯ картинка:
        /// рваные 30 кадров с идеальными пикселями выглядят хуже ровных 60 с
        /// чуть мягче. Поэтому в живом режиме мы не держим разрешение любой
        /// ценой, а подгоняем его под то, что устройство успевает.</para>
        ///
        /// <para>Меняем РЕДКО и ступенями: непрерывная подстройка читается как
        /// «дыхание» резкости и раздражает сильнее, чем сама мягкость.</para></summary>
        private float _budgetScale = 1f;
        private float _frameAvg = 0.016f;
        private float _budgetClock;

        private void UpdateBudget()
        {
            // Сглаженное время кадра: одиночный всплеск (загрузка, сборка мусора)
            // не должен ронять разрешение всей сцены.
            _frameAvg = Mathf.Lerp(_frameAvg, Time.unscaledDeltaTime, 0.1f);
            _budgetClock += Time.unscaledDeltaTime;
            if (_budgetClock < 1.2f) return;   // не чаще раза в секунду с небольшим
            _budgetClock = 0f;

            float target = _budgetScale;
            if (_frameAvg > 0.0235f) target -= 0.15f;        // ниже ~42 кадров — уступаем резкостью
            else if (_frameAvg < 0.0165f) target += 0.15f;   // держим 60 с запасом — возвращаем
            target = Mathf.Clamp(target, 0.6f, 1f);
            if (Mathf.Abs(target - _budgetScale) < 0.01f) return;

            _budgetScale = target;
            Debug.Log($"[bg3d-budget] кадр {_frameAvg * 1000f:0.0} мс → масштаб буфера {_budgetScale:0.00}");
            EnsureTarget();
        }

        private void Update()
        {
            if (_cam == null) return;
            if (_live) UpdateBudget();
            EnsureTarget(); // rotation/resize and the screen-target safety invariant
            // СТРАЖ: держать кадр закрытым можно только пока сцена строится.
            // Если пауза так и не наступила (глава без реплик, ошибка в
            // скрипте), через полторы секунды открываем сами — чёрный экран
            // навсегда хуже, чем показанная наполовину сцена.
            if (_revealHeld)
            {
                _holdClock += Time.unscaledDeltaTime;
                // ПАЧКА ИССЯКЛА — место собрано. Команды сцены идут подряд в
                // одном шаге скрипта; четверть секунды без новых означает, что
                // ставить больше нечего. Это надёжнее, чем ждать реплики:
                // сцена бывает и без единого слова.
                bool settled = Time.unscaledTime - _lastBuildAt > 0.25f;
                if (settled || _holdClock > 1.5f)
                {
                    _holdClock = 0f;
                    ReleaseReveal(0.4f);
                }
                enabled = true;
            }
            else _holdClock = 0f;

            if (_revealSpeed > 0f)
            {
                _reveal = Mathf.Min(1f, _reveal + _revealSpeed * Time.unscaledDeltaTime);
                RevealChanged?.Invoke(_reveal);
                if (_reveal >= 1f) _revealSpeed = 0f;
                else enabled = true; // не засыпаем посреди проявления
            }
            if (_swayAmp > 0f)
            {
                AdvanceSway();
                if (_speed <= 0f)
                {
                    // Качание — само по себе движение кадра: снимаем и двигаем
                    // привязанные спрайты, но НЕ засыпаем, как сделал бы
                    // неподвижный набор.
                    Apply();
                    CameraMoved?.Invoke();
                    return;
                }
            }
            if (_speed <= 0f)
            {
                // A living set keeps filming (Unity renders the enabled camera
                // itself); a still one sleeps until the next `bg3d` wakes it.
                if (!_live && _revealSpeed <= 0f) enabled = false;
                return;
            }
            float step = _speed * Time.unscaledDeltaTime;
            _pos = Vector3.Lerp(_pos, _posTo, Mathf.Clamp01(step));
            _rot = Vector3.Lerp(_rot, _rotTo, Mathf.Clamp01(step));
            _fov = Mathf.Lerp(_fov, _fovTo, Mathf.Clamp01(step));
            Apply();
            CameraMoved?.Invoke();

            if ((_pos - _posTo).sqrMagnitude < 0.0001f &&
                (_rot - _rotTo).sqrMagnitude < 0.0001f &&
                Mathf.Abs(_fov - _fovTo) < 0.01f)
            {
                _pos = _posTo; _rot = _rotTo; _fov = _fovTo;
                Apply();
                _speed = 0f;
                enabled = false; // arrived — stop burning frames
            }
        }

        private void OnDestroy() => Release();
    }
}
