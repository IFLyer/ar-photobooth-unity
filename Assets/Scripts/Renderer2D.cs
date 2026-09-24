// Renderer2D.cs — port of js/ar-renderer-2d.js
// Gambar item 2D aktif (PNG statis, sprite sheet, GIF) sebagai quad dunia +
// transisi scale-bounce/fade + clock animasi per item + particle engine.
//
// Perbedaan vs web: canvas 2D diganti quad mesh di bidang z≈-0.1 (dunia);
// math posisi/rotasi identik (canvas rot θ → world rot -θ).

using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    public class Renderer2D : MonoBehaviour
    {
        public BoothStage Stage;
        public ArManager Manager;
        public Material StickerMaterial;   // ArBooth/Unlit queue 3000
        public Material ParticleMaterial;  // ArBooth/Unlit queue 3010

        public readonly ParticleEngine Particles;

        class TransitionState
        {
            public ArItemConfig Item;
            public string Phase = "in";   // "in" | "out"
            public float T0;
            public float BounceSeen = -1f;
            public List<LastDraw> Last = new List<LastDraw>(); // posisi terakhir per slot wajah
        }
        public struct LastDraw { public Vector3 Pos; public float RotDeg; public float Width; }

        class AnimClock
        {
            public float T;
            public bool Playing;
            public bool Once;
            public float ConsumedOnce = -1f;
        }

        readonly Dictionary<string, TransitionState> _transitions = new Dictionary<string, TransitionState>();
        readonly Dictionary<string, AnimClock> _clocks = new Dictionary<string, AnimClock>();
        readonly Dictionary<string, List<GameObject>> _quads = new Dictionary<string, List<GameObject>>();
        readonly Dictionary<string, ParticleBatch> _batches = new Dictionary<string, ParticleBatch>();
        Mesh _quadMesh;
        MaterialPropertyBlock _mpb;
        Transform _root;

        public Renderer2D()
        {
            Particles = new ParticleEngine(null);
        }

        public void Init(ArManager manager)
        {
            Manager = manager;
            Particles.SetManager(manager);
        }

        void EnsureInit()
        {
            if (_root != null) return;
            _root = new GameObject("Renderer2D").transform;
            _root.SetParent(transform, false);
            _quadMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx") ?? CreateFallbackQuad();
            _mpb = new MaterialPropertyBlock();
        }

        static Mesh CreateFallbackQuad()
        {
            var m = new Mesh();
            m.vertices = new[]
            {
                new Vector3(-.5f, -.5f), new Vector3(.5f, -.5f),
                new Vector3(.5f, .5f), new Vector3(-.5f, .5f),
            };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            return m;
        }

        GameObject QuadAt(string itemId, int faceIdx)
        {
            EnsureInit();
            if (!_quads.TryGetValue(itemId, out var arr))
            {
                arr = new List<GameObject>();
                _quads[itemId] = arr;
            }
            while (arr.Count <= faceIdx)
            {
                var go = new GameObject($"sticker_{itemId}_{arr.Count}");
                go.transform.SetParent(_root, false);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = _quadMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = StickerMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.SetActive(false);
                arr.Add(go);
            }
            return arr[faceIdx];
        }

        /// <summary>Gambar semua item 2D aktif + particle; lanjutkan fade-out yang berjalan.</summary>
        public void Render(List<FacePose> poses, List<ArItemConfig> activeItems)
        {
            EnsureInit();
            float now = BoothApp.Ticker.ElapsedTime;
            float dt = BoothApp.Ticker.DeltaTime;
            var activeIds = new HashSet<string>();

            foreach (var item in activeItems)
            {
                if (item.type != "2d") continue;
                activeIds.Add(item.id);
                var rt = item.Runtime;
                if (!_transitions.TryGetValue(item.id, out var tr) || tr.Phase == "out")
                {
                    tr = new TransitionState
                    {
                        Item = item,
                        Phase = "in",
                        T0 = now,
                        Last = tr?.Last ?? new List<LastDraw>(),
                        BounceSeen = -1f,
                    };
                    _transitions[item.id] = tr;
                }
                // play_once pada item statis → replay transisi pasang (bounce).
                if (rt != null && rt.BounceAt != tr.BounceSeen)
                {
                    tr.BounceSeen = rt.BounceAt;
                    if (rt.BounceAt > 0)
                    {
                        tr.Phase = "in";
                        tr.T0 = rt.BounceAt;
                    }
                }
                if (rt != null && rt.Hidden)
                {
                    HideItemQuads(item.id);
                    continue;
                }
                DrawAllFaces(tr.Item, poses, tr, now, dt, 1f);
            }

            // Item yang baru dilepas: fade-out singkat lalu hapus record.
            var done = new List<string>();
            foreach (var kv in _transitions)
            {
                string id = kv.Key;
                var tr = kv.Value;
                if (activeIds.Contains(id)) continue;
                if (tr.Phase == "in")
                {
                    tr.Phase = "out";
                    tr.T0 = now;
                }
                float p = AnimEase.Clamp01((now - tr.T0) / Transition.Detach);
                if (p >= 1f)
                {
                    HideItemQuads(id);
                    done.Add(id);
                    continue;
                }
                if (poses.Count > 0) DrawAllFaces(tr.Item, poses, tr, now, dt, 1f - p);
                else
                {
                    for (int i = 0; i < tr.Last.Count; i++)
                        DrawItem(tr.Item, null, tr, now, dt, 1f - p, i);
                }
            }
            foreach (var id in done) _transitions.Remove(id);

            // Particle di atas sticker 2D — ikut ter-composite saat capture.
            Particles.Update(dt, poses, Stage.VideoWidth, Stage.VideoHeight);
            RenderBatches();
        }

        void HideItemQuads(string itemId)
        {
            if (_quads.TryGetValue(itemId, out var arr))
                foreach (var g in arr) g.SetActive(false);
        }

        /// <summary>Gambar item di tiap wajah — satu quad per slot wajah.</summary>
        void DrawAllFaces(ArItemConfig item, List<FacePose> poses, TransitionState tr,
                          float now, float dt, float alphaMul)
        {
            for (int i = 0; i < poses.Count; i++)
                DrawItem(item, poses[i], tr, now, dt, alphaMul, i);
            // Sembunyikan quad untuk slot wajah yang sudah tidak ada.
            if (_quads.TryGetValue(item.id, out var arr))
                for (int i = poses.Count; i < arr.Count; i++)
                    arr[i].SetActive(false);
            if (poses.Count > 0 && tr.Last.Count > poses.Count)
                tr.Last.RemoveRange(poses.Count, tr.Last.Count - poses.Count);
        }

        /// <summary>Ambil sumber frame untuk item: frame animasi via anim clock
        /// per item, atau texture statis.</summary>
        SpriteFrame? PickSource(ArItemConfig item, float dt)
        {
            var anim = item.Anim;
            bool animated = anim != null &&
                (anim.Type == "sprite-sheet" || anim.Type == "gif" ||
                 (anim.Type == "reactive" && anim.Cols > 0));
            if (!animated)
            {
                var tex = Manager.GetImage(item);
                if (tex == null) return null;
                return new SpriteFrame { Tex = tex, Uv = new Rect(0, 0, 1, 1) };
            }
            var src = Manager.GetAnimSource(item);
            if (src == null) return null;

            var rt = item.Runtime;
            if (!_clocks.TryGetValue(item.id, out var ck))
            {
                ck = new AnimClock
                {
                    T = 0,
                    Playing = rt?.Playing ?? true,
                    Once = false,
                    ConsumedOnce = rt?.PlayOnceAt ?? -1f,
                };
                _clocks[item.id] = ck;
            }
            // Konsumsi request play_once: putar satu siklus dari awal.
            if (rt != null && rt.PlayOnceAt > 0 && rt.PlayOnceAt != ck.ConsumedOnce)
            {
                ck.ConsumedOnce = rt.PlayOnceAt;
                ck.T = 0;
                ck.Playing = true;
                ck.Once = true;
            }
            if (!ck.Once) ck.Playing = rt?.Playing ?? true;
            if (ck.Playing)
            {
                ck.T += dt * (anim.Speed != 0 ? anim.Speed : 1f) * (rt?.SpeedMul ?? 1f);
                float dur = CycleDur(anim, src);
                if (ck.Once && ck.T >= dur)
                {
                    ck.Once = false;
                    ck.Playing = rt?.Playing ?? true;
                    ck.T = dur - 1e-4f; // clamp di frame terakhir
                }
            }
            return src.Frames[FrameIndex(anim, src, ck.T)];
        }

        /// <summary>Durasi satu siklus animasi (detik): gif pakai total delay, sprite pakai fps.</summary>
        static float CycleDur(AnimConfig anim, AnimSource src)
        {
            if (src.Delays != null)
            {
                float t = 0;
                foreach (var d in src.Delays) t += d;
                return t;
            }
            return src.Frames.Length / Mathf.Max(anim.Fps, 1f);
        }

        /// <summary>Index frame pada waktu t: gif → delay kumulatif, sprite → t*fps mod n.</summary>
        static int FrameIndex(AnimConfig anim, AnimSource src, float t)
        {
            int n = src.Frames.Length;
            if (src.Delays == null)
            {
                int i = Mathf.FloorToInt(t * anim.Fps) % n;
                return (i + n) % n;
            }
            float total = CycleDur(anim, src);
            float tt = ((t % total) + total) % total;
            float acc = 0;
            for (int i = 0; i < n; i++)
            {
                acc += src.Delays[i];
                if (tt < acc) return i;
            }
            return n - 1;
        }

        /// <summary>Gambar satu item pada satu wajah: posisi dari anchor wajah
        /// (atau posisi terakhir saat pose slot itu tidak ada).</summary>
        void DrawItem(ArItemConfig item, FacePose pose, TransitionState tr,
                      float now, float dt, float alphaMul, int faceIdx)
        {
            float alpha = alphaMul;
            float scaleMul = 1f;
            if (tr.Phase == "in")
            {
                float p = AnimEase.Clamp01((now - tr.T0) / Transition.Attach);
                alpha *= p;
                scaleMul = AnimEase.EaseOutBack(p);
            }

            // Parameter dasar posisi per slot wajah — refresh dari pose bila ada.
            if (pose != null)
            {
                var anchor = ParticleEngine.GetAnchor(pose, item.anchor);
                var off = item.offset;
                var rot = item.rotation;
                float fwW = Stage.FaceWidthWorld(pose);
                var pos = Stage.WorldFromNorm(anchor.x, anchor.y);
                pos.x += (off?.x ?? 0) * fwW;
                pos.y -= (off?.y ?? 0) * fwW; // offset y positif = ke bawah
                pos.z = BoothStage.ZSticker;
                var ld = new LastDraw
                {
                    Pos = pos,
                    // canvas rot (roll + rotZ, clockwise-pixel) → world rot = -roll - rotZ
                    RotDeg = -(pose.Roll * Mathf.Rad2Deg + (rot?.z ?? 0)),
                    Width = fwW * (item.scale != 0 ? item.scale : 1f),
                };
                while (tr.Last.Count <= faceIdx) tr.Last.Add(default);
                tr.Last[faceIdx] = ld;
            }
            if (faceIdx >= tr.Last.Count) return;
            var last = tr.Last[faceIdx];

            var src = PickSource(item, dt);
            if (src == null) return;
            var f = src.Value;

            float srcAspect = (f.Uv.height * f.Tex.height) / (f.Uv.width * f.Tex.width);
            float w = last.Width * scaleMul;
            float h = w * srcAspect;

            var go = QuadAt(item.id, faceIdx);
            go.SetActive(true);
            var t = go.transform;
            t.position = last.Pos;
            t.localRotation = Quaternion.Euler(0, 0, last.RotDeg);
            t.localScale = new Vector3(w, h, 1f);

            _mpb.SetTexture("_MainTex", f.Tex);
            _mpb.SetVector("_MainTex_ST", new Vector4(f.Uv.width, f.Uv.height, f.Uv.x, f.Uv.y));
            _mpb.SetColor("_Color", new Color(1, 1, 1, alpha));
            go.GetComponent<MeshRenderer>().SetPropertyBlock(_mpb);
        }

        /// <summary>Rebuild semua particle batch mesh sesuai emitter aktif.</summary>
        void RenderBatches()
        {
            var seen = new HashSet<string>();
            foreach (var kv in Particles.Emitters)
            {
                seen.Add(kv.Key);
                if (!_batches.TryGetValue(kv.Key, out var b))
                {
                    var go = new GameObject($"particles_{kv.Key}");
                    go.transform.SetParent(_root, false);
                    b = go.AddComponent<ParticleBatch>();
                    b.Setup(ParticleMaterial, Particles.GetImage(kv.Value.Cfg.Src));
                    b.Rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    _batches[kv.Key] = b;
                }
                b.Rebuild(kv.Value, Stage.VideoWidth, Stage.VideoHeight, Stage);
            }
            var stale = new List<string>();
            foreach (var kv in _batches)
            {
                if (!seen.Contains(kv.Key))
                {
                    Destroy(kv.Value.gameObject);
                    stale.Add(kv.Key);
                }
            }
            foreach (var k in stale) _batches.Remove(k);
        }
    }
}
