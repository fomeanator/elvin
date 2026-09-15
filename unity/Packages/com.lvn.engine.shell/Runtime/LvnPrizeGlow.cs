using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СИЯНИЕ ПРИЗА (Илья 15.09: «рамку горящую в цвет редкости, чтобы лучи
    /// переливались»): за картинкой — веер лучей, который медленно вращается
    /// и переливается яркостью, вокруг картинки — горящая рамка, дышащая в
    /// такт, под всем — мягкое гало. Всё в цвете ступени редкости.
    ///
    /// <para>Рисуется своей геометрией элемента (<c>generateVisualContent</c>),
    /// без материалов и текстур: тот же вид на любом телефоне и никакой
    /// отдельной шейдерной сборки, которую нельзя проверить до APK. Живёт,
    /// пока прикреплён к панели; снятие останавливает тик.</para>
    /// </summary>
    public sealed class LvnPrizeGlow : LvnLiveMesh
    {
        public Color Tint = Color.white;
        /// <summary>Соотношение сторон картинки: рамка обводит её, а не окно.</summary>
        public float Aspect;

        private const int Rays = 20, HaloSteps = 40, CornerSteps = 10;
        private const float Step = 0.016f;
        private float _phase;

        protected override void Tick() => _phase += Step;

        /// <summary>Прямоугольник самой картинки внутри окна: вписана по
        /// меньшей стороне, как её и показывает <c>LvnPicture.Fit</c>.</summary>
        private Rect Picture(Rect r)
        {
            if (Aspect <= 0f) return r;
            float w = r.height * Aspect, h = r.height;
            if (w > r.width) { w = r.width; h = w / Aspect; }
            return new Rect(r.center.x - w / 2f, r.center.y - h / 2f, w, h);
        }

        protected override void Draw(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width <= 2f || r.height <= 2f) return;
            var pic = Picture(r);
            var c = r.center;
            int ringPts = 4 * CornerSteps;
            var mesh = mgc.Allocate(HaloSteps + 1 + Rays * 3 + ringPts * 2, HaloSteps * 3 + Rays * 3 + ringPts * 6);
            ushort v = 0;

            // ГАЛО — мягкий диск за всем, дышит.
            float breath = 0.5f + 0.5f * Mathf.Sin(_phase * 2.4f);
            float haloR = Mathf.Max(pic.width, pic.height) * 0.72f;
            Put(mesh, c, Tint, 0.28f + 0.10f * breath); ushort haloC = v++;
            for (int i = 0; i < HaloSteps; i++)
            {
                float a = i * Mathf.PI * 2f / HaloSteps;
                Put(mesh, c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * haloR, Tint, 0f); v++;
            }
            for (int i = 0; i < HaloSteps; i++)
            {
                mesh.SetNextIndex(haloC);
                mesh.SetNextIndex((ushort)(haloC + 1 + i));
                mesh.SetNextIndex((ushort)(haloC + 1 + (i + 1) % HaloSteps));
            }

            // ЛУЧИ — веер, вращается медленно, каждый луч переливается своим ритмом.
            float reach = Mathf.Sqrt(r.width * r.width + r.height * r.height) * 0.9f;
            float spin = _phase * 0.35f;
            float half = Mathf.PI / Rays * 0.42f;
            for (int i = 0; i < Rays; i++)
            {
                float a = spin + i * Mathf.PI * 2f / Rays;
                float shimmer = 0.5f + 0.5f * Mathf.Sin(_phase * 2.2f + i * 1.7f);
                float alpha = 0.22f + 0.38f * shimmer;
                ushort apex = v;
                Put(mesh, c, Tint, alpha); v++;
                Put(mesh, c + new Vector2(Mathf.Cos(a - half), Mathf.Sin(a - half)) * reach, Tint, 0f); v++;
                Put(mesh, c + new Vector2(Mathf.Cos(a + half), Mathf.Sin(a + half)) * reach, Tint, 0f); v++;
                mesh.SetNextIndex(apex); mesh.SetNextIndex((ushort)(apex + 1)); mesh.SetNextIndex((ushort)(apex + 2));
            }

            // РАМКА — горящий ободок по краю картинки, гаснущий наружу.
            float rad = Mathf.Min(pic.width, pic.height) * 0.06f;
            float glow = Mathf.Min(pic.width, pic.height) * 0.14f;
            float burn = 0.62f + 0.30f * breath;
            ushort ring = v;
            for (int k = 0; k < 4; k++)
            {
                // Углы по часовой: правый верхний, правый нижний, левый нижний, левый верхний.
                var corner = k == 0 ? new Vector2(pic.xMax - rad, pic.yMin + rad)
                    : k == 1 ? new Vector2(pic.xMax - rad, pic.yMax - rad)
                    : k == 2 ? new Vector2(pic.xMin + rad, pic.yMax - rad)
                    : new Vector2(pic.xMin + rad, pic.yMin + rad);
                for (int i = 0; i < CornerSteps; i++)
                {
                    float a = (-90f + k * 90f + i * 90f / CornerSteps) * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    Put(mesh, corner + dir * rad, Tint, burn); v++;
                    Put(mesh, corner + dir * (rad + glow), Tint, 0f); v++;
                }
            }
            for (int i = 0; i < ringPts; i++)
            {
                int j = (i + 1) % ringPts;
                ushort inI = (ushort)(ring + i * 2), outI = (ushort)(ring + i * 2 + 1);
                ushort inJ = (ushort)(ring + j * 2), outJ = (ushort)(ring + j * 2 + 1);
                mesh.SetNextIndex(inI); mesh.SetNextIndex(outI); mesh.SetNextIndex(outJ);
                mesh.SetNextIndex(inI); mesh.SetNextIndex(outJ); mesh.SetNextIndex(inJ);
            }
        }

        private static void Put(MeshWriteData mesh, Vector2 at, Color tint, float alpha)
        {
            var color = tint; color.a = alpha;
            mesh.SetNextVertex(new Vertex { position = new Vector3(at.x, at.y, Vertex.nearZ), tint = color });
        }
    }
}
