using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>ТРЕУГОЛЬНИК-УКАЗАТЕЛЬ — залитая стрелка вверх или вниз своей
    /// геометрией: указатель ленты круток (Илья 15.09: «вместо линии стрелочки
    /// сверху и снизу — так моднее»). Без картинки и шрифта: острый на любом
    /// экране.</summary>
    public sealed class LvnTriangle : VisualElement
    {
        public enum Point { Up, Down }
        public Color Tint = Color.white;
        public Point Points = Point.Down;

        public LvnTriangle()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        private void Draw(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width <= 1f || r.height <= 1f) return;
            var mesh = mgc.Allocate(3, 3);
            if (Points == Point.Down)
            {
                Put(mesh, r.xMin, r.yMin); Put(mesh, r.xMax, r.yMin); Put(mesh, r.center.x, r.yMax);
            }
            else
            {
                Put(mesh, r.center.x, r.yMin); Put(mesh, r.xMax, r.yMax); Put(mesh, r.xMin, r.yMax);
            }
            mesh.SetNextIndex(0); mesh.SetNextIndex(1); mesh.SetNextIndex(2);
        }

        private void Put(MeshWriteData mesh, float x, float y)
            => mesh.SetNextVertex(new Vertex { position = new Vector3(x, y, Vertex.nearZ), tint = Tint });
    }
}
