// AnimationTicker.cs — port of js/animation-ticker.js
// Clock animasi global: deltaTime & elapsedTime per frame + easing transisi.

using UnityEngine;

namespace ArBooth
{
    /// <summary>Durasi transisi item (detik): pasang = scale-bounce + fade-in, lepas = fade-out.</summary>
    public static class Transition
    {
        public const float Attach = 0.38f;
        public const float Detach = 0.22f;
    }

    public static class AnimEase
    {
        public static float Clamp01(float t) => Mathf.Clamp01(t);

        /// <summary>easeOutBack: overshoot halus di akhir — dipakai untuk scale-bounce saat item dipasang.</summary>
        public static float EaseOutBack(float t)
        {
            const float c = 1.70158f;
            float u = Clamp01(t) - 1f;
            return 1f + (c + 1f) * u * u * u + c * u * u;
        }
    }

    /// <summary>
    /// Satu sumber waktu untuk semua animasi (sprite sheet, Animation, transisi).
    /// Tick() dipanggil sekali per frame di loop utama sebelum renderer jalan, supaya
    /// semua animasi membaca deltaTime/elapsedTime yang konsisten dalam satu frame.
    /// </summary>
    public class AnimationTicker
    {
        public float ElapsedTime { get; private set; } // detik sejak tick pertama
        public float DeltaTime { get; private set; }   // detik sejak frame sebelumnya (di-clamp maks 0.1)
        private float? _last;

        /// <summary>Majukan clock ke timestamp frame ini; kembalikan deltaTime (detik).</summary>
        public float Tick(float nowMs)
        {
            if (_last == null)
            {
                _last = nowMs;
                DeltaTime = 0;
                return 0;
            }
            // Clamp supaya jeda besar (alt-tab / GC) tidak "melompatkan" animasi.
            DeltaTime = Mathf.Min((nowMs - _last.Value) / 1000f, 0.1f);
            _last = nowMs;
            ElapsedTime += DeltaTime;
            return DeltaTime;
        }
    }
}
