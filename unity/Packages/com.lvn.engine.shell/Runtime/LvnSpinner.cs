using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>КРУТИЛКА — дуга кольца в три четверти, гаснущая к хвосту,
    /// вращается, пока элемент прикреплён. Своей геометрией: без картинок,
    /// без покраски сторон рамки; цвет — тот, что дал хозяин.</summary>
    public sealed class LvnSpinner : LvnLiveMesh
    {
        public Color Tint = Color.white;
        private const int Steps = 36;
        private const float Sweep = Mathf.PI * 1.5f;
        private float _angle;

        protected override void Tick() => _angle += 0.16f;

        protected override void Draw(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width <= 2f || r.height <= 2f) return;
            var c = r.center;
            float outer = Mathf.Min(r.width, r.height) * 0.5f, inner = outer * 0.72f;
            var mesh = mgc.Allocate((Steps + 1) * 2, Steps * 6);
            for (int i = 0; i <= Steps; i++)
            {
                float t = (float)i / Steps;
                float a = _angle + t * Sweep;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var color = Tint; color.a = Mathf.Lerp(0.08f, 1f, t);
                mesh.SetNextVertex(new Vertex { position = new Vector3(c.x + dir.x * inner, c.y + dir.y * inner, Vertex.nearZ), tint = color });
                mesh.SetNextVertex(new Vertex { position = new Vector3(c.x + dir.x * outer, c.y + dir.y * outer, Vertex.nearZ), tint = color });
            }
            for (int i = 0; i < Steps; i++)
            {
                ushort inA = (ushort)(i * 2), outA = (ushort)(i * 2 + 1), inB = (ushort)(i * 2 + 2), outB = (ushort)(i * 2 + 3);
                mesh.SetNextIndex(inA); mesh.SetNextIndex(outA); mesh.SetNextIndex(outB);
                mesh.SetNextIndex(inA); mesh.SetNextIndex(outB); mesh.SetNextIndex(inB);
            }
        }
    }
}
