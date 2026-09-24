// BoothPortTests.cs — EditMode tests untuk logika port (parity perilaku web).
// Tidak butuh kamera/MediaPipe — semua input disintesis.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using ArBooth;

namespace ArBooth.Tests
{
    /// <summary>Landmark sintetis: 468 titik — wajah frontal di tengah frame.</summary>
    static class FakeFace
    {
        public static ArBooth.FaceLandmark[] Make(float cx = 0.5f, float cy = 0.5f,
            float w = 0.2f, float h = 0.25f, float roll = 0f)
        {
            var lm = new ArBooth.FaceLandmark[478];
            for (int i = 0; i < lm.Length; i++)
                lm[i] = new ArBooth.FaceLandmark(cx, cy, 0);
            // Mata kiri/kanan (roll memutar garis mata).
            float cos = Mathf.Cos(roll), sin = Mathf.Sin(roll);
            float lx = cx - w * 0.35f * cos, ly = cy - h * 0.2f + (-w * 0.35f) * sin;
            float rx = cx + w * 0.35f * cos, ry = cy - h * 0.2f + (w * 0.35f) * sin;
            lm[FacePoseMath.LeftEyeOuter] = new ArBooth.FaceLandmark(lx, ly, 0);
            lm[FacePoseMath.RightEyeOuter] = new ArBooth.FaceLandmark(rx, ry, 0);
            // Sudut dalam mata + kelopak atas/bawah (EAR ~0.2 — mata terbuka).
            float eyeW = w * 0.14f, eyeH = eyeW * 0.2f;
            lm[133] = new ArBooth.FaceLandmark(lx + eyeW, ly, 0);
            lm[159] = new ArBooth.FaceLandmark(lx + eyeW / 2f, ly - eyeH, 0);
            lm[145] = new ArBooth.FaceLandmark(lx + eyeW / 2f, ly + eyeH, 0);
            lm[362] = new ArBooth.FaceLandmark(rx - eyeW, ry, 0);
            lm[386] = new ArBooth.FaceLandmark(rx - eyeW / 2f, ry - eyeH, 0);
            lm[374] = new ArBooth.FaceLandmark(rx - eyeW / 2f, ry + eyeH, 0);
            lm[FacePoseMath.NoseTip] = new ArBooth.FaceLandmark(cx, cy + h * 0.05f, 0);
            lm[FacePoseMath.NoseBridge] = new ArBooth.FaceLandmark(cx, cy - h * 0.1f, 0);
            lm[FacePoseMath.Forehead] = new ArBooth.FaceLandmark(cx, cy - h * 0.5f, 0);
            lm[FacePoseMath.Chin] = new ArBooth.FaceLandmark(cx, cy + h * 0.5f, 0);
            lm[FacePoseMath.LeftFaceEdge] = new ArBooth.FaceLandmark(cx - w / 2f, cy, 0);
            lm[FacePoseMath.RightFaceEdge] = new ArBooth.FaceLandmark(cx + w / 2f, cy, 0);
            lm[FacePoseMath.UpperLip] = new ArBooth.FaceLandmark(cx, cy + h * 0.3f, 0);
            lm[FacePoseMath.LowerLip] = new ArBooth.FaceLandmark(cx, cy + h * 0.36f, 0);
            // Sudut mulut kiri/kanan (lebar mulut ~ setengah wajah → smile rendah).
            lm[78] = new ArBooth.FaceLandmark(cx - w * 0.2f, cy + h * 0.33f, 0);
            lm[308] = new ArBooth.FaceLandmark(cx + w * 0.2f, cy + h * 0.33f, 0);
            return lm;
        }
    }

    public class FacePoseTests
    {
        [Test]
        public void ComputePose_AnchorsLengkap()
        {
            var pose = FacePoseMath.ComputeFacePose(FakeFace.Make());
            string[] need = { "forehead", "eyes", "left_eye", "right_eye",
                "nose", "nose_bridge", "mouth", "chin", "full_face" };
            foreach (var a in need)
                Assert.IsTrue(pose.Anchors.ContainsKey(a), "anchor hilang: " + a);
            Assert.AreEqual(0.5f, pose.Anchors["full_face"].x, 1e-4);
            Assert.Greater(pose.FaceWidth, 0);
            Assert.Greater(pose.FaceHeight, 0);
        }

