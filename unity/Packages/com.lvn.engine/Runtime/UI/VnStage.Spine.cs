using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public sealed partial class VnStage
    {
        // ── spine actors (the optional spine-unity runtime) ─────────────────
        private readonly Dictionary<string, GameObject> _spineActors = new Dictionary<string, GameObject>();
        // Replays fire actor commands in a burst while the first build is still
        // awaiting its file loads — reserve the id or every call builds its own
        // skeleton (the shadow-clone army). Plays requested mid-load apply after.
        private readonly HashSet<string> _spineLoading = new HashSet<string>();
        private readonly Dictionary<string, (string name, bool loop)> _spinePendingPlay
            = new Dictionary<string, (string, bool)>();

        private async Task ApplySpineAsync(string id, Lvn.Content.LvnSpriteEntity e, JObject cmd)
        {
            var placement = _placements.TryGetValue(id, out var prevSp)
                ? PlacementFrom(cmd, prevSp) : PlacementFrom(cmd);
            _placements[id] = placement; // sticky base (spine actors too)

            if (!LvnSpineBridge.Available)
            {
                Debug.LogWarning("[lvn] '" + id + "' is kind:spine, but the spine-unity integration " +
                                 "isn't installed (add com.esotericsoftware.spine.spine-unity to the project)");
                return;
            }
            if (!(_renderer is CanvasSceneRenderer canvas))
            {
                Debug.LogWarning("[lvn] spine entities render on the Canvas scene path only");
                return;
            }

            bool alreadyBuilt = _spineActors.TryGetValue(id, out var existing) && existing != null;

            // Fast hide: an already-built skeleton just toggles off. But a
            // NEVER-built one with show=false is WARMED below — created hidden so
            // the later reveal is a single SetActive, never a synchronous build
            // in the frame the player sees. (Author: `actor id=x show=false` a
            // couple of lines before you need it.)
            if (!placement.Show && alreadyBuilt)
            {
                existing.SetActive(false);
                return;
            }

            // The full sprite-actor placement vocabulary applies: x/y/width/height/
            // anchor/flip/rotation via the slot, opacity via the rig's CanvasGroup.
            _renderer.PlaceActor(id, placement);
            canvas.SetActorOpacity(id, placement.Opacity);
            var slot = canvas.RigFor(id);
            if (slot == null) return;

            // Build once — for a real show OR a warm (show=false, never built).
            // The reveal frame never pays the skeleton-parse + mesh cost.
            if (!alreadyBuilt && !_spineLoading.Contains(id))
            {
                _spineLoading.Add(id);
                try
                {
                    var sp = e.spine;
                    string json = null, atlasText = null;
                    Sprite page = null;
                    try
                    {
                        json = await Assets.LoadTextAsync(sp.json, _cts.Token);
                        atlasText = await Assets.LoadTextAsync(sp.atlas, _cts.Token);
                        page = await Assets.LoadSpriteAsync(sp.texture, _cts.Token);
                    }
                    catch { }
                    if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(atlasText) || page == null)
                    {
                        Debug.LogWarning("[lvn] spine '" + id + "': failed to load skeleton files");
                        return;
                    }
                    var go = LvnSpineBridge.Create(slot, json, atlasText, page.texture, sp.scale);
                    if (go == null) return;
                    _spineActors[id] = go;
                    existing = go;
                    if (_spinePendingPlay.TryGetValue(id, out var pend))
                    {
                        _spinePendingPlay.Remove(id);
                        LvnSpineBridge.Play(go, pend.name, pend.loop);
                    }
                    else if (!string.IsNullOrEmpty(sp.auto)) LvnSpineBridge.Play(go, sp.auto, true);
                }
                finally { _spineLoading.Remove(id); }
            }

            // Visibility follows show; a warm build stays inactive until the
            // later show=true flips it on with zero build cost.
            if (existing != null)
            {
                existing.SetActive(placement.Show);
                // Real-time size: re-fit to the slot's (possibly changed) height
                // each command, so `actor id=x height=…` resizes on the fly.
                LvnSpineBridge.Refit?.Invoke(existing, e.spine.scale);
            }

            var play = (string)cmd["play"];
            if (!string.IsNullOrEmpty(play))
            {
                if (_spineActors.TryGetValue(id, out var g) && g != null)
                    LvnSpineBridge.Play(g, play, BoolOr(cmd["loop"], false));
                else
                    _spinePendingPlay[id] = (play, BoolOr(cmd["loop"], false)); // lands after the build
            }

            // Spine actors join drag & drop like any object: the drag moves the
            // slot, the skeleton rides the rig and keeps animating.
            if (cmd["draggable"] != null)
            {
                if (BoolOr(cmd["draggable"], false))
                    _draggables[id] = new DragInfo
                    {
                        Home = placement,
                        Drop = ParseDropMap((string)cmd["on_drop"]),
                        MissLabel = (string)cmd["on_drop_miss"],
                        BoundToScreen = (string)cmd["drag_bounds"] != "none",
                    };
                else
                    _draggables.Remove(id);
            }
        }
    }
}
