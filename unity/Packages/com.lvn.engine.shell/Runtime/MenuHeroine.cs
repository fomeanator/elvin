using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ГЕРОИНЯ НА ПЕРЕДНЕМ ПЛАНЕ МЕНЮ (концепция Ильи и партнёра, 26.08):
    /// живая кукла (те же слои и оси, что в гардеробе) стоит на каждом экране
    /// меню поверх общего полотна; вкладки и контент едут ПОД и НАД ней, она —
    /// неподвижный передний план. Гардероб лишь меняет UI вокруг неё.
    /// Слои собираются по шаблонам манифеста (sprites.&lt;entity&gt;.layers) с
    /// осями из LvnWardrobe; смена наряда обновляет куклу сама (Changed).
    /// </summary>
    public sealed class MenuHeroine : VisualElement
    {
        private readonly ILvnAssets _assets;
        private readonly LvnManifest _manifest;
        private readonly string _entity;
        private int _gen;

        /// <summary>Есть ли у игры героиня (иначе слой мёртв навсегда).</summary>
        public bool HasEntity { get; private set; }

        public MenuHeroine(LvnManifest manifest, ILvnAssets assets)
        {
            _manifest = manifest;
            _assets = assets;
            _entity = manifest?.ui?.wardrobe?.entity;
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            // ПО ЦЕНТРУ и крупно (Илья 26.08), ногами к нижнему меню:
            // якорь — середина экрана, сдвиг на половину собственной ширины.
            style.left = Length.Percent(50f);
            style.translate = new Translate(Length.Percent(-50f), 0f);
            style.bottom = 118;
            style.top = Length.Percent(12f);
            style.justifyContent = Justify.FlexEnd;
            if (string.IsNullOrEmpty(_entity)
                || _manifest?.sprites == null || !_manifest.sprites.ContainsKey(_entity))
            {
                style.display = DisplayStyle.None; // игра без героини — слоя нет
                return;
            }
            HasEntity = true;
            Lvn.UI.LvnWardrobe.Changed += OnWardrobeChanged;
            RegisterCallback<DetachFromPanelEvent>(_ => Lvn.UI.LvnWardrobe.Changed -= OnWardrobeChanged);
            RegisterCallback<GeometryChangedEvent>(_ => FitWidth());
            LvnAsync.Fire(RebuildAsync(), "MenuHeroine");
        }

        private void OnWardrobeChanged(string _) => LvnAsync.Fire(RebuildAsync(), "MenuHeroine");

        private void FitWidth()
        {
            var def = _manifest.sprites[_entity];
            float aspect = def.aspect > 0.01f ? def.aspect : 0.6f;
            style.width = resolvedStyle.height * aspect;
        }

        // Значение оси: надетое из гардероба, иначе дефолт манифеста, иначе
        // первый вариант оси.
        private string AxisValue(LvnSpriteEntity def, string axis)
        {
            var eq = Lvn.UI.LvnWardrobe.Equipped(_entity);
            if (eq != null && eq.TryGetValue(axis, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (def.defaults != null && def.defaults.TryGetValue(axis, out var d) && !string.IsNullOrEmpty(d)) return d;
            if (def.axes != null && def.axes.TryGetValue(axis, out var vals) && vals != null && vals.Count > 0)
                return vals[0];
            return "";
        }

        private async Task RebuildAsync()
        {
            var def = _manifest.sprites[_entity];
            if (def?.layers == null) return;
            int gen = ++_gen;

            var urls = new List<string>();
            foreach (var layer in def.layers)
            {
                if (string.IsNullOrEmpty(layer?.url)) continue;
                string url = layer.url;
                if (def.axes != null)
                    foreach (var axis in def.axes.Keys)
                        url = url.Replace("{" + axis + "}", AxisValue(def, axis));
                if (!url.Contains("{")) urls.Add(url); // неразрешённый шаблон — слой молчит
            }

            var sprites = new List<Sprite>(urls.Count);
            foreach (var url in urls)
            {
                var sp = await _assets.LoadSpriteAsync(url, default);
                if (gen != _gen) return; // наряд сменили быстрее, чем доехал арт
                sprites.Add(sp);
            }

            Clear();
            foreach (var sp in sprites)
            {
                if (sp == null) continue;
                var img = new Image { sprite = sp, scaleMode = ScaleMode.ScaleToFit };
                img.pickingMode = PickingMode.Ignore;
                img.style.position = Position.Absolute;
                img.style.left = 0; img.style.right = 0;
                img.style.top = 0; img.style.bottom = 0;
                Add(img);
            }
            FitWidth();
        }
    }
}
