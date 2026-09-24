// ExpressionDetector.cs — port of js/expression-detector.js
// Deteksi ekspresi wajah dari landmark MediaPipe:
// blink (EAR), mouth_open (MAR), smile, raise_brows → skor 0..1 + event edge.

using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    /// <summary>Skor ekspresi 0..1 + metrik mentah untuk debug/kalibrasi.</summary>
    public class ExpressionScores
    {
        public float Blink, MouthOpen, Smile, RaiseBrows;
        // Metrik mentah — dipakai debug/kalibrasi threshold.
        public float RawEar, RawMar, RawSmileRatio, RawBrowDist;
        public float this[string name] => name switch
        {
            "blink" => Blink,
            "mouth_open" => MouthOpen,
            "smile" => Smile,
            "raise_brows" => RaiseBrows,
            _ => 0f,
        };
    }

    public class ExpressionEvent
    {
        public string Name;
        public bool Active;
        public float Score;
    }

    public static class ExpressionMath
    {
        // Index landmark MediaPipe Face Mesh untuk metrik ekspresi.
        static readonly int[] LeftEyeH = { 33, 133 };
        static readonly int[] LeftEyeV = { 159, 145 };
        static readonly int[] RightEyeH = { 263, 362 };
        static readonly int[] RightEyeV = { 386, 374 };
        const int UpperLip = 13, LowerLip = 14, MouthL = 78, MouthR = 308;
        const int FaceEdgeL = 234, FaceEdgeR = 454, Forehead = 10, Chin = 152;
        static readonly int[] LeftBrow = { 70, 63, 105, 66, 107 };
        static readonly int[] RightBrow = { 336, 296, 334, 293, 300 };

        // Range kalibrasi raw→skor 0..1 per ekspresi (estimasi empiris).
        const float BlinkClosed = 0.09f, BlinkOpen = 0.3f;      // EAR: kecil = merem
        const float MarClosed = 0.08f, MarOpen = 0.65f;         // MAR: besar = mangap
        const float SmileNeutral = 0.4f, SmileFull = 0.56f;     // lebar mulut / lebar wajah
        const float BrowLiftRange = 0.3f;                       // deviasi dari baseline personal

        static float Dist(FaceLandmark a, FaceLandmark b)
            => Mathf.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

        static FaceLandmark Mid(FaceLandmark a, FaceLandmark b)
            => new FaceLandmark((a.x + b.x) / 2f, (a.y + b.y) / 2f, 0);

        static FaceLandmark Centroid(IList<FaceLandmark> lm, int[] idx)
        {
            float x = 0, y = 0;
            foreach (var i in idx) { x += lm[i].x; y += lm[i].y; }
            return new FaceLandmark(x / idx.Length, y / idx.Length, 0);
        }

        /// <summary>
        /// Hitung skor ekspresi 0..1 dari array 468 landmark (koordinat normalized).
        /// browBase = baseline jarak alis–mata (EMA per wajah). null bila input tidak valid.
        /// </summary>
        public static ExpressionScores ComputeExpressionScores(IList<FaceLandmark> lm, float? browBase)
        {
            if (lm == null || lm.Count < 468) return null;

            float earL = Dist(lm[LeftEyeV[0]], lm[LeftEyeV[1]]) /
                Mathf.Max(Dist(lm[LeftEyeH[0]], lm[LeftEyeH[1]]), 1e-6f);
            float earR = Dist(lm[RightEyeV[0]], lm[RightEyeV[1]]) /
                Mathf.Max(Dist(lm[RightEyeH[0]], lm[RightEyeH[1]]), 1e-6f);
            float ear = (earL + earR) / 2f;
            float blink = AnimEase.Clamp01((BlinkOpen - ear) / (BlinkOpen - BlinkClosed));

            float mar = Dist(lm[UpperLip], lm[LowerLip]) /
                Mathf.Max(Dist(lm[MouthL], lm[MouthR]), 1e-6f);
            float mouthOpen = AnimEase.Clamp01((mar - MarClosed) / (MarOpen - MarClosed));

            float faceW = Mathf.Max(Dist(lm[FaceEdgeL], lm[FaceEdgeR]), 1e-6f);
            float faceH = Mathf.Max(Dist(lm[Forehead], lm[Chin]), 1e-6f);
            float smileRatio = Dist(lm[MouthL], lm[MouthR]) / faceW;
            float smile = AnimEase.Clamp01((smileRatio - SmileNeutral) / (SmileFull - SmileNeutral));

            var browL = Centroid(lm, LeftBrow);
            var browR = Centroid(lm, RightBrow);
            var eyeL = Mid(lm[LeftEyeV[0]], lm[LeftEyeV[1]]);
            var eyeR = Mid(lm[RightEyeV[0]], lm[RightEyeV[1]]);
            float browDist = (Dist(browL, eyeL) + Dist(browR, eyeR)) / 2f / faceH;
            // Skor = deviasi relatif dari baseline personal; ~1 saat alis naik ~30%.
            float raiseBrows = browBase.HasValue && browBase.Value > 0
                ? AnimEase.Clamp01((browDist - browBase.Value) / (browBase.Value * BrowLiftRange))
                : 0;

            return new ExpressionScores
            {
                Blink = blink,
                MouthOpen = mouthOpen,
                Smile = smile,
                RaiseBrows = raiseBrows,
                RawEar = ear,
                RawMar = mar,
                RawSmileRatio = smileRatio,
                RawBrowDist = browDist,
            };
        }
    }

    /// <summary>
    /// Detector stateful: dipanggil tiap frame dari render loop dengan landmark
    /// terbaru. Mengembalikan { scores, events } — events berisi edge transition
    /// pada ambang default. Input landmark yang sama (referensi identik) di-skip.
    /// </summary>
    public class ExpressionDetector
    {
        const float EventThreshold = 0.5f;
        const float Hysteresis = 0.7f;
        const int MissReset = 30; // frame tanpa wajah sebelum baseline alis di-reset
        static readonly string[] ExprNames = { "blink", "mouth_open", "smile", "raise_brows" };

        IList<FaceLandmark> _lastLm;
        float? _browBase;
        int _missed;
        readonly Dictionary<string, bool> _active = new Dictionary<string, bool>
        { { "blink", false }, { "mouth_open", false }, { "smile", false }, { "raise_brows", false } };

        public ExpressionScores Scores { get; private set; } = new ExpressionScores();

        /// <summary>Update dari landmark terbaru; null bila tidak ada input/duplikat.</summary>
        public (ExpressionScores scores, List<ExpressionEvent> events)? Update(IList<FaceLandmark> landmarks)
        {
            if (landmarks == null)
            {
                _lastLm = null;
                // Wajah hilang > ~0.5s: anggap orang baru — reset baseline & edge state.
                if (++_missed > MissReset) Reset();
                return null;
            }
            _missed = 0;
            if (ReferenceEquals(landmarks, _lastLm)) return null;
            _lastLm = landmarks;

            // Baseline alis: EMA lambat (~2 detik) mengejar posisi netral wajah ini.
            var probe = ExpressionMath.ComputeExpressionScores(landmarks, null);
            if (probe == null) return null;
            float bd = probe.RawBrowDist;
            _browBase = _browBase == null ? bd : _browBase.Value * 0.98f + bd * 0.02f;

            var scores = ExpressionMath.ComputeExpressionScores(landmarks, _browBase);
            if (scores == null) return null;
            Scores = scores;

            var events = new List<ExpressionEvent>();
            foreach (var name in ExprNames)
            {
                bool was = _active[name];
                float s = scores[name];
                bool isOn = was ? s >= EventThreshold * Hysteresis : s >= EventThreshold;
                if (isOn != was)
                {
                    _active[name] = isOn;
                    events.Add(new ExpressionEvent { Name = name, Active = isOn, Score = s });
                }
            }
            return (scores, events);
        }

        /// <summary>Reset state supaya edge berikutnya bersih.</summary>
        public void Reset()
        {
            _lastLm = null;
            _browBase = null;
            _missed = 0;
            foreach (var k in ExprNames) _active[k] = false;
            Scores = new ExpressionScores();
        }
    }
}
