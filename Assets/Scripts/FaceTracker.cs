// FaceTracker.cs — port of js/face-tracker.js (bagian MediaPipe).
// MediaPipe FaceLandmarker (Tasks API) — landmark normalized identik dengan
// Face Mesh web (478 titik; 468 pertama = indeks yang sama).
// processFrame() diganti cadence Update ~30 FPS inference + callback async.

using System.Collections;
using System.Collections.Generic;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Experimental;
using UnityEngine;

namespace ArBooth
{
    /// <summary>
    /// Wrapper FaceLandmarker. Kirim frame via Update() dengan cadence sendiri
    /// (terpisah dari render loop); hasil landmark diteruskan ke OnResults
    /// sebagai List&lt;IList&lt;FaceLandmark&gt;&gt; (array per wajah, maks MaxFaces).
    /// </summary>
    public class FaceTracker : MonoBehaviour
    {
        [Tooltip("Model face landmarker (.bytes di Resources/mediapipe)")]
        public string ModelResource = "mediapipe/face_landmarker";
        public float MinDetectionConfidence = 0.5f;
        public float MinPresenceConfidence = 0.5f;
        public float MinTrackingConfidence = 0.5f;

        /// <summary>Callback hasil tracking — dipanggil di main thread dari Update.</summary>
        public System.Action<List<FaceLandmark[]>> OnResults;

        FaceLandmarker _landmarker;
        TextureFramePool _framePool;
        bool _busy;
        float _lastSubmit;
        long _lastTimestamp;
        List<FaceLandmark[]> _pending;
        readonly object _lock = new object();
        bool _failed;

        const float TrackIntervalMs = 33f;   // target ~30 FPS inference
        const float TrackBackoffMs = 500f;   // jeda ekstra saat inference error

        public bool Ready { get; private set; }

        /// <summary>Init FaceLandmarker dari model di Resources (dibundel dalam build).</summary>
        public IEnumerator Init()
        {
            var model = Resources.Load<TextAsset>(ModelResource);
            if (model == null || model.bytes == null || model.bytes.Length == 0)
            {
                Debug.LogError("[FaceTracker] Model tidak ditemukan: Resources/" + ModelResource);
                _failed = true;
                yield break;
            }
            var baseOptions = new BaseOptions(
                delegateCase: BaseOptions.Delegate.CPU,
                modelAssetBuffer: model.bytes);
            var options = new FaceLandmarkerOptions(
                baseOptions,
                runningMode: RunningMode.LIVE_STREAM,
                numFaces: FacePoseMath.MaxFaces,
                minFaceDetectionConfidence: MinDetectionConfidence,
                minFacePresenceConfidence: MinPresenceConfidence,
                minTrackingConfidence: MinTrackingConfidence,
                outputFaceBlendshapes: false,
                outputFaceTransformationMatrixes: false,
                resultCallback: OnLandmarkerResult);
            _landmarker = FaceLandmarker.CreateFromOptions(options);
            Ready = true;
        }

        /// <summary>Buat frame pool setelah resolusi kamera diketahui.</summary>
        public void EnsurePool(int w, int h)
        {
            if (_framePool == null || _framePool.textureWidth != w || _framePool.textureHeight != h)
            {
                _framePool?.Dispose();
                _framePool = new TextureFramePool(w, h, TextureFormat.RGBA32, 10);
            }
        }

        /// <summary>
        /// Kirim satu frame untuk diproses bila cadence tercapai & tidak busy.
        /// Frame di-skip bila proses sebelumnya belum selesai.
        /// </summary>
        public void ProcessFrame(WebCamTexture webcam)
        {
            if (!Ready || _failed || _landmarker == null || webcam == null || !webcam.isPlaying) return;
            float now = Time.realtimeSinceStartup * 1000f;
            if (now - _lastSubmit < TrackIntervalMs) return;
            if (_busy) return;
            EnsurePool(webcam.width, webcam.height);
            if (_framePool == null || !_framePool.TryGetTextureFrame(out var frame)) return;

            _lastSubmit = now;
            _busy = true;
            try
            {
                // Tidak di-flip — landmark di ruang kamera asli (mirror di tampilan),
                // sama seperti video element mentah di web.
                frame.ReadTextureOnCPU(webcam, false, false);
                var image = frame.BuildCPUImage();
                long ts = (long)now;
                if (ts <= _lastTimestamp) ts = _lastTimestamp + 1;
                _lastTimestamp = ts;
                _landmarker.DetectAsync(image, ts);
            }
            catch (System.Exception err)
            {
                Debug.LogError("[track] " + err);
                _lastSubmit = now + TrackBackoffMs;
            }
            finally
            {
                _busy = false;
                frame.Release();
            }
        }

        /// <summary>Hasil dari LIVE_STREAM callback — bisa di thread worker.</summary>
        void OnLandmarkerResult(FaceLandmarkerResult result, Mediapipe.Image image, long timestamp)
        {
            var faces = new List<FaceLandmark[]>();
            if (result.faceLandmarks != null)
            {
                foreach (var nl in result.faceLandmarks)
                {
                    var lms = nl.landmarks;
                    if (lms == null || lms.Count < 468) continue;
                    var arr = new FaceLandmark[lms.Count];
                    for (int i = 0; i < lms.Count; i++)
                        arr[i] = new FaceLandmark(lms[i].x, lms[i].y, lms[i].z);
                    faces.Add(arr);
                    if (faces.Count >= FacePoseMath.MaxFaces) break;
                }
            }
            lock (_lock) { _pending = faces; }
        }

        /// <summary>Update: submit frame + kirim hasil terbaru ke main thread.</summary>
        public List<FaceLandmark[]> PumpResults()
        {
            List<FaceLandmark[]> faces = null;
            lock (_lock)
            {
                if (_pending != null) { faces = _pending; _pending = null; }
            }
            return faces;
        }

        void OnDestroy()
        {
            _landmarker?.Close();
            _framePool?.Dispose();
        }
    }
}
