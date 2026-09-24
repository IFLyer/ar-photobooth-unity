// BoothApp.cs — port of js/app.js
// Orchestrator: bootstrap config/kamera/tracker/UI, render loop per frame,
// tracking cadence terpisah, dispatch ekspresi → ARManager, capture flow,
// error handling supaya loop tidak pernah berhenti.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    public class BoothApp : MonoBehaviour
    {
        /// <summary>Clock animasi global — dibaca semua renderer.</summary>
        public static AnimationTicker Ticker { get; private set; }
        public static BoothApp Instance { get; private set; }

        [Header("Wiring (diisi BoothSceneBuilder)")]
        public CameraFeed Cam;
        public FaceTracker Tracker;
        public BoothStage Stage;
        public Renderer2D R2D;
        public Renderer3D R3D;
        public BoothUI UI;
        public CaptureManager Capture;
        public MeshRenderer VideoQuad;

        public ArManager Manager { get; private set; }
        readonly PoseSmoother _smoother = new PoseSmoother();
        readonly ExpressionDetector _detector = new ExpressionDetector();

        List<FacePose> _poses = new List<FacePose>();
        MaterialPropertyBlock _videoMpb;
        bool _started;
        float _videoRot; // videoRotationAngle — landmark di-unrotate sebesar ini

        /// <summary>Landmarks mentah terbaru (dipakai debug).</summary>
        public List<FaceLandmark[]> LastFaces { get; private set; }
        public List<FacePose> Poses => _poses;
        public ExpressionScores LastScores => _detector.Scores;
        public int Fps => _fps;

        void Awake()
        {
            Instance = this;
            // Booth app: tetap render saat window tidak fokus & jangan sleep.
            Application.runInBackground = true;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Ticker = new AnimationTicker();
            Manager = new ArManager(Ticker);
            _videoMpb = new MaterialPropertyBlock();
        }

        IEnumerator Start()
        {
            // 0) Wire renderer ke manager (dibuat di Awake).
            R2D.Init(Manager);
            R3D.Manager = Manager;

            // 1) Config & catalog.
            try { Manager.LoadConfig("assets/ar/config.json"); }
            catch (System.Exception e)
            {
                Debug.LogError("[boot] " + e.Message);
                UI?.ShowStatus("Gagal memuat config AR: " + e.Message);
                yield break;
            }
            UI?.BuildSelector(Manager.Items, Manager.Branding);
            Manager.OnChange += items => { UI?.RefreshActive(Manager); R3D?.Preload(items); };
            UI?.RefreshActive(Manager);

            // 2) Kamera.
            yield return Cam.StartCamera();
            if (Cam.LastError != null || Cam.Texture == null)
            {
                Debug.LogError("[cam] " + Cam.LastError);
                UI?.ShowError("Kamera tidak tersedia: " + (Cam.LastError ?? "unknown"));
                yield break;
            }
            Stage.SetVideoSize(Cam.VideoWidth, Cam.VideoHeight);
            var tex = Cam.Texture;
            _videoMpb.SetTexture("_MainTex", tex);
            // Koreksi orientasi hardware (umum di Android): rotasi quad UV
            // ditangani lewat transform; mirror vertikal via skala y negatif.
            float rot = tex.videoRotationAngle;
            _videoRot = ((rot % 360) + 360) % 360;
            if (Mathf.Approximately(Mathf.Abs(rot), 90f) || Mathf.Approximately(Mathf.Abs(rot), 270f))
                Stage.SetVideoSize(tex.height, tex.width);
            VideoQuad.transform.localRotation = Quaternion.Euler(0, 0, -rot);
            // Quad menutup bidang pandang — skala = lebar/tinggi view;
            // skala y negatif untuk koreksi videoVerticallyMirrored.
            float vh = 2f * BoothStage.CameraDistance *
                Mathf.Tan(BoothStage.Fov * 0.5f * Mathf.Deg2Rad);
            float vw = vh * Stage.Aspect;
            VideoQuad.transform.localScale = new Vector3(
                vw, tex.videoVerticallyMirrored ? -vh : vh, 1);
            VideoQuad.transform.position = new Vector3(0, 0, BoothStage.ZVideo);
            VideoQuad.SetPropertyBlock(_videoMpb);

            // 3) Face tracker.
            yield return Tracker.Init();
            UI?.HideStatus();
            UI?.BindLiveView();
            if (!Tracker.Ready)
            {
                UI?.ShowError("Face tracker gagal diinisialisasi (model tidak ditemukan).");
                yield break;
            }
            _started = true;
        }

        void Update()
        {
            if (!_started) return;
            Ticker.Tick(Time.realtimeSinceStartup * 1000f);

            // Inference di-cadence sendiri (dalam FaceTracker) — render tidak menunggu.
            try { Tracker.ProcessFrame(Cam.Texture); }
            catch (System.Exception e) { Debug.LogError("[track-submit] " + e); }

            List<FaceLandmark[]> faces = null;
            try { faces = Tracker.PumpResults(); }
            catch (System.Exception e) { Debug.LogError("[track-result] " + e); }

            if (faces != null)
            {
                UnrotateLandmarks(faces);
                LastFaces = faces;
                var raw = new List<FacePose>(faces.Count);
                foreach (var f in faces) raw.Add(FacePoseMath.ComputeFacePose(f));
                _poses = _smoother.Update(raw);

                // Ekspresi dari wajah pertama (primary) → trigger item.
                var res = _detector.Update(faces.Count > 0 ? faces[0] : null);
                if (res.HasValue)
                    try { Manager.HandleExpressions(res.Value.scores); }
                    catch (System.Exception e) { Debug.LogError("[expr] " + e); }
            }

            var active = Manager.GetActiveItems();
            try { R2D.Render(_poses, active); }
            catch (System.Exception e) { Debug.LogError("[render2d] " + e); }
            try { R3D.Render(_poses, active); }
            catch (System.Exception e) { Debug.LogError("[render3d] " + e); }

            // Indikator status + FPS rolling per detik (parity updateStatus).
            _frames++;
            float t = Time.realtimeSinceStartup;
            if (t - _fpsT >= 1f)
            {
                _fps = _frames;
                _frames = 0;
                _fpsT = t;
            }
            if (_poses.Count > 0)
            {
                int n = _poses.Count;
                UI?.SetStatus("ok", $"{(n > 1 ? n + " wajah" : "Wajah")} terdeteksi · {_fps} FPS");
            }
            else UI?.SetStatus("warn", "Mencari wajah…");
        }

        int _frames;
        float _fpsT;
        int _fps;

        /// <summary>
        /// MediaPipe melihat frame mentah (terotasi videoRotationAngle derajat CW).
        /// Geser tiap landmark ke koordinat upright supaya anchor sejajar dengan
        /// video yang sudah diputar untuk tampilan. Rotasi image-space (y down).
        /// </summary>
        void UnrotateLandmarks(List<FaceLandmark[]> faces)
        {
            float r = _videoRot;
            if (r < 1f) return;
            foreach (var f in faces)
            {
                for (int i = 0; i < f.Length; i++)
                {
                    float x = f[i].x, y = f[i].y;
                    if (Mathf.Approximately(r, 90f)) f[i] = new FaceLandmark(1f - y, x, f[i].z);
                    else if (Mathf.Approximately(r, 180f)) f[i] = new FaceLandmark(1f - x, 1f - y, f[i].z);
                    else if (Mathf.Approximately(r, 270f)) f[i] = new FaceLandmark(y, 1f - x, f[i].z);
                }
            }
        }

        /// <summary>Tombol shutter → countdown → capture → preview (parity web).</summary>
        public void OnShutter()
        {
            if (!_started || UI == null || Capture == null) return;
            StartCoroutine(UI.CountdownAndCapture(Capture));
        }

        /// <summary>Retake dari preview — kembali ke live.</summary>
        public void OnRetake() => UI?.HidePreview();

        /// <summary>Simpan hasil capture (PNG, nama ar-booth-YYYYMMDD-HHMMSS).</summary>
        public void OnSave(Texture2D photo)
        {
            if (photo == null) return;
            string path = Capture.SavePng(photo);
            UI?.ShowStatus("Foto tersimpan: " + path, autoHide: true);
        }

        /// <summary>Toggle fullscreen (parity fullscreen button di web).</summary>
        public void ToggleFullscreen()
        {
            Screen.fullScreen = !Screen.fullScreen;
        }
    }
}