        [Test]
        public void ComputePose_RollMengikutiGarisMata()
        {
            var pose = FacePoseMath.ComputeFacePose(FakeFace.Make(roll: 0.2f));
            Assert.AreEqual(0.2f, pose.Roll, 0.05f);
        }

        [Test]
        public void ComputePose_YawNetralSaatHidungDiTengah()
        {
            var pose = FacePoseMath.ComputeFacePose(FakeFace.Make());
            Assert.AreEqual(0f, pose.Yaw, 1e-4);
        }
    }

    public class PoseSmootherTests
    {
        [Test]
        public void Smoother_SnapPadaWajahBaru_LaluBlend()
        {
            var sm = new PoseSmoother(0.3f);
            var a = sm.Update(new List<FacePose> { FacePoseMath.ComputeFacePose(FakeFace.Make(0.5f)) });
            Assert.AreEqual(0.5f, a[0].Anchors["full_face"].x, 1e-3);
            // Wajah bergeser sedikit → hasil blend (bukan snap).
            var b = sm.Update(new List<FacePose> { FacePoseMath.ComputeFacePose(FakeFace.Make(0.54f)) });
            Assert.Greater(b[0].Anchors["full_face"].x, 0.5f);
            Assert.Less(b[0].Anchors["full_face"].x, 0.54f);
        }

        [Test]
        public void Smoother_InputKosong_ResetState()
        {
            var sm = new PoseSmoother(0.3f);
            sm.Update(new List<FacePose> { FacePoseMath.ComputeFacePose(FakeFace.Make(0.5f)) });
            var none = sm.Update(new List<FacePose>());
            Assert.AreEqual(0, none.Count);
            // Setelah reset, wajah berikutnya snap penuh lagi.
            var c = sm.Update(new List<FacePose> { FacePoseMath.ComputeFacePose(FakeFace.Make(0.9f)) });
            Assert.AreEqual(0.9f, c[0].Anchors["full_face"].x, 1e-3);
        }

        [Test]
        public void Smoother_MultiFace_MatchByCenter()
        {
            var sm = new PoseSmoother(0.3f);
            var p1 = FacePoseMath.ComputeFacePose(FakeFace.Make(0.3f));
            var p2 = FacePoseMath.ComputeFacePose(FakeFace.Make(0.7f));
            sm.Update(new List<FacePose> { p1, p2 });
            // Urutan terbalik — matcher harus tetap menemukan pasangan yang benar.
            var out2 = sm.Update(new List<FacePose>
            {
                FacePoseMath.ComputeFacePose(FakeFace.Make(0.72f)),
                FacePoseMath.ComputeFacePose(FakeFace.Make(0.32f)),
            });
            Assert.AreEqual(2, out2.Count);
            Assert.Greater(out2[0].Anchors["full_face"].x, 0.7f);
            Assert.Greater(out2[1].Anchors["full_face"].x, 0.3f);
        }
    }

    public class ExpressionTests
    {
        [Test]
        public void Scores_WajahNetral_SkorRendah()
        {
            var s = ExpressionMath.ComputeExpressionScores(FakeFace.Make(), null);
            Assert.IsNotNull(s);
            Assert.Less(s.Blink, 0.5f);
            Assert.Less(s.MouthOpen, 0.5f);
            Assert.Less(s.Smile, 0.5f);
        }

        [Test]
        public void Scores_MulutMangap_MouthOpenTinggi()
        {
            var lm = FakeFace.Make();
            // Buka mulut lebar: bibir atas & bawah berjauhan.
            lm[FacePoseMath.UpperLip] = new ArBooth.FaceLandmark(0.5f, 0.55f, 0);
            lm[FacePoseMath.LowerLip] = new ArBooth.FaceLandmark(0.5f, 0.7f, 0);
            var s = ExpressionMath.ComputeExpressionScores(lm, null);
            Assert.Greater(s.MouthOpen, 0.5f);
        }

