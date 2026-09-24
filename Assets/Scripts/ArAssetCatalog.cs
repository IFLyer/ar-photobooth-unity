// ArAssetCatalog.cs — Pemetaan path ala web ("assets/ar/2d/x.png") ke asset
// yang dibundel di Resources:
//   .png/.json → Resources.Load tanpa ekstensi
//   .gif/.glb  → disimpan sebagai *.bytes → Resources.Load<TextAsset> pakai nama
//                lengkap (ekstensi .gif/.glb tetap bagian nama resource).
// config.json dibiarkan identik dengan versi web — mapping terjadi di sini.

using UnityEngine;

namespace ArBooth
{
    public static class ArAssetCatalog
    {
        const string AssetsPrefix = "assets/";

        /// <summary>Path resource untuk path ala web. null bila path kosong.</summary>
        public static string ResourcePath(string webPath)
        {
            if (string.IsNullOrEmpty(webPath)) return null;
            string p = webPath;
            if (p.StartsWith(AssetsPrefix)) p = p.Substring(AssetsPrefix.Length);
            string ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".json":
                    return p.Substring(0, p.Length - ext.Length);
                default:
                    return p; // .gif/.glb disimpan sebagai .bytes → nama resource memuat ext asli
            }
        }

        /// <summary>Load gambar (PNG) sebagai Texture2D. null bila tidak ada.</summary>
        public static Texture2D LoadTexture(string webPath)
        {
            var rp = ResourcePath(webPath);
            return rp == null ? null : Resources.Load<Texture2D>(rp);
        }

        /// <summary>Load bytes mentah (GIF/GLB/model) dari asset *.bytes di Resources.</summary>
        public static byte[] LoadBytes(string webPath)
        {
            var rp = ResourcePath(webPath);
            var ta = rp == null ? null : Resources.Load<TextAsset>(rp);
            return ta != null ? ta.bytes : null;
        }

        /// <summary>Load file teks (JSON) dari Resources.</summary>
        public static string LoadText(string webPath)
        {
            var rp = ResourcePath(webPath);
            var ta = rp == null ? null : Resources.Load<TextAsset>(rp);
            return ta != null ? ta.text : null;
        }
    }
}
