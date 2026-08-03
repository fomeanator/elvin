using System.Threading.Tasks;
using Lvn.UI.World;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Команды `o3d` и `light` — сцена, которую автор собирает из скрипта.
    ///
    /// <para>Смысл разделения с `bg3d`: тот ставит ГОТОВОЕ место (собранный
    /// набор из бандла), эти две строят место ИЗ ЧАСТЕЙ. Обе дороги ведут в один
    /// и тот же набор и одну и ту же камеру — построенную сцену так же снимают,
    /// так же качают и так же населяют персонажами через `obj … in3d=1`.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        private async Task ApplyO3DAsync(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;

            if (BoolOr(cmd["off"], false))
            {
                _renderer?.RemoveBody3D(id);
                return;
            }
            // Тело без сцены поставить некуда. Строим пустую — так `o3d` работает
            // и сам по себе, без «сначала объявите набор»: место в игре может
            // целиком состоять из коробок и плоскостей.
            if (_renderer != null && !_renderer.Has3DSet) _renderer.Build3D();

            var body = new Lvn3DBackdrop.Body
            {
                Shape = (string)cmd["shape"],
                Hills = Num(cmd["hills"]) ?? 0f,
                HillSize = Num(cmd["hill_size"]) ?? 0f,
                Detail = Num(cmd["detail"]) ?? 0f,
                Cells = (int)(Num(cmd["cells"]) ?? 0f),
                Model = (string)cmd["model"],
                Pos = Vec3(cmd["pos"]),
                Size = Size3(cmd["size"]),
                Rot = Rot3(cmd),
                Tint = Col(cmd["color"]),
                Alpha = Num(cmd["alpha"]),
                Glow = Num(cmd["glow"]),
                Ground = cmd["ground"] != null ? BoolOr(cmd["ground"], true) : (bool?)null,
                Shadow = cmd["shadow"] != null ? BoolOr(cmd["shadow"], true) : (bool?)null,
                // Посев: `count` превращает одно описание в рощу.
                Count = (int)(Num(cmd["count"]) ?? 0f),
                Area = Vec2(cmd["area"]) ?? Vector2.zero,
                Seed = (int)(Num(cmd["seed"]) ?? 1f),
                ScaleVar = Num(cmd["scale_var"]) ?? 0f,
                YawVar = Num(cmd["yaw_var"]) ?? 0f,
                Gap = Num(cmd["gap"]) ?? 0f,
                Kinds = List(cmd["kinds"]),
                Tints = Colors(cmd["colors"]),
                Wind = Num(cmd["wind"]) ?? 0f,
                Fade = Num(cmd["fade"]),
                Shader = (string)cmd["shader"],
                Bump = Num(cmd["bump"]),
                Tiling = Num(cmd["tiling"]),
                RoadEdge = Num(cmd["edge"]),
                RoadRuts = Num(cmd["ruts"]),
                RoadWet = Num(cmd["wet"]),
                Rim = Num(cmd["rim"]),
                Outline = Num(cmd["outline"]),
                OutlineTint = Col(cmd["outline_color"]),
                Spots = Spots(cmd["at"]),
                Dur = Num(cmd["dur"]) ?? 0f,
                Dissolve = Num(cmd["dissolve"]),
                Spin = Num(cmd["spin"]),
                Bob = Vec3(cmd["bob"]) ?? (Num(cmd["bob"]) is float bh
                    ? new Vector3(0f, bh, 0f) : (Vector3?)null),
                BobSpeed = Num(cmd["bob_speed"]),
                Pulse = Num(cmd["pulse"]),
                PulseSpeed = Num(cmd["pulse_speed"]),
            };

            // МОДЕЛЬ ФАЙЛОМ. `model=` обычно называет объект внутри собранного
            // набора, но если это похоже на путь — геометрия приезжает текстом,
            // как и всё остальное в сцене. Так модель, сделанную нейросетью или
            // руками в редакторе, можно поставить, не пересобирая набор.
            var modelRef = (string)cmd["model"];
            if (Lvn3DBackdrop_LooksLikeObj(modelRef) && Assets != null)
            {
                int epochM = _stageEpoch;
                var cachedMesh = Lvn.UI.World.LvnObjMesh.Cached(modelRef);
                if (cachedMesh != null) body.Mesh = cachedMesh;
                else
                {
                    try
                    {
                        var objText = await Assets.LoadTextAsync(modelRef, _cts.Token);
                        if (!StageCurrent(epochM)) return;
                        body.Mesh = Lvn.UI.World.LvnObjMesh.ParseCached(modelRef, objText);
                        if (body.Mesh == null)
                            LvnPlayer.Log?.Invoke($"[lvn-o3d] модель '{modelRef}' пуста или не разобралась");
                    }
                    catch (System.OperationCanceledException) { return; }
                    catch (System.Exception e)
                    {
                        LvnPlayer.Log?.Invoke($"[lvn-o3d] модель '{modelRef}': {e.Message}");
                    }
                }
                // Имя набора здесь ни при чём — иначе движок пойдёт искать
                // объект «/content/models/камень.obj» внутри бандла и не найдёт.
                if (body.Mesh != null) body.Model = null;
            }

            // Картинки — единственное, чего приходится ждать: и текстура
            // поверхности, и плоская фигура приезжают по сети.
            var texUrl = (string)cmd["texture"];
            var sprUrl = (string)cmd["sprite"];
            Sprite flat = null;
            if (!string.IsNullOrEmpty(texUrl))
            {
                int epoch = _stageEpoch;
                body.Texture = await LoadSurfaceAsync(texUrl, linear: false, epoch);
                if (!StageCurrent(epoch)) return;
            }
            else if (!string.IsNullOrEmpty(sprUrl))
            {
                int epoch = _stageEpoch;
                flat = await LoadSceneSpriteAsync(sprUrl, "o3d", () => StageCurrent(epoch));
                if (!StageCurrent(epoch)) return;
            }

            // Звук тела: грузится тем же путём, что и всё остальное в сцене.
            var soundUrl = (string)cmd["sound"];
            if (!string.IsNullOrEmpty(soundUrl) && Assets != null)
            {
                int epochS = _stageEpoch;
                try
                {
                    var clip = await Assets.LoadAudioAsync(soundUrl, _cts.Token);
                    if (!StageCurrent(epochS)) return;
                    body.Sound = clip;
                }
                catch (System.Exception e)
                {
                    LvnPlayer.Log?.Invoke($"[lvn-o3d] звук '{soundUrl}' не загрузился: {e.Message}");
                }
            }
            body.SoundRange = Num(cmd["sound_range"]);
            body.SoundVolume = Num(cmd["sound_volume"]);

            var normUrl = (string)cmd["normal"];
            if (!string.IsNullOrEmpty(normUrl))
            {
                int epoch2 = _stageEpoch;
                body.Normal = await LoadSurfaceAsync(normUrl, linear: true, epoch2);
                if (!StageCurrent(epoch2)) return;
            }

            // Плоская фигура — та же дорога, что у персонажей: биллборд в
            // наборе. Одна реализация «фигура в сцене», а не вторая рядом.
            if (flat != null)
            {
                var p = new Placement
                {
                    X = 0.5f, Y = 0.5f, Show = true, In3D = true,
                    World = body.Pos ?? Vector3.zero,
                    WorldHeight = Num(cmd["height"]) ?? body.Size?.y ?? 1.8f,
                    Flip = BoolOr(cmd["flip"], false),
                };
                _renderer?.PlaceActor(id, p);
                _renderer?.ApplyActor(id, new[] { flat }, p, null, null, null);
                return;
            }

            // Нажатие на тело: поле объявлено в грамматике, и до сих пор оно
            // ничего не делало — документация обещала то, чего нет. Худший вид
            // тихого отказа: автор пишет правильно и не понимает, почему клик
            // не работает.
            _renderer?.SetBody3DClick(id, (string)cmd["on_click"]);

            if (_renderer != null && !_renderer.Body3D(id, body))
                LvnPlayer.Log?.Invoke($"[lvn-o3d] '{id}' не встал: нет сцены или неизвестная форма");
        }

        /// <summary>Текстура поверхности — материал, который ПОВТОРЯЕТСЯ по телу,
        /// а не картинка, показанная целиком. Настройки у них разные настолько,
        /// что общий тракт даёт видимый брак (полосы вдоль взгляда, кипение
        /// вдали), поэтому здесь отдельная дорога.
        ///
        /// Загрузчик, написанный до появления этого метода, вернёт null — тогда
        /// падаем на обычный спрайт: материал будет виден, просто с теми самыми
        /// дефектами. Молча остаться без земли было бы хуже.</summary>
        private static bool Lvn3DBackdrop_LooksLikeObj(string s) =>
            Lvn.UI.World.LvnObjMesh.LooksLikePath(s);

        private async Task<Texture> LoadSurfaceAsync(string url, bool linear, int epoch)
        {
            if (Assets == null || _cts == null) return null;
            try
            {
                var tex = await Assets.LoadSurfaceTextureAsync(url, linear, _cts.Token);
                if (!StageCurrent(epoch)) return null;
                if (tex != null) return tex;
            }
            catch (System.OperationCanceledException) { return null; }
            catch (System.Exception e)
            {
                LvnPlayer.Log?.Invoke($"[lvn-o3d] текстура '{url}': {e.Message}");
            }

            var what = linear ? "o3d normal" : "o3d";
            var sprite = await LoadSceneSpriteAsync(url, what, () => StageCurrent(epoch));
            if (!StageCurrent(epoch) || sprite == null) return null;
            // Спрайтовая текстура повторяться не умеет — но разложить её по
            // правилам поверхности всё же можно, и полосы уйдут.
            LvnTextures.Configure(sprite.texture);
            return sprite.texture;
        }

        private void ApplyLight(JObject cmd)
        {
            if (_renderer != null && !_renderer.Has3DSet) _renderer.Build3D();
            _renderer?.Light3D(
                (string)cmd["kind"] ?? "sun",
                (string)cmd["id"],
                Vec2(cmd["angle"]),
                Vec3(cmd["pos"]),
                Col(cmd["color"]),
                Num(cmd["power"]),
                Num(cmd["range"]),
                Num(cmd["near"]),
                Num(cmd["far"]),
                Col(cmd["top"]),
                Col(cmd["bottom"]),
                BoolOr(cmd["off"], false),
                Num(cmd["dur"]) ?? 0f,
                Num(cmd["flicker"]) ?? 0f);
        }

        // --- разбор значений -------------------------------------------------

        /// <summary>«x,y,z» или одно число (тогда все три равны). Метры.</summary>
        private static Vector3? Vec3(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
            {
                float v = (float)t;
                return new Vector3(v, v, v);
            }
            var parts = ((string)t ?? "").Split(',');
            if (parts.Length < 3) return null;
            if (float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y) &&
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z))
                return new Vector3(x, y, z);
            return null;
        }

        private static Vector2? Vec2(JToken t)
        {
            if (t == null) return null;
            var parts = ((string)t ?? "").Split(',');
            if (parts.Length < 2) return null;
            if (float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
                return new Vector2(x, y);
            return null;
        }

        /// <summary>Размер: одно число — куб/шар со стороной, «x,y,z» — по осям.</summary>
        private static Vector3? Size3(JToken t) => Vec3(t);

        private Vector3? Rot3(JObject cmd)
        {
            var p = Num(cmd["pitch"]); var y = Num(cmd["yaw"]); var r = Num(cmd["roll"]);
            if (p == null && y == null && r == null) return null;
            return new Vector3(p ?? 0f, y ?? 0f, r ?? 0f);
        }

        /// <summary>Места копий: «x,z;x,z;…» в метрах. Так говорит карта.</summary>
        private static Vector2[] Spots(JToken t)
        {
            var s = (string)t;
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(';');
            var list = new System.Collections.Generic.List<Vector2>(parts.Length);
            foreach (var p in parts)
            {
                var xz = p.Split(',');
                if (xz.Length < 2) continue;
                if (float.TryParse(xz[0].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(xz[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var z))
                    list.Add(new Vector2(x, z));
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Список через запятую: «ель,куст,валун» — виды одного посева.</summary>
        private static string[] List(JToken t)
        {
            var s = (string)t;
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        /// <summary>Несколько окрасов через запятую. Их держим короткими: каждый
        /// окрас — отдельный материал, то есть отдельный вызов отрисовки.</summary>
        private static Color[] Colors(JToken t)
        {
            var parts = List(t);
            if (parts == null) return null;
            var list = new System.Collections.Generic.List<Color>(parts.Length);
            foreach (var p in parts)
            {
                var c = Col(p);
                if (c is Color col) list.Add(col);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Цвет «#rrggbb» или «#rrggbbaa». Мусор — не цвет, а молчание:
        /// красить сцену в чёрное из-за опечатки хуже, чем не покрасить.</summary>
        private static Color? Col(JToken t)
        {
            var s = (string)t;
            if (string.IsNullOrEmpty(s)) return null;
            if (!s.StartsWith("#")) s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out var c) ? c : (Color?)null;
        }
    }
}