        [Test]
        public void Detector_EdgeBlink_DanReset()
        {
            var det = new ExpressionDetector();
            var open = FakeFace.Make();
            // Frame baseline dulu.
            det.Update(open);
            // Kedipkan: jarak vertikal mata → 0 (EAR kecil → blink tinggi).
            var blink = FakeFace.Make();
            blink[159] = blink[145]; // left eye vert close (159/145 dipakai detector)
            blink[386] = blink[374]; // right eye vert close
            var res = det.Update(blink);
            Assert.IsTrue(res.HasValue);
            Assert.IsTrue(res.Value.events.Exists(e => e.Name == "blink" && e.Active),
                "edge blink naik tidak terdeteksi");
        }
    }

    public class ParticleTests
    {
        [Test]
        public void Presets_CountClampMaks100()
        {
            var cfg = ParticlePresets.Resolve(new ParticleJson { type = "confetti", count = 500 });
            Assert.LessOrEqual(cfg.Count, ParticlePresets.MaxParticles);
        }

        [Test]
        public void Presets_TipeTakDikenal_FallbackConfetti()
        {
            var cfg = ParticlePresets.Resolve(new ParticleJson { type = "ngawur" });
            Assert.AreEqual("confetti", cfg.Type);
        }

        [Test]
        public void Presets_Src_CustomJadiImage()
        {
            var cfg = ParticlePresets.Resolve(new ParticleJson { type = "custom", src = "assets/ar/2d/x.png" });
            Assert.AreEqual("image", cfg.Shape);
            Assert.AreEqual("assets/ar/2d/x.png", cfg.Src);
        }

        [Test]
        public void Emitter_Spawn_TidakMelebihiCount()
        {
            var e = new Emitter(ParticlePresets.Resolve(new ParticleJson { type = "confetti", count = 10 }));
            e.X = 100; e.Y = 100; e.HasPos = true;
            e.Spawn(50, 300f, false);
            Assert.LessOrEqual(e.Parts.Count, 10);
        }

        [Test]
        public void Emitter_ParticleMati_KembaliKePool()
        {
            var e = new Emitter(ParticlePresets.Resolve(new ParticleJson
                { type = "confetti", count = 5, lifetime = 0.1f }));
            e.X = 0; e.Y = 0; e.HasPos = true;
            e.Spawn(5, 300f, false);
            e.Update(0.5f, 300f); // lewati ttl
            Assert.AreEqual(0, e.Parts.Count);
            Assert.AreEqual(5, e.Pool.Count);
        }
    }

    public class ConfigTests
    {
        [Test]
        public void ConfigJson_BerhasilParse_SemuaItem()
        {
            string json = ArAssetCatalog.LoadText("assets/ar/config.json");
            Assert.IsNotNull(json, "config.json tidak ada di Resources");
            var cfg = JsonUtility.FromJson<BoothConfig>(json);
            Assert.IsNotNull(cfg);
            Assert.GreaterOrEqual(cfg.items.Count, 10, "jumlah item config");
            var ids = cfg.items.ConvertAll(i => i.id);
            string[] expected = { "sunglasses", "mustache", "crown", "party-frame",
                "party-hat", "clown-nose", "spinning-star", "fire",
                "confetti-burst", "hearts-smile", "cat-ears", "spin-gif" };
            foreach (var id in expected)
                Assert.Contains(id, ids);
        }

        [Test]
        public void ArManager_Toggle_EksklusivitasKategori()
        {
            var mgr = new ArManager(new AnimationTicker());
            mgr.LoadConfig("assets/ar/config.json");
            // sunglasses & crown beda kategori — bisa sama-sama aktif.
            mgr.Toggle("sunglasses");
            mgr.Toggle("crown");
            Assert.IsTrue(mgr.IsActive("sunglasses"));
            Assert.IsTrue(mgr.IsActive("crown"));
            // party-hat & crown sama-sama kategori "head" → toggle party-hat melepas crown.
            var hat = mgr.Items.Find(i => i.id == "party-hat");
            var crown = mgr.Items.Find(i => i.id == "crown");
            Assert.AreEqual(hat.category, crown.category, "asumsi kategori head");
            mgr.Toggle("party-hat");
            Assert.IsTrue(mgr.IsActive("party-hat"));
            Assert.IsFalse(mgr.IsActive("crown"));
        }

