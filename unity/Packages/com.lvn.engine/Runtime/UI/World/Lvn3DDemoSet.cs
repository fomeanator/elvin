using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// A room built out of primitives, so `bg3d` can be seen working before a
    /// project has any 3D art. Ask for it by the reserved id <c>demo</c>:
    ///
    /// <code>
    /// bg3d id=demo
    /// bg3d x=0 y=1.6 z=-3.2 yaw=0        # standing at the door
    /// bg3d x=1.9 y=1.5 z=0.4 yaw=-70 dur=1.4   # gliding to the window
    /// </code>
    ///
    /// The room is deliberately plain — floor, three walls, a window, a table,
    /// two chairs, a lamp — because its job is to prove the angles, not to look
    /// finished. Everything is placed in metres around the origin, so the camera
    /// coordinates a script writes here read the same way they will against real
    /// art: eye height is about 1.6, the room is 6×4.
    /// </summary>
    public static class Lvn3DDemoSet
    {
        /// <summary>The id a script uses to ask for this set.</summary>
        public const string Id = "demo";

        /// <summary>Build the room. The caller owns the returned object (the
        /// backdrop instantiates it like any prefab and destroys it on release).</summary>
        public static GameObject Build()
        {
            var root = new GameObject("lvn-demo-room");

            // Muted, slightly warm surfaces: enough contrast to read depth on a
            // phone without pretending to be finished art.
            var floorMat = Mat(new Color(0.30f, 0.28f, 0.27f));
            var wallMat = Mat(new Color(0.55f, 0.53f, 0.50f));
            var woodMat = Mat(new Color(0.42f, 0.30f, 0.20f));
            var glassMat = Mat(new Color(0.62f, 0.74f, 0.82f));
            var lampMat = Mat(new Color(1f, 0.94f, 0.78f));

            // Room shell: 6 wide, 4 deep, 2.8 high. Three walls only — the fourth
            // is where the camera lives, exactly like a stage set.
            Box(root, "floor", new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0.1f, 4f), floorMat);
            Box(root, "ceiling", new Vector3(0f, 2.85f, 0f), new Vector3(6f, 0.1f, 4f), wallMat);
            Box(root, "wall-back", new Vector3(0f, 1.4f, 2.05f), new Vector3(6f, 2.8f, 0.1f), wallMat);
            Box(root, "wall-left", new Vector3(-3.05f, 1.4f, 0f), new Vector3(0.1f, 2.8f, 4f), wallMat);
            Box(root, "wall-right", new Vector3(3.05f, 1.4f, 0f), new Vector3(0.1f, 2.8f, 4f), wallMat);

            // A window on the right wall — the bright anchor that makes a camera
            // move read as a move.
            Box(root, "window", new Vector3(2.98f, 1.6f, 0.4f), new Vector3(0.06f, 1.3f, 1.8f), glassMat);
            Box(root, "window-frame", new Vector3(2.96f, 1.6f, 0.4f), new Vector3(0.04f, 1.42f, 1.92f), woodMat);

            // Furniture: a table with two chairs, and a floor lamp in the corner.
            Box(root, "table-top", new Vector3(0f, 0.75f, 0f), new Vector3(1.6f, 0.08f, 0.9f), woodMat);
            Box(root, "table-leg-1", new Vector3(-0.7f, 0.37f, -0.35f), new Vector3(0.08f, 0.74f, 0.08f), woodMat);
            Box(root, "table-leg-2", new Vector3(0.7f, 0.37f, -0.35f), new Vector3(0.08f, 0.74f, 0.08f), woodMat);
            Box(root, "table-leg-3", new Vector3(-0.7f, 0.37f, 0.35f), new Vector3(0.08f, 0.74f, 0.08f), woodMat);
            Box(root, "table-leg-4", new Vector3(0.7f, 0.37f, 0.35f), new Vector3(0.08f, 0.74f, 0.08f), woodMat);

            Box(root, "chair-a-seat", new Vector3(-1.2f, 0.45f, 0f), new Vector3(0.5f, 0.07f, 0.5f), woodMat);
            Box(root, "chair-a-back", new Vector3(-1.42f, 0.75f, 0f), new Vector3(0.07f, 0.6f, 0.5f), woodMat);
            Box(root, "chair-b-seat", new Vector3(1.2f, 0.45f, 0f), new Vector3(0.5f, 0.07f, 0.5f), woodMat);
            Box(root, "chair-b-back", new Vector3(1.42f, 0.75f, 0f), new Vector3(0.07f, 0.6f, 0.5f), woodMat);

            Box(root, "lamp-post", new Vector3(-2.5f, 0.8f, 1.5f), new Vector3(0.07f, 1.6f, 0.07f), woodMat);
            Box(root, "lamp-shade", new Vector3(-2.5f, 1.72f, 1.5f), new Vector3(0.42f, 0.3f, 0.42f), lampMat);

            // Two lights: a soft key from the window side plus a warm bounce, so
            // the room reads as a room from every angle instead of flattening out
            // when the camera turns away from the window.
            var key = new GameObject("key-light");
            key.transform.SetParent(root.transform, false);
            key.transform.localPosition = new Vector3(2.2f, 2.4f, 0.4f);
            key.transform.localRotation = Quaternion.Euler(35f, -110f, 0f);
            var keyL = key.AddComponent<Light>();
            keyL.type = LightType.Directional;
            keyL.color = new Color(1f, 0.96f, 0.88f);
            keyL.intensity = 1.1f;

            var fill = new GameObject("fill-light");
            fill.transform.SetParent(root.transform, false);
            fill.transform.localPosition = new Vector3(-2.5f, 1.8f, 1.5f);
            var fillL = fill.AddComponent<Light>();
            fillL.type = LightType.Point;
            fillL.color = new Color(1f, 0.88f, 0.70f);
            fillL.intensity = 1.4f;
            fillL.range = 7f;

            return root;
        }

        private static GameObject Box(GameObject parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            var col = go.GetComponent<Collider>();
            // Nothing here is ever touched; outside play mode Destroy only marks
            // the object and warns, so tear it down immediately.
            if (col != null)
            {
                if (Application.isPlaying) Object.Destroy(col);
                else Object.DestroyImmediate(col);
            }
            var r = go.GetComponent<Renderer>();
            if (r != null && mat != null) r.sharedMaterial = mat;
            return go;
        }

        private static Material Mat(Color c)
        {
            // Whatever lit shader this project actually has: the built-in
            // pipeline's Standard, or URP/HDRP's Lit. Falling back to an unlit
            // colour is better than a magenta room.
            var shader = Shader.Find("Standard")
                      ?? Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("HDRP/Lit")
                      ?? Shader.Find("Sprites/Default");
            if (shader == null) return null;
            var m = new Material(shader) { name = "lvn-demo-" + ColorUtility.ToHtmlStringRGB(c) };
            m.color = c;
            return m;
        }
    }
}
