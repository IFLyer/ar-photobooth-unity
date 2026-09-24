// CameraFeed.cs — port of js/camera.js
// Akses kamera via WebCamTexture (setara getUserMedia), stream management.
// Mirror preview ditangani di layer tampilan (flip RT), bukan di sini —
// landmark & overlay tetap di ruang kamera asli, sama seperti versi web.

using System.Collections;
using UnityEngine;

namespace ArBooth
{
    public class CameraFeed : MonoBehaviour
    {
        [Tooltip("Resolusi ideal kamera (getUserMedia ideal 1920×1080)")]
        public int RequestedWidth = 1920;
        public int RequestedHeight = 1080;
        public int RequestedFps = 30;

        public WebCamTexture Texture { get; private set; }
        public bool IsRunning => Texture != null && Texture.isPlaying;
        public int VideoWidth => Texture != null ? Texture.width : 0;
        public int VideoHeight => Texture != null ? Texture.height : 0;

        /// <summary>Pesan error terakhir saat start gagal (null bila sukses).</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Nyalakan kamera depan (facingMode "user"). Stream lama selalu
        /// dihentikan dulu — mencegah leak track hardware.
        /// Kegagalan dilaporkan via LastError (bukan throw) supaya coroutine
        /// caller tidak mati.
        /// </summary>
        public IEnumerator StartCamera()
        {
            LastError = null;
            StopCamera();
            if (WebCamTexture.devices.Length == 0)
            {
                LastError = "Tidak ada perangkat kamera.";
                yield break;
            }

            // Pilih kamera depan bila ada (facingMode "user" di web).
            string device = WebCamTexture.devices[0].name;
            foreach (var d in WebCamTexture.devices)
                if (d.isFrontFacing) { device = d.name; break; }

            Texture = new WebCamTexture(device, RequestedWidth, RequestedHeight, RequestedFps);
            Texture.Play();
            // Tunggu metadata stream siap (setara loadedmetadata di web) dengan timeout.
            float t0 = Time.realtimeSinceStartup;
            yield return new WaitUntil(() =>
                Texture == null || Texture.width > 16 ||
                Time.realtimeSinceStartup - t0 > 8f);
            if (Texture == null || Texture.width <= 16)
            {
                LastError = "Kamera gagal merespons.";
                StopCamera();
            }
        }

        /// <summary>Hentikan kamera dan lepas texture.</summary>
        public void StopCamera()
        {
            if (Texture != null)
            {
                Texture.Stop();
                Destroy(Texture);
                Texture = null;
            }
        }

        void OnDestroy() => StopCamera();
    }
}
