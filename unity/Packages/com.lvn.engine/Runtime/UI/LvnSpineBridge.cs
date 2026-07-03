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
        /// <summary>Build a skeleton under the actor's slot from RUNTIME data:
        /// (parent, skeleton json, atlas text, atlas texture, scale) → the
        /// SkeletonGraphic GameObject. Set by the optional assembly.</summary>
        public static Func<RectTransform, string, string, Texture2D, float, GameObject> Create;

        /// <summary>Play a named animation: (skeleton GO, name, loop).</summary>
        public static Action<GameObject, string, bool> Play;

        public static bool Available => Create != null;
    }
}
