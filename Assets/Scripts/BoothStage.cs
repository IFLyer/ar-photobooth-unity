// BoothStage.cs — Parameter kamera & pemetaan koordinat normalized → dunia.
// Parity dengan Renderer3D web: PerspectiveCamera fov=40, distance=2,
// bidang overlay di z=0.
//
// CATATAN KONVENSI: Three.js (right-handed) menaruh kamera di +z melihat -z.
// Unity (left-handed) menaruh kamera di -z melihat +z — menghasilkan view yang
// IDENTIK (+x ke kanan, +y ke atas di layar). Transform world disalin verbatim
// dari kode web; satu-satunya penyesuaian: sumbu z dunia berbalik arah
// (viewer di sisi -z), jadi pos.z & komponen x/y euler dinegasikan di
// ThreeToUnity (lihat Renderer3D). Mirroring cermin dilakukan saat menampilkan
// RT ke layar dan saat capture — sama seperti scaleX(-1) di web.

using UnityEngine;

namespace ArBooth
{
    public class BoothStage : MonoBehaviour
    {
        public const float CameraDistance = 2f;
        public const float Fov = 40f;

        // Lapisan z dalam ruang dunia (makin negatif = makin dekat kamera di -2):
        // video (0, opaque) → sticker 2D (-0.10) → particle (-0.20) → model 3D (-0.30).
        public const float ZVideo = 0f;
        public const float ZSticker = -0.10f;
        public const float ZParticle = -0.20f;
        public const float ZModel = -0.30f;

        public Camera Cam;
        public int VideoWidth = 1280;
        public int VideoHeight = 720;

        /// <summary>RenderTexture target berisi komposit video + overlay (ruang kamera asli).</summary>
        public RenderTexture Target { get; private set; }

        float ViewHeight => 2f * CameraDistance * Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
        float ViewWidth => ViewHeight * Aspect;
        public float Aspect => VideoHeight > 0 ? (float)VideoWidth / VideoHeight : 1f;

        /// <summary>Konversi koordinat normalized (0..1, y ke bawah) ke posisi dunia di bidang z=0.</summary>
        public Vector3 WorldFromNorm(float nx, float ny)
            => new Vector3((nx - 0.5f) * ViewWidth, (0.5f - ny) * ViewHeight, 0);

        /// <summary>Lebar wajah dalam satuan dunia.</summary>
        public float FaceWidthWorld(FacePose pose)
            => WorldFromNorm(pose.FaceWidth, 0.5f).x - WorldFromNorm(0, 0.5f).x;

        /// <summary>Samakan resolusi render dengan resolusi video.</summary>
        public void SetVideoSize(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            VideoWidth = w;
            VideoHeight = h;
            if (Target != null) Target.Release();
            Target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4,
                name = "BoothRT",
            };
            Target.Create();
            if (Cam != null)
            {
                Cam.targetTexture = Target;
                Cam.aspect = (float)w / h;
            }
        }

        /// <summary>Konversi euler Three.js (order YXZ, derajat) → rotasi Unity yang
        /// menghasilkan visual identik di layar: conjugate oleh refleksi z —
        /// negate komponen x & y, z tetap, komposisi tetap YXZ.</summary>
        public static Quaternion ThreeToUnityEuler(float exDeg, float eyDeg, float ezDeg)
        {
            return Quaternion.AngleAxis(-eyDeg, Vector3.up)
                 * Quaternion.AngleAxis(-exDeg, Vector3.right)
                 * Quaternion.AngleAxis(ezDeg, Vector3.forward);
        }

        /// <summary>Konversi posisi Three.js → Unity (negate z — viewer di sisi -z).</summary>
        public static Vector3 ThreeToUnityPos(Vector3 p) => new Vector3(p.x, p.y, -p.z);
    }
}
