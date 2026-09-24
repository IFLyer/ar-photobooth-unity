// ParticleEngine.cs — port of js/particle-engine.js
// Particle system: emitter attach ke anchor wajah, preset
// confetti/sparkle/hearts/snow/custom, pool ≤100 per emitter.
// Render dilakukan ParticleBatch (mesh dinamis) — engine ini murni simulasi.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    /// <summary>Konfigurasi particle hasil normalisasi (preset + override user).</summary>
    public class ParticleConfig
    {
        public string Type = "confetti";
        public int Count = 50;
        public float Speed = 1f;
        public float Gravity = 0.5f;
        public float SizeMin, SizeMax;
        public string[] Colors = { "#FFFFFF" };
        public float Lifetime = 2f;
        public float Spread = 360f;
        public float Direction = -90f;
        public string Src;
        public string Shape = "rect";
        public bool Rotate;
        public bool Twinkle;
        public float Sway;
        public float SpawnWidth = 0.2f;
    }

    /// <summary>Preset particle (parity dengan PRESETS di web).</summary>
    public static class ParticlePresets
    {
        public const int MaxParticles = 100;

        class P : ParticleConfig { }

        static readonly Dictionary<string, Func<ParticleConfig>> Presets =
            new Dictionary<string, Func<ParticleConfig>>
        {
            ["confetti"] = () => new ParticleConfig
            {
                Shape = "rect", Direction = -90, Spread = 120, Speed = 2.2f, Gravity = 0.9f,
                Lifetime = 2.8f, SizeMin = 5, SizeMax = 11, SpawnWidth = 0.15f,
                Colors = new[] { "#FF6B6B", "#4ECDC4", "#FFE66D", "#A855F7", "#3B82F6" },
                Rotate = true,
            },
            ["sparkle"] = () => new ParticleConfig
            {
                Shape = "star", Direction = -90, Spread = 360, Speed = 0.35f, Gravity = 0,
                Lifetime = 1.3f, SizeMin = 3, SizeMax = 8, SpawnWidth = 0.55f,
                Colors = new[] { "#FFFFFF", "#FFE66D", "#FFF3B0" },
                Twinkle = true,
            },
            ["hearts"] = () => new ParticleConfig
            {
                Shape = "heart", Direction = -90, Spread = 70, Speed = 1.1f, Gravity = -0.45f,
                Lifetime = 2.6f, SizeMin = 9, SizeMax = 17, SpawnWidth = 0.35f,
                Colors = new[] { "#FF5B8D", "#FF8FAB", "#E63950" },
                Sway = 1.4f,
            },
            ["snow"] = () => new ParticleConfig
            {
                Shape = "circle", Direction = 90, Spread = 50, Speed = 0.35f, Gravity = 0.3f,
                Lifetime = 4.5f, SizeMin = 3, SizeMax = 8, SpawnWidth = 0.9f,
                Colors = new[] { "#FFFFFF", "#DCEEFF" },
                Sway = 1.0f,
            },
            ["custom"] = () => new ParticleConfig
            {
                Shape = "image", Direction = -90, Spread = 360, Speed = 1.0f, Gravity = 0.5f,
                Lifetime = 2.0f, SizeMin = 8, SizeMax = 16, SpawnWidth = 0.2f,
                Colors = new[] { "#FFFFFF" },
            },
        };

        static float Num(float v, float dflt) => v != 0 ? v : dflt;

        /// <summary>
        /// Normalisasi animation.particle dari config.json ke bentuk final.
        /// Default dari preset tipe; field user selalu menang. count di-clamp
        /// ke MaxParticles (maks 100 particle aktif per emitter).
        /// </summary>
        public static ParticleConfig Resolve(ParticleJson raw)
        {
            string type = raw != null && Presets.ContainsKey(raw.type) ? raw.type : "confetti";
            var p = Presets[type]();
            float sizeMin = raw?.size?.min ?? 0;
            float sizeMax = raw?.size?.max ?? 0;
            if (sizeMin <= 0 || sizeMax <= 0) { sizeMin = p.SizeMin; sizeMax = p.SizeMax; }
            p.Type = type;
            p.Count = Mathf.Clamp(raw?.count > 0 ? raw.count : 50, 1, MaxParticles);
            p.Speed = Num(raw?.speed ?? 0, p.Speed);
            p.Gravity = Num(raw?.gravity ?? 0, p.Gravity);
            p.SizeMin = sizeMin;
            p.SizeMax = sizeMax;
            p.Colors = raw?.colors != null && raw.colors.Length > 0 ? raw.colors : p.Colors;
            p.Lifetime = Mathf.Max(Num(raw?.lifetime ?? 0, p.Lifetime), 0.1f);
            p.Spread = Num(raw?.spread ?? 0, p.Spread);
            p.Direction = Num(raw?.direction ?? 0, p.Direction);
            p.Src = string.IsNullOrEmpty(raw?.src) ? null : raw.src;
            p.Shape = !string.IsNullOrEmpty(raw?.src) ? "image" : p.Shape;
            p.Sway = Num(raw?.sway ?? 0, p.Sway);
            return p;
        }
    }

    /// <summary>Satu particle hidup — field mutasi tiap update.</summary>
    public class Particle
    {
        public float X, Y, Vx, Vy, Rot, Vr, Size, Life, Ttl, Phase;
        public Color32 Color;
    }

    /// <summary>
    /// Satu emitter per (item, slot wajah). Particle hidup dalam array Parts;
    /// yang mati didaur ulang lewat Pool agar tidak alokasi per frame.
    /// Koordinat dalam pixel ruang kamera (x kanan, y bawah — sama seperti canvas 2D).
    /// </summary>
    public class Emitter
    {
        public readonly ParticleConfig Cfg;
        public readonly List<Particle> Parts = new List<Particle>();
        public readonly Queue<Particle> Pool = new Queue<Particle>();
        public float SpawnAcc;
        public float X, Y;
        public float FwPx = 300f; // lebar wajah terakhir dalam px — dipakai saat wajah hilang
        public bool HasPos;
        public float BurstSeen = -1f;
        readonly System.Random _rng = new System.Random();

        public Emitter(ParticleConfig cfg) { Cfg = cfg; }

        float Rnd() => (float)_rng.NextDouble();
        float RndRange(float a, float b) => a + Rnd() * (b - a);

        static readonly Dictionary<string, Color32> ColorCache = new Dictionary<string, Color32>();
        static Color32 ParseColor(string hex)
        {
            if (ColorCache.TryGetValue(hex, out var c)) return c;
            ColorUtility.TryParseHtmlString(hex, out var col);
            var c32 = (Color32)col;
            ColorCache[hex] = c32;
            return c32;
        }

        /// <summary>Spawn n particle baru (dibatasi cfg.count). burst = dorongan kecepatan ekstra.</summary>
        public void Spawn(int n, float fwPx, bool burst)
        {
            var cfg = Cfg;
            for (int i = 0; i < n && Parts.Count < cfg.Count; i++)
            {
                var p = Pool.Count > 0 ? Pool.Dequeue() : new Particle();
                float spread = cfg.Spread >= 360 ? Rnd() * 360 : (Rnd() - 0.5f) * cfg.Spread;
                float ang = (cfg.Direction + spread) * Mathf.Deg2Rad;
                float spd = cfg.Speed * fwPx * (0.5f + Rnd() * 0.7f) * (burst ? 1.35f : 1f);
                p.X = X + (Rnd() - 0.5f) * cfg.SpawnWidth * fwPx;
                p.Y = Y + (Rnd() - 0.5f) * cfg.SpawnWidth * fwPx * 0.4f;
                p.Vx = Mathf.Cos(ang) * spd;
                p.Vy = Mathf.Sin(ang) * spd;
                p.Rot = Rnd() * Mathf.PI * 2f;
                p.Vr = (Rnd() - 0.5f) * 10f;
                p.Size = RndRange(cfg.SizeMin, cfg.SizeMax);
                p.Color = ParseColor(cfg.Colors[(int)(Rnd() * cfg.Colors.Length) % cfg.Colors.Length]);
                p.Life = 0;
                p.Ttl = cfg.Lifetime * (0.7f + Rnd() * 0.6f);
                p.Phase = Rnd() * Mathf.PI * 2f;
                Parts.Add(p);
            }
        }

        /// <summary>Burst: keluarkan cfg.count particle sekaligus (dipakai trigger play_once).</summary>
        public void Burst(float fwPx) => Spawn(Cfg.Count, fwPx, true);

        /// <summary>Update fisika semua particle; yang mati kembali ke pool.</summary>
        public void Update(float dt, float fwPx)
        {
            float g = Cfg.Gravity * fwPx * 1.6f;
            float swayK = Cfg.Sway * fwPx * 0.15f;
            var parts = Parts;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var p = parts[i];
                p.Life += dt;
                if (p.Life >= p.Ttl)
                {
                    parts[i] = parts[parts.Count - 1];
                    parts.RemoveAt(parts.Count - 1);
                    Pool.Enqueue(p);
                    continue;
                }
                p.Vy += g * dt;
                p.X += (p.Vx + Mathf.Sin(p.Life * 3 + p.Phase) * swayK) * dt;
                p.Y += p.Vy * dt;
                p.Rot += p.Vr * dt;
            }
        }

        /// <summary>Alpha efektif particle saat ini (fade-in/fade-out + twinkle).</summary>
        public static float ParticleAlpha(ParticleConfig cfg, Particle p)
        {
            float fadeIn = AnimEase.Clamp01(p.Life / 0.08f);
            float fadeOut = AnimEase.Clamp01((p.Ttl - p.Life) / (p.Ttl * 0.35f));
            float alpha = Mathf.Min(fadeIn, fadeOut);
            if (cfg.Twinkle) alpha *= 0.55f + 0.45f * Mathf.Sin(p.Life * 10 + p.Phase);
            return alpha;
        }
    }

    /// <summary>
    /// Mengelola semua emitter particle. Dipanggil dari Renderer2D tiap frame:
    /// Update() menyinkronkan emitter dengan item aktif + state trigger.
    /// Multi-face: satu emitter per (item, slot wajah) — key item.id untuk wajah
    /// pertama, item.id#n untuk wajah berikutnya.
    /// </summary>
    public class ParticleEngine
    {
        ArManager _arManager;
        public readonly Dictionary<string, Emitter> Emitters = new Dictionary<string, Emitter>();
        readonly Dictionary<string, Texture2D> _imgCache = new Dictionary<string, Texture2D>();
        readonly HashSet<string> _imgLoading = new HashSet<string>();

        public ParticleEngine(ArManager arManager) { _arManager = arManager; }

        /// <summary>Ganti manager referensi (Renderer2D mengisi saat Init).</summary>
        public void SetManager(ArManager arManager) { _arManager = arManager; }

        /// <summary>Preload gambar particle custom (particle.src) — sekali per src.</summary>
        void LoadImage(string src)
        {
            if (_imgCache.ContainsKey(src) || _imgLoading.Contains(src)) return;
            _imgLoading.Add(src);
            var tex = ArAssetCatalog.LoadTexture(src);
            if (tex != null) _imgCache[src] = tex;
            _imgLoading.Remove(src);
        }

        /// <summary>Gambar particle custom (null bila belum termuat).</summary>
        public Texture2D GetImage(string src)
            => src != null && _imgCache.TryGetValue(src, out var t) ? t : null;

        /// <summary>
        /// Sinkronkan emitter dengan items config; simulasi fisika; dipanggil tiap frame.
        /// Emitter di slot wajah yang hilang berhenti spawn tapi particle-nya tetap
        /// disimulasikan sampai habis.
        /// </summary>
        public void Update(float dt, List<FacePose> poses, float W, float H)
        {
            var activeIds = _arManager.ActiveIds;

            foreach (var item in _arManager.Items)
            {
                if (item.Anim?.Type != "particle") continue;
                var rt = item.Runtime;
                bool emitting = !rt.Hidden && (activeIds.Contains(item.id) || rt.ParticleOn);

                // Slot yang diproses = wajah saat ini + slot emitter lama yang masih hidup.
                int slots = Mathf.Max(poses.Count, 1);
                foreach (var key in Emitters.Keys)
                {
                    if (key.StartsWith(item.id + "#"))
                    {
                        int f = int.Parse(key.Substring(item.id.Length + 1)) + 1;
                        if (f > slots) slots = f;
                    }
                }

                for (int f = 0; f < slots; f++)
                {
                    var pose = f < poses.Count ? poses[f] : null;
                    string key = f == 0 ? item.id : $"{item.id}#{f}";
                    Emitters.TryGetValue(key, out var e);
                    bool pendingBurst = rt != null && rt.BurstAt > 0 && rt.BurstAt != e?.BurstSeen;

                    if (e == null)
                    {
                        if (!emitting && !pendingBurst) continue;
                        e = new Emitter(item.Anim.Particle);
                        if (!string.IsNullOrEmpty(e.Cfg.Src)) LoadImage(e.Cfg.Src);
                        Emitters[key] = e;
                    }

                    // Emitter mengikuti anchor wajah (offset sama seperti item 2D).
                    if (pose != null)
                    {
                        float fwPx = pose.FaceWidth * W;
                        e.FwPx = fwPx;
                        var anchor = GetAnchor(pose, item.anchor);
                        var off = item.offset;
                        e.X = anchor.x * W + (off?.x ?? 0) * fwPx;
                        e.Y = anchor.y * H + (off?.y ?? 0) * fwPx;
                        e.HasPos = true;
                    }

                    if (rt != null && rt.BurstAt != e.BurstSeen)
                    {
                        e.BurstSeen = rt.BurstAt;
                        if (rt.BurstAt > 0 && e.HasPos) e.Burst(e.FwPx);
                    }

                    // Emisi kontinu: rate = count/lifetime menjaga populasi ~count.
                    if (emitting && e.HasPos && pose != null)
                    {
                        e.SpawnAcc += (e.Cfg.Count / e.Cfg.Lifetime) * dt;
                        int n = Mathf.FloorToInt(e.SpawnAcc);
                        if (n > 0)
                        {
                            e.SpawnAcc -= n;
                            e.Spawn(n, e.FwPx, false);
                        }
                    }
                    else
                    {
                        e.SpawnAcc = 0;
                    }

                    e.Update(dt, e.FwPx);

                    // Bersihkan emitter yang sudah tidak dipakai & particle-nya habis.
                    if (!emitting && e.Parts.Count == 0 && !activeIds.Contains(item.id) && !rt.ParticleOn)
                    {
                        Emitters.Remove(key);
                    }
                }
            }
        }

        /// <summary>Anchor wajah by name (fallback full_face).</summary>
        public static FaceLandmark GetAnchor(FacePose pose, string name)
        {
            if (name != null && pose.Anchors.TryGetValue(name, out var a)) return a;
            return pose.Anchors["full_face"];
        }
    }
}
