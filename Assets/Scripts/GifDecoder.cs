// GifDecoder.cs — Decoder animated GIF (GIF89a) → Texture2D[] + delays (detik).
// Setara fungsi decodeGif versi web (ImageDecoder): frame sudah ter-composite
// penuh (disposal & transparency ditangani) sehingga renderer tinggal swap.
// Implementasi: LZW decompress + deinterlace + compositing disposal 0-3.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    public static class GifDecoder
    {
        const float MinDelay = 0.02f; // clamp delay GIF (beberapa file pakai 0)

        /// <summary>Decode GIF dari bytes → AnimSource { frames, delays }.</summary>
        public static AnimSource Decode(byte[] data)
        {
            if (data == null || data.Length < 13)
                throw new Exception("GIF tidak valid");
            var g = new Reader(data);

            string sig = g.Ascii(6);
            if (sig != "GIF87a" && sig != "GIF89a")
                throw new Exception("Bukan file GIF");

            int screenW = g.U16(), screenH = g.U16();
            int packed = g.U8();
            g.Skip(2); // bg color index + aspect
            Color32[] gct = null;
            if ((packed & 0x80) != 0)
                gct = g.ColorTable(3 * (1 << ((packed & 7) + 1)));

            var canvas = new Color32[screenW * screenH];
            var prevCanvas = new Color32[screenW * screenH];
            var frames = new List<SpriteFrame>();
            var delays = new List<float>();

            int disposal = 0, delayCs = 0, transparentIdx = -1;

            while (!g.Eof)
            {
                int tag = g.U8();
                if (tag == 0x3B) break; // trailer
                if (tag == 0x21)
                {
                    int label = g.U8();
                    if (label == 0xF9)
                    {
                        // Graphic Control Extension — berlaku untuk frame berikutnya.
                        // Layout: size(1)=4, packed(1), delay(2), transIdx(1), terminator(1).
                        int sz = g.U8();
                        int pk = g.U8();
                        disposal = (pk >> 2) & 7;
                        delayCs = g.U16();
                        int ti = g.U8(); // transparent color index (selalu ada di block)
                        transparentIdx = (pk & 1) != 0 ? ti : -1;
                        g.Skip(sz - 4); // sisa payload bila size > 4
                        g.U8(); // block terminator
                    }
                    else
                    {
                        g.SkipSubBlocks();
                    }
                    continue;
                }
                if (tag != 0x2C)
                {
                    // byte tak dikenal — hentikan parse (perilaku sama dengan web)
                    break;
                }

                // --- Image Descriptor ---
                int ix = g.U16(), iy = g.U16(), iw = g.U16(), ih = g.U16();
                int ipk = g.U8();
                Color32[] table = gct;
                if ((ipk & 0x80) != 0)
                    table = g.ColorTable(3 * (1 << ((ipk & 7) + 1)));
                if (table == null) throw new Exception("GIF tanpa color table");
                bool interlaced = (ipk & 0x40) != 0;

                int lzwMin = g.U8();
                var compressed = g.ReadSubBlocks();
                var indices = LzwDecode(compressed, lzwMin, iw * ih);

                // Simpan state sebelum composite (untuk disposal=3)
                if (disposal == 3) Array.Copy(canvas, prevCanvas, canvas.Length);

                // --- Composite frame ke canvas ---
                int pixel = 0;
                for (int pass = 0; pass < (interlaced ? 4 : 1); pass++)
                {
                    int row = 0, step = 1;
                    if (interlaced)
                    {
                        if (pass == 0) { row = 0; step = 8; }
                        else if (pass == 1) { row = 4; step = 8; }
                        else if (pass == 2) { row = 2; step = 4; }
                        else { row = 1; step = 2; }
                    }
                    for (; row < ih; row += step)
                    {
                        int baseIdx = (iy + row) * screenW + ix;
                        for (int col = 0; col < iw; col++, pixel++)
                        {
                            if (pixel >= indices.Length) break;
                            int ci = indices[pixel];
                            if (ci == transparentIdx) continue; // transparan → pertahankan canvas
                            if (baseIdx + col >= 0 && baseIdx + col < canvas.Length)
                                canvas[baseIdx + col] = table[ci];
                        }
                    }
                }

                // --- Output frame (copy canvas penuh, flip y untuk Texture2D) ---
                var tex = new Texture2D(screenW, screenH, TextureFormat.RGBA32, false);
                var outPx = new Color32[canvas.Length];
                for (int y = 0; y < screenH; y++)
                    Array.Copy(canvas, y * screenW, outPx, (screenH - 1 - y) * screenW, screenW);
                tex.SetPixels32(outPx);
                tex.Apply(false, false);
                frames.Add(new SpriteFrame { Tex = tex, Uv = new Rect(0, 0, 1, 1) });
                delays.Add(Mathf.Max(delayCs / 100f, MinDelay));

                // --- Terapkan disposal untuk frame berikutnya ---
                if (disposal == 2)
                {
                    for (int y = iy; y < iy + ih; y++)
                        for (int x = ix; x < ix + iw; x++)
                            if (y * screenW + x < canvas.Length)
                                canvas[y * screenW + x] = new Color32(0, 0, 0, 0);
                }
                else if (disposal == 3)
                {
                    Array.Copy(prevCanvas, canvas, canvas.Length);
                }
                disposal = 0;
                transparentIdx = -1;
            }

            if (frames.Count == 0) throw new Exception("GIF tanpa frame");
            return new AnimSource { Frames = frames.ToArray(), Delays = delays.ToArray() };
        }

        /// <summary>LZW decompress: bytes → indeks warna per pixel.</summary>
        static byte[] LzwDecode(byte[] data, int minCodeSize, int expectedPixels)
        {
            int clear = 1 << minCodeSize;
            int eoi = clear + 1;
            var dict = new List<byte[]>(4096);
            byte[] prev = null;
            var output = new byte[expectedPixels + 64];
            int outLen = 0;

            int bitPos = 0;
            int codeSize = minCodeSize + 1;
            int nextCode = eoi + 1;
            ResetDict();

            void ResetDict()
            {
                dict.Clear();
                for (int i = 0; i < clear + 2; i++) dict.Add(new[] { (byte)i });
                codeSize = minCodeSize + 1;
                nextCode = eoi + 1;
                prev = null;
            }

            int ReadCode()
            {
                int bytePos = bitPos >> 3;
                if (bytePos >= data.Length) return -1;
                int bits = data[bytePos];
                if (bytePos + 1 < data.Length) bits |= data[bytePos + 1] << 8;
                if (bytePos + 2 < data.Length) bits |= data[bytePos + 2] << 16;
                int code = (bits >> (bitPos & 7)) & ((1 << codeSize) - 1);
                bitPos += codeSize;
                return code;
            }

            while (true)
            {
                int code = ReadCode();
                if (code < 0 || code == eoi) break;
                if (code == clear) { ResetDict(); continue; }

                byte[] entry;
                if (code < dict.Count)
                {
                    entry = dict[code];
                }
                else if (code == dict.Count && prev != null)
                {
                    entry = new byte[prev.Length + 1];
                    Array.Copy(prev, entry, prev.Length);
                    entry[prev.Length] = prev[0];
                }
                else break; // kode rusak

                foreach (var b in entry)
                    if (outLen < output.Length) output[outLen++] = b;

                if (prev != null)
                {
                    var newEntry = new byte[prev.Length + 1];
                    Array.Copy(prev, newEntry, prev.Length);
                    newEntry[prev.Length] = entry[0];
                    if (dict.Count < 4096) dict.Add(newEntry);
                    if (dict.Count - 1 == (1 << codeSize) - 1 && codeSize < 12)
                        codeSize++;
                }
                prev = entry;
            }

            var result = new byte[Math.Min(outLen, expectedPixels)];
            Array.Copy(output, result, result.Length);
            return result;
        }

        /// <summary>Parse header GIF binary → daftar delay per frame (detik).
        /// Dipakai debug/test — parity dengan parseGifDelays di web.</summary>
        public static List<float> ParseGifDelays(byte[] buf)
        {
            var delays = new List<float>();
            var g = new Reader(buf);
            g.Skip(6); // signature
            g.Skip(7); // Logical Screen Descriptor
            int packed = buf[10];
            if ((packed & 0x80) != 0) g.Skip(3 * (1 << ((packed & 7) + 1))); // GCT
            float delay = 0.1f;
            while (!g.Eof)
            {
                int tag = g.U8();
                if (tag == 0x3B) break;
                if (tag == 0x21)
                {
                    if (g.U8() == 0xF9)
                    {
                        int sz = g.U8();
                        g.Skip(1); // packed
                        delay = Mathf.Max(g.U16() / 100f, MinDelay);
                        g.Skip(sz - 3 + 1); // transparan idx + terminator
                    }
                    else { g.SkipSubBlocks(); }
                }
                else if (tag == 0x2C)
                {
                    int pk = buf[g.Pos + 8];
                    g.Skip(9);
                    if ((pk & 0x80) != 0) g.Skip(3 * (1 << ((pk & 7) + 1)));
                    g.Skip(1); // LZW min code size
                    g.SkipSubBlocks();
                    delays.Add(delay);
                    delay = 0.1f;
                }
                else break;
            }
            return delays;
        }

        // --- Binary reader kecil untuk struktur block GIF ---
        class Reader
        {
            readonly byte[] _d;
            public int Pos;
            public bool Eof => Pos >= _d.Length;
            public Reader(byte[] d) { _d = d; }
            public int U8() => Pos < _d.Length ? _d[Pos++] : 0;
            public int U16() { int v = U8() | (U8() << 8); return v; }
            public string Ascii(int n)
            {
                var s = System.Text.Encoding.ASCII.GetString(_d, Pos, Math.Min(n, _d.Length - Pos));
                Pos += n; return s;
            }
            public void Skip(int n) => Pos = Math.Min(Pos + n, _d.Length);
            public Color32[] ColorTable(int byteLen)
            {
                int n = byteLen / 3;
                var t = new Color32[n];
                for (int i = 0; i < n; i++)
                    t[i] = new Color32((byte)U8(), (byte)U8(), (byte)U8(), 255);
                return t;
            }
            public void SkipSubBlocks()
            {
                while (true)
                {
                    int n = U8();
                    if (n == 0 || Eof) break;
                    Skip(n);
                }
            }
            public byte[] ReadSubBlocks()
            {
                var ms = new List<byte>(4096);
                while (true)
                {
                    int n = U8();
                    if (n == 0 || Eof) break;
                    for (int i = 0; i < n && Pos < _d.Length; i++) ms.Add(_d[Pos++]);
                }
                return ms.ToArray();
            }
        }
    }
}
