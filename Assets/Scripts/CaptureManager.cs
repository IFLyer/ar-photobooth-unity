// CaptureManager.cs — port of js/capture.js
// Ambil isi RenderTarget stage (video + 2D + 3D + particle), mirror horizontal
// (sama seperti scale(-1,1) di web), stamp watermark di kanan bawah, encode PNG,
// simpan ke file ar-booth-YYYYMMDD-HHMMSS.png.

using System;
using System.IO;
using UnityEngine;

namespace ArBooth
{
    public class CaptureManager : MonoBehaviour
    {
        public BoothStage Stage;
        [Tooltip("Lebar watermark relatif terhadap lebar frame (parity web: 0.16)")]
        public float WatermarkWidthFrac = 0.16f;
        [Tooltip("Margin watermark dari tepi relatif lebar frame (parity web: 0.025)")]
        public float WatermarkMarginFrac = 0.025f;
        [Tooltip("Opacity watermark (parity web: globalAlpha 0.85)")]
        public float WatermarkAlpha = 0.85f;

        Texture2D _watermark;

        /// <summary>Hasil capture terakhir (untuk preview/retake/save).</summary>
        public Texture2D LastPhoto { get; private set; }

        void Awake()
        {
            _watermark = ArAssetCatalog.LoadTexture("assets/branding/watermark.png");
        }

        /// <summary>
        /// Baca RenderTexture → Texture2D, flip horizontal, stamp watermark.
        /// Dipanggil tepat setelah frame selesai dirender (WaitForEndOfFrame).
        /// </summary>
        public Texture2D Capture()
        {
            var rt = Stage.Target;
            if (rt == null) return null;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var raw = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            raw.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            raw.Apply();
            RenderTexture.active = prev;

            var photo = FlipHorizontal(raw);
            Destroy(raw);
            StampWatermark(photo);
            if (LastPhoto != null) Destroy(LastPhoto);
            LastPhoto = photo;
            return photo;
        }

        /// <summary>Flip horizontal via GetPixels32 blok-scanline (cepat, tanpa SetPixel per px).</summary>
        static Texture2D FlipHorizontal(Texture2D src)
        {
            int w = src.width, h = src.height;
            var dst = new Texture2D(w, h, src.format, false);
            var px = src.GetPixels32();
            var row = new Color32[w];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(px, y * w, row, 0, w);
                Array.Reverse(row);
                Array.Copy(row, 0, px, y * w, w);
            }
            dst.SetPixels32(px);
            dst.Apply();
            return dst;
        }

        /// <summary>Stamp watermark di pojok kanan bawah (parity web: wmW=w*0.16,
        /// margin=w*0.025, globalAlpha=0.85).</summary>
        void StampWatermark(Texture2D photo)
        {
            if (_watermark == null) return;
            int w = photo.width, h = photo.height;
            int wmW = Mathf.Max(8, Mathf.RoundToInt(w * WatermarkWidthFrac));
            int wmH = Mathf.Max(8, Mathf.RoundToInt(wmW * ((float)_watermark.height / _watermark.width)));
            int margin = Mathf.RoundToInt(w * WatermarkMarginFrac);
            int x0 = w - wmW - margin;
            int y0 = margin; // Texture2D y=0 di bawah → kanan-bawah = x besar, y kecil
            BlitAlpha(_watermark, photo, x0, y0, wmW, wmH, WatermarkAlpha);
            photo.Apply();
        }

        /// <summary>Blend src (diskalakan) ke dst pada rect (x,y,w,h) — alpha over,
        /// alpha sumber dikalikan alphaMul (globalAlpha).</summary>
        static void BlitAlpha(Texture2D src, Texture2D dst, int x, int y, int w, int h, float alphaMul)
        {
            var d = dst.GetPixels32();
            var sp = src.GetPixels32();
            for (int j = 0; j < h; j++)
            {
                int dy = y + j;
                if (dy < 0 || dy >= dst.height) continue;
                int sy = Mathf.Clamp(Mathf.FloorToInt((float)j / h * src.height), 0, src.height - 1);
                for (int i = 0; i < w; i++)
                {
                    int dx = x + i;
                    if (dx < 0 || dx >= dst.width) continue;
                    int sx = Mathf.Clamp(Mathf.FloorToInt((float)i / w * src.width), 0, src.width - 1);
                    var sc = (Color)sp[sy * src.width + sx];
                    sc.a *= alphaMul;
                    if (sc.a <= 0.003f) continue;
                    var dp = d[dy * dst.width + dx];
                    float a = sc.a + dp.a * (1f - sc.a);
                    float r = (sc.r * sc.a + dp.r * dp.a * (1f - sc.a)) / Mathf.Max(a, 1e-4f);
                    float g = (sc.g * sc.a + dp.g * dp.a * (1f - sc.a)) / Mathf.Max(a, 1e-4f);
                    float b = (sc.b * sc.a + dp.b * dp.a * (1f - sc.a)) / Mathf.Max(a, 1e-4f);
                    d[dy * dst.width + dx] = new Color(r, g, b, a);
                }
            }
            dst.SetPixels32(d);
        }

        /// <summary>Simpan PNG ke disk; kembalikan path absolut. Nama parity web.</summary>
        public string SavePng(Texture2D photo)
        {
            string name = "ar-booth-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            string dir = CapturesDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, photo.EncodeToPNG());
#if UNITY_ANDROID && !UNITY_EDITOR
            // Beri tahu MediaStore supaya foto langsung muncul di gallery.
            try
            {
                var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var ctx = player.GetStatic<AndroidJavaObject>("currentActivity");
                var scanner = new AndroidJavaClass("android.media.MediaScannerConnection");
                scanner.CallStatic("scanFile", ctx,
                    new[] { path }, new[] { "image/png" }, (AndroidJavaObject)null);
            }
            catch (Exception) { }
#endif
            return path;
        }

        /// <summary>Folder tujuan simpan per platform.</summary>
        public static string CapturesDir()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Android: simpan ke Pictures supaya bisa diakses gallery/file manager.
            try
            {
                var env = new AndroidJavaClass("android.os.Environment");
                var dirEnum = env.GetStatic<string>("DIRECTORY_PICTURES");
                using (var f = new AndroidJavaObject("java.io.File",
                    env.CallStatic<AndroidJavaObject>("getExternalStoragePublicDirectory", dirEnum),
                    "AR-Booth"))
                    return f.Call<string>("getAbsolutePath");
            }
            catch (Exception) { return Application.persistentDataPath; }
#else
            return Path.Combine(Application.persistentDataPath, "Captures");
#endif
        }
    }
}