        [Test]
        public void ArManager_Trigger_SmileShowParticle()
        {
            var mgr = new ArManager(new AnimationTicker());
            mgr.LoadConfig("assets/ar/config.json");
            var hearts = mgr.Items.Find(i => i.id == "hearts-smile");
            Assert.IsNotNull(hearts?.Trigger, "hearts-smile harus punya trigger");
            // Kirim skor smile tinggi → trigger aktif.
            mgr.HandleExpressions(new ExpressionScores { Smile = 1f });
            Assert.IsTrue(hearts.Runtime.ParticleOn, "ParticleOn harus true saat smile");
            // Skor turun di bawah hysteresis → lepas.
            mgr.HandleExpressions(new ExpressionScores { Smile = 0f });
            Assert.IsFalse(hearts.Runtime.ParticleOn);
        }

        [Test]
        public void ArManager_Reactive_DefaultTidakPlaying()
        {
            var mgr = new ArManager(new AnimationTicker());
            mgr.LoadConfig("assets/ar/config.json");
            var star = mgr.Items.Find(i => i.id == "spinning-star");
            if (star.Anim?.Type == "reactive")
                Assert.IsFalse(star.Runtime.Playing, "reactive harus mulai paused");
        }
    }

    public class SpriteGifTests
    {
        [Test]
        public void SpriteDecoder_SliceGrid_Benar()
        {
            var tex = new Texture2D(128, 64, TextureFormat.RGBA32, false);
            var src = SpriteDecoder.DecodeSheet(tex, 4, 2, 0);
            Assert.AreEqual(8, src.Frames.Length);
            var f0 = src.Frames[0].Uv;
            Assert.AreEqual(0f, f0.x, 1e-4);
            Assert.AreEqual(0.5f, f0.y, 1e-4); // baris atas (y=0.5 karena UV y-up)
            Assert.AreEqual(0.25f, f0.width, 1e-4);
            Object.DestroyImmediate(tex);
        }

        [Test]
        public void SpriteDecoder_FrameCountMembatasi()
        {
            var tex = new Texture2D(128, 64, TextureFormat.RGBA32, false);
            var src = SpriteDecoder.DecodeSheet(tex, 4, 2, 5);
            Assert.AreEqual(5, src.Frames.Length);
            Object.DestroyImmediate(tex);
        }

        [Test]
        public void GifDecoder_DecodeRealAsset_FramesDanDelay()
        {
            var bytes = ArAssetCatalog.LoadBytes("assets/ar/2d/spin-star.gif");
            Assert.IsNotNull(bytes, "spin-star.gif tidak ada di Resources");
            var src = GifDecoder.Decode(bytes);
            Assert.IsNotNull(src);
            Assert.Greater(src.Frames.Length, 1, "GIF harus punya >1 frame");
            Assert.IsNotNull(src.Delays, "GIF harus punya delay per frame");
            Assert.AreEqual(src.Frames.Length, src.Delays.Length);
            // Semua frame valid dan ukuran konsisten.
            int w = src.Frames[0].Tex.width, h = src.Frames[0].Tex.height;
            foreach (var f in src.Frames)
            {
                Assert.AreEqual(w, f.Tex.width);
                Assert.AreEqual(h, f.Tex.height);
            }
        }
    }

    public class ConfigAssetTests
    {
        [Test]
        public void SemuaAssetItem_AdaDiResources()
        {
            var mgr = new ArManager(new AnimationTicker());
            mgr.LoadConfig("assets/ar/config.json");
            foreach (var item in mgr.Items)
            {
                if (string.IsNullOrEmpty(item.src)) continue;
                string ext = System.IO.Path.GetExtension(item.src).ToLowerInvariant();
                if (ext == ".glb" || ext == ".gif")
                    Assert.IsNotNull(ArAssetCatalog.LoadBytes(item.src), "asset hilang: " + item.src);
                else
                    Assert.IsNotNull(ArAssetCatalog.LoadTexture(item.src), "asset hilang: " + item.src);
            }
        }
    }
}
