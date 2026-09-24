// ParticleBatch.cs — Renderer satu emitter: rebuild mesh dinamis tiap frame
// dari state particle (setara ctx draw loop di web). Verts dihitung dalam
// koordinat dunia (x kanan, y atas = image-up), vertex color untuk tint+alpha.
// Shape: rect (quad), star (fan 8 sisi), heart (fan ~20 sisi), circle (fan),
// image (textured quad).

using System.Collections.Generic;
using UnityEngine;

namespace ArBooth
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class ParticleBatch : MonoBehaviour
    {
        Mesh _mesh;
        MeshRenderer _mr;
        MaterialPropertyBlock _mpb;
        bool _hasTex;
        readonly List<Vector3> _verts = new List<Vector3>(512);
        readonly List<Color32> _cols = new List<Color32>(512);
        readonly List<Vector2> _uvs = new List<Vector2>(512);
        readonly List<int> _tris = new List<int>(1024);

        static readonly Vector2[] HeartPath = BuildHeartPath();
        static readonly Vector2[] StarPath = BuildStarPath();
        const int CircleSegs = 12;

        public MeshRenderer Rend => _mr;

        void Awake()
        {
            _mesh = new Mesh { name = "ParticleBatch" };
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _mr = GetComponent<MeshRenderer>();
            _mpb = new MaterialPropertyBlock();
        }

        /// <summary>Set material & texture (untuk particle bertipe image).</summary>
        public void Setup(Material mat, Texture2D img)
        {
            _mr.sharedMaterial = mat;
            _hasTex = img != null;
            if (_hasTex)
            {
                _mpb.SetTexture("_MainTex", img);
                _mr.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>
        /// Rebuild mesh dari emitter. px→world: (px/W - .5) * vw (x),
        /// (.5 - py/H) * vh (y), z = ZParticle. Ukuran particle dalam px
        /// diskalakan dengan vw/W (world per pixel).
        /// </summary>
        public void Rebuild(Emitter e, float W, float H, BoothStage stage)
        {
            _verts.Clear(); _cols.Clear(); _uvs.Clear(); _tris.Clear();
            if (e.Parts.Count == 0)
            {
                _mesh.Clear();
                return;
            }
            float vw = 2f * BoothStage.CameraDistance * Mathf.Tan(BoothStage.Fov * 0.5f * Mathf.Deg2Rad) * stage.Aspect;
            float vh = vw / stage.Aspect;
            float k = vw / W; // world per pixel (x); pixel y pakai skala sama (square pixel)
            var cfg = e.Cfg;

            foreach (var p in e.Parts)
            {
                float alpha = Emitter.ParticleAlpha(cfg, p);
                if (alpha <= 0.01f) continue;
                float wx = (p.X / W - 0.5f) * vw;
                float wy = (0.5f - p.Y / H) * vh;
                var col = p.Color;
                col.a = (byte)Mathf.RoundToInt(alpha * 255f);
                switch (cfg.Shape)
                {
                    case "rect":
                        AddRotQuad(wx, wy, p.Size * k, p.Size * 0.6f * k, -p.Rot, col);
                        break;
                    case "star":
                        AddFan(wx, wy, StarPath, p.Size * k, -p.Rot * 0.3f, col);
                        break;
                    case "heart":
                        AddFan(wx, wy, HeartPath, p.Size * 0.5f * k,
                            -Mathf.Sin(p.Life * 3 + p.Phase) * 0.3f, col);
                        break;
                    case "image" when _hasTex:
                        AddRotQuad(wx, wy, p.Size * 2f * k, p.Size * 2f * k, -p.Rot, col, textured: true);
                        break;
                    default: // circle — snow & fallback
                        AddCircle(wx, wy, p.Size * 0.5f * k, col);
                        break;
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_cols);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetTriangles(_tris, 0);
        }

        void AddVert(float x, float y, Color32 c, Vector2 uv)
        {
            _verts.Add(new Vector3(x, y, BoothStage.ZParticle));
            _cols.Add(c);
            _uvs.Add(uv);
        }

        /// <summary>Quad berotasi (confetti rect / custom image). w,h = full size.</summary>
        void AddRotQuad(float cx, float cy, float w, float h, float rot, Color32 col, bool textured = false)
        {
            float cos = Mathf.Cos(rot), sin = Mathf.Sin(rot);
            float hx = w / 2f, hy = h / 2f;
            int b = _verts.Count;
            // BL, BR, TR, TL (world-look: y up)
            Vector2[] p = {
                new Vector2(-hx, -hy), new Vector2(hx, -hy),
                new Vector2(hx, hy), new Vector2(-hx, hy),
            };
            Vector2[] uv = {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };
            for (int i = 0; i < 4; i++)
            {
                AddVert(cx + p[i].x * cos - p[i].y * sin,
                        cy + p[i].x * sin + p[i].y * cos,
                        col, textured ? uv[i] : Vector2.zero);
            }
            _tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
        }

        /// <summary>Triangle fan: pusat + ring. path dalam skala unit (dikali size).</summary>
        void AddFan(float cx, float cy, Vector2[] path, float size, float rot, Color32 col)
        {
            float cos = Mathf.Cos(rot), sin = Mathf.Sin(rot);
            int c = _verts.Count;
            AddVert(cx, cy, col, Vector2.zero);
            for (int i = 0; i <= path.Length; i++)
            {
                var pt = path[i % path.Length];
                // path dalam koordinat canvas (y down) → world-look (y up) = negate y
                // (ring jadi clockwise di world → winding tri dibalik agar front face ke kamera)
                float x = pt.x * size, y = -pt.y * size;
                AddVert(cx + x * cos - y * sin, cy + x * sin + y * cos, col, Vector2.zero);
                if (i > 0) _tris.AddRange(new[] { c, c + i, c + i + 1 });
            }
        }

        void AddCircle(float cx, float cy, float r, Color32 col)
        {
            int c = _verts.Count;
            AddVert(cx, cy, col, Vector2.zero);
            for (int i = 0; i <= CircleSegs; i++)
            {
                float a = i * Mathf.PI * 2f / CircleSegs;
                AddVert(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r, col, Vector2.zero);
                if (i > 0) _tris.AddRange(new[] { c, c + i + 1, c + i });
            }
        }

        // --- Shape paths (geometri disalin dari web; koordinat canvas, y down) ---

        static Vector2[] BuildStarPath()
        {
            // Bintang 4 ujung: outer r=1, inner r=0.35 — sama seperti path canvas.
            var p = new Vector2[8];
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI / 2f;
                p[i * 2] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float a2 = a + Mathf.PI / 4f;
                p[i * 2 + 1] = new Vector2(Mathf.Cos(a2) * 0.35f, Mathf.Sin(a2) * 0.35f);
            }
            return p;
        }

        static Vector2[] BuildHeartPath()
        {
            // Path heart web (s = p.size/2 → di sini unit space, skala di caller):
            // moveTo(0, .9) → bezier(-1.4,0, -.7,-1.1, 0,-.35) → bezier(.7,-1.1, 1.4,0, 0,.9)
            var pts = new List<Vector2>();
            var p0 = new Vector2(0, 0.9f);
            pts.AddRange(SampleCubic(p0, new Vector2(-1.4f, 0), new Vector2(-0.7f, -1.1f), new Vector2(0, -0.35f), 10));
            pts.AddRange(SampleCubic(new Vector2(0, -0.35f), new Vector2(0.7f, -1.1f), new Vector2(1.4f, 0), new Vector2(0, 0.9f), 10));
            return pts.ToArray();
        }

        static IEnumerable<Vector2> SampleCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float u = 1 - t;
                yield return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
            }
        }
    }
}
