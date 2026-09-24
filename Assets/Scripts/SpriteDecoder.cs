// SpriteDecoder.cs — port of js/sprite-decoder.js (bagian sprite sheet).
// GIF decode ada di GifDecoder.cs.
// Perbedaan vs web: frame sprite sheet direpresentasikan sebagai UV rect ke
// satu Texture2D (bukan ImageBitmap per frame) — setara secara visual & lebih
// hemat memori; akumulasi budget memori tetap dihitung per frame di ARManager.

using UnityEngine;

namespace ArBooth
{
    public static class SpriteDecoder
    {
        /// <summary>
        /// Slice sprite sheet menjadi array SpriteFrame mengikuti grid cols×rows.
        /// frameCount default cols×rows; frame kosong di grid terakhir di-skip.
        /// </summary>
        public static AnimSource DecodeSheet(Texture2D tex, int cols, int rows, int frameCount)
        {
            if (tex == null || cols <= 0 || rows <= 0)
                return new AnimSource { Frames = new SpriteFrame[0], Delays = null };
            int total = frameCount > 0 ? frameCount : cols * rows;
            total = Mathf.Min(total, cols * rows);
            float fw = 1f / cols, fh = 1f / rows;
            var frames = new SpriteFrame[total];
            for (int i = 0; i < total; i++)
            {
                int col = i % cols;
                int row = i / cols;
                // UV Unity y=0 di bawah — row 0 (atas gambar) → y = 1 - (row+1)*fh.
                frames[i] = new SpriteFrame
                {
                    Tex = tex,
                    Uv = new Rect(col * fw, 1f - (row + 1) * fh, fw, fh),
                };
            }
            return new AnimSource { Frames = frames, Delays = null };
        }
    }
}
