using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The character layer (z-order 1): each actor is a slot (left / center /
    /// right or an explicit x), bottom-anchored and sized by a height fraction.
    /// An actor is drawn as a stack of full-frame sprite layers (body, face,
    /// prop…) resolved by <see cref="SpriteComposer"/> from the cast — pass one
    /// sprite for a flat character, several to composite. Non-speakers dim.
    /// </summary>
    public sealed class ActorLayer : VisualElement
    {
        private readonly Dictionary<string, VisualElement> _actors = new Dictionary<string, VisualElement>();

        public ActorLayer()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Place / update / hide an actor as a stack of layer sprites
        /// (bottom to top). A null/empty list leaves the current art unchanged.</summary>
        public void Apply(string id, IReadOnlyList<Sprite> layers, string position, float? x, float heightFraction, bool show)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!_actors.TryGetValue(id, out var slot))
            {
                slot = new VisualElement { name = "vn-actor-" + id, pickingMode = PickingMode.Ignore };
                slot.style.position = Position.Absolute;
                slot.style.bottom = 0;
                Add(slot);
                _actors[id] = slot;
            }

            if (layers != null && layers.Count > 0)
            {
                slot.Clear();
                foreach (var sprite in layers)
                {
                    if (sprite == null) continue;
                    var img = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                    img.style.position = Position.Absolute;
                    img.style.left = 0;
                    img.style.right = 0;
                    img.style.top = 0;
                    img.style.bottom = 0;
                    slot.Add(img);
                }
            }

            float fx = x ?? SlotX(position);
            float h = heightFraction > 0.05f ? heightFraction : 0.62f;
            slot.style.height = Length.Percent(h * 100f);
            slot.style.width = Length.Percent(46f);
            slot.style.left = Length.Percent(fx * 100f);
            slot.style.translate = new Translate(Length.Percent(-50f), 0, 0);
            slot.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Full opacity for the speaker, dim for everyone else. Pass
        /// null to undim all.</summary>
        public void SetSpeaker(string id)
        {
            foreach (var kv in _actors)
                kv.Value.style.opacity = id == null || kv.Key == id ? 1f : 0.55f;
        }

        public void RemoveAll()
        {
            Clear();
            _actors.Clear();
        }

        private static float SlotX(string position)
        {
            switch (position)
            {
                case "left": return 0.25f;
                case "right": return 0.75f;
                case "center": return 0.5f;
                default: return 0.5f;
            }
        }
    }
}
