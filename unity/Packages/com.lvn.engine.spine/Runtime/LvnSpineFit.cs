using Spine.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Spine
{
    /// <summary>
    /// Lives on the spine CONTAINER and applies the manual fit in LateUpdate,
    /// retrying every frame until it succeeds — the ONLY safe time to fit, since
    /// SkeletonGraphic.MeshScale needs a frame or two to settle after build. A
    /// fresh Request() (e.g. a `scale`/`fit` change) re-arms it.
    ///
    /// MUST live in its own file matching the class name: a MonoBehaviour Unity
    /// can't resolve to a MonoScript logs "referenced script (Unknown) missing"
    /// and never runs.
    /// </summary>
    internal sealed class LvnSpineFit : MonoBehaviour
    {
        private RectTransform _container;
        private SkeletonGraphic _g;
        private RawImage _bg;
        private float _scale = 1f;
        private string _mode = "width";

        /// <summary>Подгонка ХОТЯ БЫ РАЗ применилась к живому мешу (MeshScale
        /// устаканился, масштаб выставлен). До этого меш раздут ~в 100×, и
        /// показывать его нельзя — <see cref="LvnSpineFader"/> ждёт этот флаг,
        /// иначе фигура успевала проявиться раздутыми прямоугольниками, пока
        /// подгонка ещё догоняла (гонка «через раз», живой замер 07.09).</summary>
        public bool Fitted { get; private set; }

        public void Setup(SkeletonGraphic g, RawImage bg)
        {
            _g = g;
            _bg = bg;
            _container = transform as RectTransform;
        }

        public void Request(float scale, string mode)
        {
            _scale = scale;
            if (!string.IsNullOrEmpty(mode)) _mode = mode;
            Fitted = false; // новый запрос — прежняя подгонка недействительна
            enabled = true; // keep retrying until TryFit succeeds
        }

        private void LateUpdate()
        {
            if (_g == null) { enabled = false; return; }
            if (LvnSpineBootstrap.TryFit(_container, _g, _bg, _scale, _mode)) { Fitted = true; enabled = false; }
        }
    }
}
