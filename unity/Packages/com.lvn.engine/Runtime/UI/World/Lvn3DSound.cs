using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Звук, привязанный к телу сцены: костёр трещит там, где горит, вода
    /// шумит за спиной, дверь скрипит слева.
    ///
    /// <para>Пространственный звук — половина присутствия. Плоское «эмбиент на
    /// всю сцену» звучит как радио в комнате: оно не говорит, ГДЕ ты стоишь и
    /// куда повернулся, а осмотр сцены свайпом при этом обещает именно это.</para>
    ///
    /// <para>Тонкость, из-за которой наивная реализация молчит: набор стоит в
    /// десяти километрах от начала координат (см. <c>Far</c>), чтобы главная
    /// камера его не поймала, а слушатель живёт при ней. Прямая привязка
    /// источника к телу дала бы расстояние в десять тысяч метров — то есть
    /// тишину. Поэтому источник ставится РЯДОМ СО СЛУШАТЕЛЕМ, сохраняя
    /// смещение тела относительно камеры набора: слышно так, будто игрок и
    /// есть эта камера.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DSound : MonoBehaviour
    {
        private Transform _body;      // тело в наборе
        private Camera _setCam;       // камера набора — «уши» игрока
        private AudioSource _src;

        public static Lvn3DSound Ensure(GameObject host, Transform body, Camera setCam)
        {
            var s = host.GetComponent<Lvn3DSound>() ?? host.AddComponent<Lvn3DSound>();
            s._body = body;
            s._setCam = setCam;
            if (s._src == null)
            {
                s._src = host.AddComponent<AudioSource>();
                s._src.spatialBlend = 1f;              // полностью пространственный
                s._src.rolloffMode = AudioRolloffMode.Linear;
                s._src.loop = true;
                s._src.playOnAwake = false;
                s._src.dopplerLevel = 0f;              // сцена не движется физически
            }
            return s;
        }

        public void Play(AudioClip clip, float volume, float range)
        {
            if (_src == null || clip == null) return;
            _src.clip = clip;
            _src.volume = Mathf.Clamp01(volume);
            _src.maxDistance = Mathf.Max(1f, range);
            _src.minDistance = Mathf.Min(1f, _src.maxDistance * 0.25f);
            if (!_src.isPlaying) _src.Play();
            enabled = true;
        }

        public void Stop()
        {
            if (_src != null) _src.Stop();
            enabled = false;
        }

        private void LateUpdate()
        {
            if (_body == null || _setCam == null || _src == null) { enabled = false; return; }
            var listener = Object.FindAnyObjectByType<AudioListener>();
            if (listener == null) return;
            // Смещение тела относительно камеры набора переносим к слушателю:
            // направление и расстояние сохраняются, а километры до набора — нет.
            var offset = _body.position - _setCam.transform.position;
            transform.position = listener.transform.position + offset;
        }
    }
}
