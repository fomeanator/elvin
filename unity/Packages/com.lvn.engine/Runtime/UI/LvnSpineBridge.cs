using System;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The seam between the engine core and the OPTIONAL spine-unity runtime
    /// (the Addressables pattern): the core carries no Spine dependency — the
    /// <c>Lvn.Engine.Spine</c> assembly compiles only when the
    /// <c>com.esotericsoftware.spine.spine-unity</c> package is installed, and
    /// hooks these delegates at load. A <c>kind: "spine"</c> catalog entity
    /// renders through them; without the package it logs a clear warning
    /// instead. Spine runtimes require a Spine editor license in production —
    /// the engine itself stays license-free.
    /// </summary>
    public static class LvnSpineBridge
    {
        /// <summary>Build a skeleton CONTAINER under the actor's slot from
        /// RUNTIME data: (parent, skeleton json, atlas text, atlas page textures,
        /// scale, optional bg texture) → the container GameObject (holds the bg
        /// behind + the skeleton, faded/scaled/dragged as one unit). The textures
        /// are the atlas' pages IN ORDER (multi-page atlases pass more than one).
        /// Set by the optional assembly.</summary>
        public static Func<RectTransform, string, string, Texture2D[], float, Texture2D, GameObject> Create;

        /// <summary>OPTIONAL: parse the skeleton data OFF the main thread and
        /// prime the resource cache, so a following <see cref="Create"/> call
        /// pays only the (cheap) mesh build: (skeleton json, atlas text, atlas
        /// page textures) → completes when the parse landed. Multi-MB skeleton
        /// JSONs cost 100-170 ms to parse — on the main thread that's a visible
        /// stutter in whatever idle animation is playing. Callers must treat
        /// this as best-effort: on any failure Create() simply parses
        /// synchronously as before.</summary>
        public static Func<string, string, Texture2D[], System.Threading.Tasks.Task> Prepare;

        /// <summary>Play a named animation: (skeleton GO, name, loop).</summary>
        public static Action<GameObject, string, bool> Play;

        /// <summary>Show/hide a skeleton with a short default fade instead of a
        /// hard toggle: (skeleton GO, visible). Fades out THEN deactivates.</summary>
        public static Action<GameObject, bool> SetVisible;

        /// <summary>Re-fit a live skeleton to the screen: (skeleton GO, catalog
        /// scale, fit mode). Called on every actor command so `scale`/`fit`
        /// resize the Spine in real time. Fit modes: "width" (default, width-to-
        /// width), "height", "cover", "contain".</summary>
        public static Action<GameObject, float, string> Refit;

        /// <summary>Drop the integration's parsed-skeleton cache. The stage
        /// calls it from <c>ResetStage</c>'s asset unload: cached
        /// SkeletonData/materials reference textures the loader is about to
        /// Destroy — reusing them after an unload draws black/pink skeletons.</summary>
        public static Action ClearCache;

        public static bool Available => Create != null;
    }
}
