# AR Mirror Booth — Unity Port

Port penuh project web **AR Mirror Booth** (`../ar-photobooth`) ke Unity.
Seluruh fitur dipertahankan: live camera mirror, face tracking multi-wajah
(maks 5), overlay 2D (PNG/sprite sheet/GIF), overlay 3D GLB beranimasi,
particle effect, expression-reactive trigger, transisi attach/detach,
countdown capture, composite + watermark, preview/retake/simpan.

- **Unity**: 6000.6.0f1 (URP 17.6, Input System)
- **Target**: Windows standalone (x64) & Android
- **Face tracking**: MediaPipe FaceLandmarker (plugin `com.github.homuler.mediapipe` 0.16.3, LIVE_STREAM, ~33 ms cadence)
- **GLB runtime**: glTFast (`com.unity.cloud.gltfast` 6.20.0, AnimationMethod.Legacy)
- **Asset**: dibundel di `Assets/Resources` (offline — tanpa server)

## Struktur

```
Assets/Scripts/
  BoothApp.cs          orchestrator (port app.js): boot, loop, status, capture
  CameraFeed.cs        WebCamTexture (port camera.js)
  FaceTracker.cs       FaceLandmarker LIVE_STREAM (port face-tracker.js)
  FacePose.cs          computeFacePose + PoseSmoother EMA multi-face
  ExpressionDetector.cs blink/mouth_open/smile/raise_brows + hysteresis
  ArConfig.cs          model JSON config (schema identik versi web)
  ArAssetCatalog.cs    mapping path web → Resources (.gif/.glb → .bytes)
  ArManager.cs         state aktif, category exclusivity, trigger (port ar-manager.js)
  Renderer2D.cs        quad sticker + transisi + anim clock (port ar-renderer-2d.js)
  Renderer3D.cs        GLB clone per wajah, animasi clip (port ar-renderer-3d.js)
  ParticleEngine.cs    simulasi emitter/preset (port particle-engine.js)
  ParticleBatch.cs     mesh dinamis per emitter (render)
  SpriteDecoder.cs     slice sprite sheet → UV frames
  GifDecoder.cs        decoder GIF89a + LZW
  CaptureManager.cs    RT readback + mirror + watermark + PNG (port capture.js)
  BoothUI.cs           selector/countdown/preview/status (port ui.js)
  BoothStage.cs        pemetaan normalized→world, konvensi kamera, RT
  BoothDebug.cs        API debug (port window.__arDebug)
  AnimationTicker.cs   clock animasi global + easing
Assets/Editor/BoothSceneBuilder.cs  builder scene + entry point build CLI
Assets/Tests/Editor/BoothPortTests.cs  EditMode tests (34 test)
Assets/Resources/
  ar/config.json       config asli (tidak diubah)
  ar/2d/*.png|gif.bytes, ar/3d/*.glb.bytes, ar/thumbnails/*.png
  branding/logo|watermark.png
  mediapipe/face_landmarker.bytes
```

## Konvensi koordinat (penting)

- Three.js (RH) kamera di +z melihat -z; Unity (LH) kamera di -z melihat +z —
  visual identik. Semua transform disalin verbatim; satu-satunya penyesuaian:
  pos.z dinegasikan dan euler x/y dinegasikan (`BoothStage.ThreeToUnityEuler`).
- Mirroring (efek cermin) terjadi saat menampilkan RenderTexture ke layar
  (`scale.x=-1`) dan saat capture (flip piksel) — sama seperti `scaleX(-1)`
  CSS di web. Landmark tetap di ruang kamera asli.
- `videoRotationAngle` (Android): quad video diputar & landmark di-unrotate.

## Build

```bash
# Build scene (sekali, atau setelah ubah scene builder)
Unity -batchmode -projectPath . -executeMethod ArBooth.Editor.BoothSceneBuilder.Build -quit

# Windows
Unity -batchmode -projectPath . -buildTarget Win64 \
  -executeMethod ArBooth.Editor.BoothBuild.BuildWindows -quit

# Android (butuh Android SDK/NDK + permission CAMERA di device)
Unity -batchmode -projectPath . -buildTarget Android \
  -executeMethod ArBooth.Editor.BoothBuild.BuildAndroid -quit
```

Output: `Builds/Windows/ARBooth.exe`, `Builds/Android/ARBooth.apk`.

## Test

```bash
Unity -batchmode -projectPath . -runTests -testPlatform EditMode \
  -testResults results.xml
```

## Catatan

- Foto tersimpan di `persistentDataPath/Captures` (Windows/Editor) atau
  `Pictures/AR-Booth` + MediaStore scan (Android).
- Fullscreen toggle tersedia (Windows). Android fullscreen immersive
  mengikuti setelan default Unity.
- Item AR dikonfigurasi lewat `Assets/Resources/ar/config.json` — schema
  identik dengan versi web, operator ganti asset tanpa ubah kode.
