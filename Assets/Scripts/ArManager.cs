// ArManager.cs — port of js/ar-manager.js
// Load config.json, kelola state AR items aktif, parsing field `animation` +
// `trigger`, preload frame sprite/GIF, dan dispatch event ekspresi ke aksi item.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    /// <summary>Satu frame animasi 2D: tekstur + rect UV normalized.</summary>
    public struct SpriteFrame
    {
        public Texture2D Tex;
        public Rect Uv;
    }

    /// <summary>Sumber frame animasi item: frames + delays (GIF) atau null (sprite pakai fps).</summary>
    public class AnimSource
    {
        public SpriteFrame[] Frames;
        public float[] Delays; // detik per frame (GIF); null untuk sprite sheet
    }

    public class ArManager
    {
        static readonly HashSet<string> AnimTypes = new HashSet<string>
        { "loop", "sprite-sheet", "gif", "particle", "reactive" };
        static readonly HashSet<string> Expressions = new HashSet<string>
        { "blink", "mouth_open", "smile", "raise_brows" };
        static readonly HashSet<string> TriggerActions = new HashSet<string>
        { "play_once", "toggle", "speed_boost", "show_particle" };
        const float Hysteresis = 0.7f; // ekspresi lepas saat skor < threshold * 0.7
        const long MemBudgetBytes = 50L * 1024 * 1024; // NFR: total AR asset <50MB

        public List<ArItemConfig> Items = new List<ArItemConfig>();
        public BrandingJson Branding = new BrandingJson();
        public readonly HashSet<string> ActiveIds = new HashSet<string>();

        readonly Dictionary<string, Texture2D> _images = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, AnimSource> _frames = new Dictionary<string, AnimSource>();
        readonly Dictionary<string, bool> _exprState = new Dictionary<string, bool>();
        readonly HashSet<string> _warned = new HashSet<string>();
        long _decodedBytes;

        readonly AnimationTicker _ticker;
        public Action<List<ArItemConfig>> OnChange;

        public ArManager(AnimationTicker ticker) { _ticker = ticker; }

        /// <summary>Ambil dan validasi config.json, lalu preload asset 2D/GIF item.</summary>
        public void LoadConfig(string webPath = "assets/ar/config.json")
        {
            string json = ArAssetCatalog.LoadText(webPath)
                ?? throw new Exception($"Gagal memuat {webPath}");
            var config = JsonUtility.FromJson<BoothConfig>(json);
            Branding = config.branding ?? new BrandingJson();
            Items = (config.items ?? new List<ArItemConfig>()).FindAll(
                // Item particle tidak wajib punya src (murni emitter); sisanya wajib.
                it => it != null && !string.IsNullOrEmpty(it.id) && !string.IsNullOrEmpty(it.type)
                      && (!string.IsNullOrEmpty(it.src) || it.type == "particle"));

            foreach (var item in Items)
            {
                item.Anim = ParseAnimation(item);
                item.Trigger = ParseTrigger(item);
                item.Runtime = new ItemRuntime
                {
                    Playing = item.Anim == null || item.Anim.Type != "reactive",
                };
            }
            foreach (var item in Items) PreloadVisual(item);
        }

        /// <summary>
        /// Normalisasi field `animation` dari config: isi default (speed 1, fps 12,
        /// frameCount = cols×rows) dan validasi parameter wajib per tipe. Mengembalikan
        /// null bila item tidak punya animasi atau konfigurasinya tidak valid.
        /// </summary>
        AnimConfig ParseAnimation(ArItemConfig item)
        {
            var raw = item.animation;
            if (raw == null) return null;
            var anim = new AnimConfig
            {
                Type = string.IsNullOrEmpty(raw.type) ? "loop" : raw.type,
                Speed = raw.speed != 0 ? raw.speed : 1f,
                ClipName = string.IsNullOrEmpty(raw.clipName) ? null : raw.clipName,
                Cols = raw.cols,
                Rows = raw.rows,
                FrameCount = raw.frameCount,
                Fps = raw.fps != 0 ? raw.fps : 12f,
            };
            if (!AnimTypes.Contains(anim.Type))
            {
                Debug.LogWarning($"[ar-manager] Item \"{item.id}\": animation.type \"{anim.Type}\" tidak dikenal — animasi diabaikan.");
                return null;
            }
            // Sprite grid dipakai tipe sprite-sheet, atau reactive pada item 2D.
            if (anim.Type == "sprite-sheet" || (anim.Type == "reactive" && item.type == "2d"))
            {
                if (anim.Cols <= 0 || anim.Rows <= 0)
                {
                    Debug.LogWarning($"[ar-manager] Item \"{item.id}\": animation.type \"{anim.Type}\" butuh cols & rows — animasi diabaikan.");
                    return null;
                }
                if (anim.FrameCount <= 0) anim.FrameCount = anim.Cols * anim.Rows;
            }
            if (anim.Type == "particle")
            {
                anim.Particle = ParticlePresets.Resolve(raw.particle);
            }
            return anim;
        }

        /// <summary>
        /// Normalisasi field `trigger` dari config: ekspresi pemicu + aksi + target
        /// (default item sendiri) + threshold sensitivitas 0..1 (default 0.5).
        /// `boost` (opsional, default 3) = multiplier untuk aksi speed_boost.
        /// </summary>
        TriggerConfig ParseTrigger(ArItemConfig item)
        {
            var raw = item.trigger;
            if (raw == null) return null;
            if (!Expressions.Contains(raw.expression) || !TriggerActions.Contains(raw.action))
            {
                Debug.LogWarning($"[ar-manager] Item \"{item.id}\": trigger tidak valid (expression \"{raw.expression}\", action \"{raw.action}\") — diabaikan.");
                return null;
            }
            return new TriggerConfig
            {
                Expression = raw.expression,
                Action = raw.action,
                Target = string.IsNullOrEmpty(raw.target) ? item.id : raw.target,
                Threshold = Mathf.Clamp01(raw.threshold != 0 ? raw.threshold : 0.5f),
                Boost = raw.boost != 0 ? raw.boost : 3f,
            };
        }

        /// <summary>Preload sumber visual item: GIF decode / gambar PNG / sprite slice.</summary>
        void PreloadVisual(ArItemConfig item)
        {
            var anim = item.Anim;
            if (anim?.Type == "gif" && !string.IsNullOrEmpty(item.src))
            {
                try
                {
                    var src = GifDecoder.Decode(ArAssetCatalog.LoadBytes(item.src));
                    _frames[item.id] = src;
                    AccountFrames(item.id, src);
                }
                catch (Exception err)
                {
                    Debug.LogWarning($"[ar-manager] Item \"{item.id}\": gagal decode GIF. {err.Message}");
                }
                return;
            }
            if (item.type != "2d") return;
            var tex = ArAssetCatalog.LoadTexture(item.src); // gagal load tidak fatal — item tetap ada, tak digambar
            if (tex != null)
            {
                _images[item.id] = tex;
                SliceSpriteSheet(item, tex);
            }
        }

        /// <summary>
        /// Slice sprite sheet menjadi array frame UV (sekali saat load) sesuai
        /// cols/rows/frameCount di field animation — sesuai NFR, frame siap pakai
        /// sehingga render loop tinggal menggambar tanpa decode overhead.
        /// </summary>
        void SliceSpriteSheet(ArItemConfig item, Texture2D tex)
        {
            var anim = item.Anim;
            bool wantsGrid =
                anim?.Type == "sprite-sheet" || (anim?.Type == "reactive" && anim.Cols > 0 && anim.Rows > 0);
            if (!wantsGrid) return;
            try
            {
                var src = SpriteDecoder.DecodeSheet(tex, anim.Cols, anim.Rows, anim.FrameCount);
                _frames[item.id] = src;
                AccountFrames(item.id, src);
            }
            catch (Exception err)
            {
                Debug.LogWarning($"[ar-manager] Item \"{item.id}\": gagal slice sprite sheet. {err.Message}");
            }
        }

        /// <summary>Akumulasi estimasi memori frame ter-decode; peringatkan bila lewat budget.</summary>
        void AccountFrames(string itemId, AnimSource src)
        {
            foreach (var f in src.Frames)
            {
                if (f.Uv.width >= 0.999f && f.Uv.height >= 0.999f)
                    _decodedBytes += (long)f.Tex.width * f.Tex.height * 4;
                else
                    _decodedBytes += (long)(f.Tex.width * f.Uv.width) * (long)(f.Tex.height * f.Uv.height) * 4;
            }
            if (_decodedBytes > MemBudgetBytes && !_warned.Contains("mem"))
            {
                _warned.Add("mem");
                Debug.LogWarning($"[ar-manager] Estimasi memori frame animasi ~{_decodedBytes / 1048576}MB melebihi budget 50MB — kurangi jumlah/ukuran frame.");
            }
        }

        /// <summary>Ambil Texture2D untuk item 2D statis (null bila belum termuat).</summary>
        public Texture2D GetImage(ArItemConfig item)
            => _images.TryGetValue(item.id, out var t) ? t : null;

        /// <summary>Ambil sumber frame animasi item; null bila frame belum siap.</summary>
        public AnimSource GetAnimSource(ArItemConfig item)
            => _frames.TryGetValue(item.id, out var s) && s.Frames.Length > 0 ? s : null;

        /// <summary>
        /// Terima skor ekspresi terbaru dan jalankan trigger item yang cocok.
        /// Edge detection per item dengan hysteresis supaya aksi tidak flicker.
        /// </summary>
        public void HandleExpressions(ExpressionScores scores)
        {
            if (scores == null) return;
            foreach (var item in Items)
            {
                var trg = item.Trigger;
                if (trg == null) continue;
                float s = scores[trg.Expression];
                _exprState.TryGetValue(item.id, out bool was);
                bool active = was;
                if (!was && s >= trg.Threshold) active = true;
                else if (was && s < trg.Threshold * Hysteresis) active = false;
                if (active == was) continue;
                _exprState[item.id] = active;
                var target = trg.Target == item.id
                    ? item
                    : Items.Find(it => it.id == trg.Target);
                if (target == null)
                {
                    WarnOnce($"trg-{item.id}", $"[ar-manager] Item \"{item.id}\": trigger target \"{trg.Target}\" tidak ditemukan.");
                    continue;
                }
                ApplyTrigger(item, target, trg, active);
            }
        }

        /// <summary>Terapkan aksi trigger ke target.Runtime pada edge naik/turun ekspresi.</summary>
        void ApplyTrigger(ArItemConfig item, ArItemConfig target, TriggerConfig trg, bool active)
        {
            var rt = target.Runtime;
            float now = _ticker.ElapsedTime;
            bool isParticle = target.type == "particle" || target.Anim?.Type == "particle";
            switch (trg.Action)
            {
                case "play_once":
                    if (!active) break;
                    if (isParticle) rt.BurstAt = now;
                    else if (target.Anim != null) rt.PlayOnceAt = now;
                    else rt.BounceAt = now;
                    break;
                case "toggle":
                    if (!active) break;
                    if (isParticle) rt.ParticleOn = !rt.ParticleOn;
                    else if (target.Anim != null) rt.Playing = !rt.Playing;
                    else rt.Hidden = !rt.Hidden;
                    break;
                case "speed_boost":
                    rt.SpeedMul = active ? trg.Boost : 1f;
                    break;
                case "show_particle":
                    if (!isParticle)
                    {
                        WarnOnce($"sp-{item.id}", $"[ar-manager] Item \"{item.id}\": show_particle butuh target bertipe particle (\"{target.id}\" bukan) — diabaikan.");
                        break;
                    }
                    rt.ParticleOn = active;
                    if (active) rt.BurstAt = now; // langsung sembur saat ekspresi mulai
                    break;
            }
        }

        void WarnOnce(string key, string msg)
        {
            if (_warned.Contains(key)) return;
            _warned.Add(key);
            Debug.LogWarning(msg);
        }

        /// <summary>
        /// Toggle aktif/nonaktif item berdasarkan id. Bila item punya `category`,
        /// item lain di kategori yang sama otomatis dilepas — mencegah dua
        /// aksesori bertabrakan di slot wajah yang sama.
        /// </summary>
        public bool Toggle(string id)
        {
            if (ActiveIds.Contains(id))
            {
                ActiveIds.Remove(id);
            }
            else
            {
                var item = Items.Find(it => it.id == id);
                if (!string.IsNullOrEmpty(item?.category))
                {
                    foreach (var other in Items)
                    {
                        if (other.id != id && other.category == item.category)
                            ActiveIds.Remove(other.id);
                    }
                }
                ActiveIds.Add(id);
            }
            Notify();
            return ActiveIds.Contains(id);
        }

        public bool IsActive(string id) => ActiveIds.Contains(id);

        /// <summary>Daftar item aktif (objek config lengkap), dipakai renderer tiap frame.</summary>
        public List<ArItemConfig> GetActiveItems()
            => Items.FindAll(it => ActiveIds.Contains(it.id));

        void Notify() => OnChange?.Invoke(GetActiveItems());
    }
}
