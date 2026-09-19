using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public static partial class LvnSpinePoster
    {
        private static readonly Dictionary<VisualElement, Attachment> _attachments = new Dictionary<VisualElement, Attachment>();

        /// <summary>Release a poster before applying freshly downloaded content at the same URLs.</summary>
        public static void Release(VisualElement host)
        {
            if (host != null && _attachments.TryGetValue(host, out var owner)) owner.Dispose();
        }

        // Pin each page BEFORE awaiting the next page/background. Otherwise
        // loading a large background may evict an already loaded atlas page.
        // The completed rig acquires its own pins before these are released.
        internal sealed class LoadingPins : IDisposable
        {
            private readonly ILvnPinLedger _ledger;
            private readonly List<Sprite> _sprites = new List<Sprite>();
            private readonly LvnPinBoard<object> _pins = new LvnPinBoard<object>();
            internal LoadingPins(ILvnPinLedger ledger) => _ledger = ledger;
            internal void Hold(Sprite sprite)
            {
                if (_ledger == null || sprite == null) return;
                _sprites.Add(sprite);
                _pins.Hold(this, _ledger, _sprites);
            }
            public void Dispose()
            {
                _pins.Release(this);
                _sprites.Clear();
            }
        }

        // One host owns one rig. Refreshing menu data used to add another
        // camera every time; only detaching the entire screen released them.
        private sealed class Attachment
        {
            internal VisualElement Host, VisibilityTarget;
            internal ILvnPinLedger Ledger;
            internal LvnSpineRef Spine;
            internal Rig Rig;
            internal int Revision;
            internal Action OnFallback;
            internal Action<RenderTexture> OnPoster;
            internal EventCallback<DetachFromPanelEvent> OnDetach;
            internal bool Disposed;
            internal bool Current(int revision) => !Disposed && Revision == revision;

            internal void DropRig()
            {
                if (Rig == null) return;
                // Destroy is deferred: disable now so replacement never renders twice.
                if (Rig.Root != null) Rig.Root.SetActive(false);
                Cleanup(Rig.Root, Rig.Rt, null);
                Rig = null;
            }
            internal void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                Host.UnregisterCallback(OnDetach);
                _attachments.Remove(Host);
                if (Rig != null) Host.style.backgroundImage = StyleKeyword.None;
                DropRig(); _pins.Release(Host);
            }
            internal void Fallback()
            {
                Dispose();
                OnFallback?.Invoke();
            }
        }

        private static bool SameSpine(LvnSpineRef a, LvnSpineRef b)
            => a != null && a.json == b.json && a.atlas == b.atlas && a.texture == b.texture
                && a.bg == b.bg && a.auto == b.auto && a.scale == b.scale && a.fit == b.fit;

        private static void AttachOwned(VisualElement host, LvnSpineRef spine,
            Func<string, Task<string>> loadText, Func<string, Task<Sprite>> loadSprite,
            ILvnPinLedger ledger, Action onFallback, Action<RenderTexture> onPoster, VisualElement visibilityTarget)
        {
            if (!_attachments.TryGetValue(host, out var owner))
            {
                owner = new Attachment { Host = host };
                owner.OnDetach = _ => owner.Dispose();
                _attachments.Add(host, owner);
                host.RegisterCallback(owner.OnDetach);
            }
            bool reuse = SameSpine(owner.Spine, spine) && ReferenceEquals(owner.Ledger, ledger);
            owner.OnFallback = onFallback; owner.OnPoster = onPoster;
            owner.VisibilityTarget = visibilityTarget;
            if (owner.Rig != null) owner.Rig.Ticker.VisibilityTarget = visibilityTarget;
            if (reuse)
            {
                if (owner.Rig != null) onPoster?.Invoke(owner.Rig.Rt);
                return;
            }
            owner.Ledger = ledger;
            // Snapshot mutable manifest data; a newer request invalidates any
            // older async load before it can overwrite the chosen poster.
            owner.Spine = new LvnSpineRef { json = spine.json, atlas = spine.atlas, texture = spine.texture,
                bg = spine.bg, auto = spine.auto, scale = spine.scale, fit = spine.fit };
            LvnAsync.Fire(BuildAsync(owner, ++owner.Revision, owner.Spine, loadText, loadSprite), "SpinePoster " + spine.json);
        }
    }
}
