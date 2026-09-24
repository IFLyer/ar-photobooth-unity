// BoothDebug.cs — port of window.__arDebug di js/app.js
// API inspeksi runtime untuk debugging & QA: fps, jumlah wajah, item aktif,
// pose mentah, skor ekspresi, toggle item, daftar emitter particle.

using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    public static class BoothDebug
    {
        static BoothApp App => BoothApp.Instance;

        /// <summary>FPS render loop (rolling per detik).</summary>
        public static int GetFps() => App != null ? App.Fps : 0;

        /// <summary>Jumlah wajah yang sedang terdeteksi.</summary>
        public static int GetFaceCount() => App?.Poses?.Count ?? 0;

        /// <summary>ID item yang sedang aktif.</summary>
        public static List<string> GetActiveIds()
        {
            var l = new List<string>();
            if (App?.Manager == null) return l;
            l.AddRange(App.Manager.ActiveIds);
            return l;
        }

        /// <summary>Pose wajah tersmooth terakhir (ruang normalized).</summary>
        public static List<FacePose> GetPoses() => App?.Poses;

        /// <summary>Skor ekspresi terbaru wajah pertama.</summary>
        public static ExpressionScores GetScores() => App?.LastScores;

        /// <summary>Toggle item by id (sama seperti klik thumbnail).</summary>
        public static bool Toggle(string id) => App?.Manager?.Toggle(id) ?? false;

        /// <summary>Jumlah emitter particle hidup.</summary>
        public static int GetEmitterCount() => App?.R2D?.Particles?.Emitters?.Count ?? 0;

        /// <summary>Tracker siap + kamera jalan.</summary>
        public static bool IsRunning => App != null && App.Tracker != null && App.Tracker.Ready
            && App.Cam != null && App.Cam.IsRunning;

        /// <summary>Ringkasan satu baris untuk log.</summary>
        public static string Dump()
        {
            var s = GetScores();
            return $"[arDebug] fps={GetFps()} faces={GetFaceCount()} active=[{string.Join(",", GetActiveIds())}] " +
                   $"emitters={GetEmitterCount()} scores(blink={s?.Blink:F2} mouth={s?.MouthOpen:F2} " +
                   $"smile={s?.Smile:F2} brows={s?.RaiseBrows:F2})";
        }
    }
}
