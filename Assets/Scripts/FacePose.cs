// FacePose.cs — port of js/face-tracker.js (bagian pose):
// computeFacePose (anchor, ukuran wajah, roll/yaw/pitch) + PoseSmoother (EMA multi-face).
// FaceTracker MediaPipe ada di FaceTracker.cs — file ini murni matematika.

using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    /// <summary>Satu landmark wajah dalam koordinat normalized (0..1, y ke bawah).</summary>
    public struct FaceLandmark
    {
        public float x, y, z;
        public FaceLandmark(float x, float y, float z = 0f) { this.x = x; this.y = y; this.z = z; }
    }

    /// <summary>
    /// Hasil estimasi pose satu wajah — dipakai renderer 2D & 3D.
    /// Semua nilai di ruang kamera asli — mirroring ditangani di layer tampilan.
    /// </summary>
    public class FacePose
    {
        public Dictionary<string, FaceLandmark> Anchors = new Dictionary<string, FaceLandmark>();
        public Rect Bbox; // minX,minY normalized → gunakan xMin/yMin/xMax/yMax via min/max
        public float MinX, MinY, MaxX, MaxY;
        public float FaceWidth, FaceHeight, EyeDist;
        public float Roll, Yaw, Pitch;
    }

    public static class FacePoseMath
    {
        // Index landmark MediaPipe Face Mesh yang dipakai sebagai anchor & metrik.
        public const int NoseTip = 1;
        public const int NoseBridge = 6;
        public const int Forehead = 10;
        public const int Chin = 152;
        public const int LeftEyeOuter = 33;
        public const int RightEyeOuter = 263;
        public const int LeftFaceEdge = 234;
        public const int RightFaceEdge = 454;
        public const int UpperLip = 13;
        public const int LowerLip = 14;

        // Jumlah wajah maksimum yang dilacak sekaligus (foto grup sampai 5 orang).
        public const int MaxFaces = 5;

        static FaceLandmark Mid(FaceLandmark a, FaceLandmark b)
            => new FaceLandmark((a.x + b.x) / 2f, (a.y + b.y) / 2f, (a.z + b.z) / 2f);

        static float Dist(FaceLandmark a, FaceLandmark b)
            => Mathf.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

        /// <summary>
        /// Hitung pose wajah dari array 468 landmark (koordinat normalized 0..1, y ke bawah).
        /// Mengembalikan anchor point, ukuran wajah, dan estimasi roll/yaw/pitch.
        /// </summary>
        public static FacePose ComputeFacePose(IList<FaceLandmark> lm)
        {
            var leftEye = lm[LeftEyeOuter];
            var rightEye = lm[RightEyeOuter];
            var nose = lm[NoseTip];
            var forehead = lm[Forehead];
            var chin = lm[Chin];

            var eyesMid = Mid(leftEye, rightEye);
            float eyeDist = Dist(leftEye, rightEye);
            float faceWidth = Dist(lm[LeftFaceEdge], lm[RightFaceEdge]);
            float faceHeight = Dist(forehead, chin);

            // Roll: kemiringan garis mata (koordinat gambar, y ke bawah)
            float roll = Mathf.Atan2(rightEye.y - leftEye.y, rightEye.x - leftEye.x);

            // Yaw: pergeseran hidung relatif ke titik tengah mata, dinormalisasi jarak mata.
            float yaw = Mathf.Clamp(((nose.x - eyesMid.x) / eyeDist) * 1.6f, -0.9f, 0.9f);

            // Pitch: posisi vertikal hidung relatif rentang dahi–dagu terhadap baseline.
            float pitchRatio = (nose.y - eyesMid.y) / (faceHeight != 0 ? faceHeight : 1f);
            float pitch = Mathf.Clamp((pitchRatio - 0.42f) * 3.2f, -0.7f, 0.7f);

            // Bounding box seluruh landmark untuk anchor "full_face".
            float minX = 1, minY = 1, maxX = 0, maxY = 0;
            foreach (var p in lm)
            {
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
            }

            var pose = new FacePose
            {
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                FaceWidth = faceWidth,
                FaceHeight = faceHeight,
                EyeDist = eyeDist,
                Roll = roll,
                Yaw = yaw,
                Pitch = pitch,
            };
            pose.Anchors["forehead"] = forehead;
            pose.Anchors["eyes"] = eyesMid;
            pose.Anchors["left_eye"] = leftEye;
            pose.Anchors["right_eye"] = rightEye;
            pose.Anchors["nose"] = nose;
            pose.Anchors["nose_bridge"] = lm[NoseBridge];
            pose.Anchors["mouth"] = Mid(lm[UpperLip], lm[LowerLip]);
            pose.Anchors["chin"] = chin;
            pose.Anchors["full_face"] = new FaceLandmark((minX + maxX) / 2f, (minY + maxY) / 2f, 0);
            return pose;
        }
    }

    /// <summary>
    /// Peredam jitter pose wajah (BUG-005): exponential moving average — pose
    /// tersmooth = lerp(pose tersmooth sebelumnya, pose baru, alpha). Panggil
    /// Update() SEKALI per hasil tracking baru (bukan per render frame).
    ///
    /// Multi-face: MediaPipe tidak menjamin urutan faceLandmarks stabil antar
    /// frame, jadi pose baru dicocokkan ke pose sebelumnya berdasarkan jarak
    /// pusat wajah (bukan index). Wajah baru tanpa pasangan langsung snap —
    /// smoothing mulai bekerja di frame berikutnya. Input kosong mereset state.
    /// </summary>
    public class PoseSmoother
    {
        const float SmoothAlpha = 0.3f;  // porsi frame baru — makin kecil makin halus tapi makin lag
        const float MatchMinDist = 0.15f; // jarak pusat wajah maksimal untuk dianggap wajah yang sama

        readonly float _alpha;
        List<FacePose> _prev = new List<FacePose>();

        public PoseSmoother(float alpha = SmoothAlpha) { _alpha = alpha; }

        /// <summary>Terima array pose mentah, kembalikan array pose tersmooth.</summary>
        public List<FacePose> Update(List<FacePose> poses)
        {
            if (poses == null || poses.Count == 0)
            {
                _prev = new List<FacePose>();
                return new List<FacePose>();
            }
            var used = new HashSet<int>();
            var outp = new List<FacePose>(poses.Count);
            foreach (var pose in poses)
            {
                int prev = Match(pose, used);
                if (prev < 0) { outp.Add(pose); continue; }
                used.Add(prev);
                outp.Add(LerpPose(_prev[prev], pose, _alpha));
            }
            _prev = outp;
            return outp;
        }

        /// <summary>Index pose sebelumnya terdekat (dari pusat wajah); -1 bila tak ada yg cocok.</summary>
        int Match(FacePose pose, HashSet<int> used)
        {
            var c = pose.Anchors["full_face"];
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < _prev.Count; i++)
            {
                if (used.Contains(i)) continue;
                var p = _prev[i].Anchors["full_face"];
                float dx = p.x - c.x, dy = p.y - c.y;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d < bestD) { bestD = d; best = i; }
            }
            // Batas jarak diskalakan ukuran wajah — wajah besar bergerak lebih jauh.
            return bestD <= Mathf.Max(MatchMinDist, pose.FaceWidth) ? best : -1;
        }

        /// <summary>Hapus state — pose berikutnya langsung snap tanpa blend.</summary>
        public void Reset() => _prev = new List<FacePose>();

        static float LerpAngle(float a, float b, float t)
        {
            float d = b - a;
            if (d > Mathf.PI) d -= Mathf.PI * 2f;
            else if (d < -Mathf.PI) d += Mathf.PI * 2f;
            return a + d * t;
        }

        static FaceLandmark LerpPt(FaceLandmark a, FaceLandmark b, float t)
            => new FaceLandmark(
                Mathf.Lerp(a.x, b.x, t),
                Mathf.Lerp(a.y, b.y, t),
                Mathf.Lerp(a.z, b.z, t));

        /// <summary>Blend seluruh field pose: anchors, bbox, dimensi wajah, dan sudut.</summary>
        static FacePose LerpPose(FacePose prev, FacePose next, float t)
        {
            var outp = new FacePose();
            foreach (var kv in next.Anchors)
            {
                FaceLandmark a;
                outp.Anchors[kv.Key] = LerpPt(
                    prev.Anchors.TryGetValue(kv.Key, out a) ? a : kv.Value, kv.Value, t);
            }
            outp.MinX = Mathf.Lerp(prev.MinX, next.MinX, t);
            outp.MinY = Mathf.Lerp(prev.MinY, next.MinY, t);
            outp.MaxX = Mathf.Lerp(prev.MaxX, next.MaxX, t);
            outp.MaxY = Mathf.Lerp(prev.MaxY, next.MaxY, t);
            outp.FaceWidth = Mathf.Lerp(prev.FaceWidth, next.FaceWidth, t);
            outp.FaceHeight = Mathf.Lerp(prev.FaceHeight, next.FaceHeight, t);
            outp.EyeDist = Mathf.Lerp(prev.EyeDist, next.EyeDist, t);
            outp.Roll = LerpAngle(prev.Roll, next.Roll, t);
            outp.Yaw = Mathf.Lerp(prev.Yaw, next.Yaw, t);
            outp.Pitch = Mathf.Lerp(prev.Pitch, next.Pitch, t);
            return outp;
        }
    }
}
