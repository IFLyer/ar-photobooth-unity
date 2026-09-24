// BoothSceneBuilder.cs — bangun scene Booth lengkap + material + build settings.
// Dipanggil dari command line:
//   Unity -batchmode -projectPath <proj> -executeMethod ArBooth.Editor.BoothSceneBuilder.Build -quit

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArBooth.Editor
{
    public static class BoothSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/Booth.unity";
        const string MatDir = "Assets/Materials";

        [MenuItem("AR Booth/Build Booth Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // --- Lighting: directional lembut + ambient trilight (parity web). ---
            var lgo = new GameObject("Key Light");
            lgo.transform.rotation = Quaternion.Euler(35f, -20f, 0f);
            var light = lgo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.58f, 0.66f);
            RenderSettings.ambientEquatorColor = new Color(0.38f, 0.38f, 0.44f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.2f, 0.24f);

            // --- Kamera booth: z=-2 melihat +z, fov 40 (parity three.js). ---
            var cgo = new GameObject("BoothCamera");
            cgo.transform.position = new Vector3(0, 0, -BoothStage.CameraDistance);
            var cam = cgo.AddComponent<Camera>();
            cam.fieldOfView = BoothStage.Fov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 10f;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            // --- Materials overlay. ---
            EnsureDir(MatDir);
            var unlit = Shader.Find("ArBooth/Unlit");
            var videoMat = MakeMat("Video", unlit, 2000);
            var stickerMat = MakeMat("Sticker", unlit, 3000);
            var particleMat = MakeMat("Particle", unlit, 3010);

            // --- Root booth + semua komponen. ---
            var root = new GameObject("Booth");
            var stage = root.AddComponent<BoothStage>();
            stage.Cam = cam;
            var camFeed = root.AddComponent<CameraFeed>();
            var tracker = root.AddComponent<FaceTracker>();
            var r2d = root.AddComponent<Renderer2D>();
            r2d.Stage = stage;
            r2d.StickerMaterial = stickerMat;
            r2d.ParticleMaterial = particleMat;
            var r3d = root.AddComponent<Renderer3D>();
            r3d.Stage = stage;
            var ui = root.AddComponent<BoothUI>();
            var capture = root.AddComponent<CaptureManager>();
            capture.Stage = stage;
            var app = root.AddComponent<BoothApp>();
            app.Cam = camFeed;
            app.Tracker = tracker;
            app.Stage = stage;
            app.R2D = r2d;
            app.R3D = r3d;
            app.UI = ui;
            app.Capture = capture;
            ui.Stage = stage;
            ui.App = app;

            // --- Quad video di z=0 (material unlit opaque-ish, queue 2000). ---
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "VideoQuad";
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            var qmr = quad.GetComponent<MeshRenderer>();
            qmr.sharedMaterial = videoMat;
            qmr.shadowCastingMode = ShadowCastingMode.Off;
            quad.transform.position = new Vector3(0, 0, BoothStage.ZVideo);
            app.VideoQuad = qmr;

            // --- Simpan scene + build settings. ---
            EnsureDir("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[BoothSceneBuilder] Scene tersimpan di " + ScenePath);
        }

        static Material MakeMat(string name, Shader shader, int queue)
        {
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            else m.shader = shader;
            m.renderQueue = queue;
            EditorUtility.SetDirty(m);
            return m;
        }

        static void EnsureDir(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parts = path.Split('/');
                var cur = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    var next = cur + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(cur, parts[i]);
                    cur = next;
                }
            }
        }
    }

    /// <summary>Entry point build dari command line (Windows standalone / Android).</summary>
    public static class BoothBuild
    {
        public static void BuildWindows()
        {
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Booth.unity" },
                locationPathName = "Builds/Windows/ARBooth.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var r = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[BoothBuild] Windows result={r.summary.result} errors={r.summary.totalErrors}");
            if (r.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }

        public static void BuildAndroid()
        {
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Booth.unity" },
                locationPathName = "Builds/Android/ARBooth.apk",
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };
            var r = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[BoothBuild] Android result={r.summary.result} errors={r.summary.totalErrors}");
            if (r.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
