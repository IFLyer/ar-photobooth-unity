// Renderer3D.cs — port of js/ar-renderer-3d.js
// Load GLB via glTFast, normalisasi ke 1 unit, clone per wajah, kontrol
// animation clip (loop / reactive / play_once / speed_boost / toggle),
// transisi attach scale-bounce + fade, detach fade-out.
//
// Material: hasil import dikonversi ke URP Lit transparent-copy supaya alpha
// per-instance bisa difade (setara material.transparent + opacity di web).

using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using GLTFast;

namespace ArBooth
{
    public class Renderer3D : MonoBehaviour
    {
        public BoothStage Stage;
        public ArManager Manager;

        class ModelTemplate
        {
            public GameObject Root;      // inactive, sudah dinormalisasi ke 1 unit
            public string[] Clips;
        }

        class FaceInstance
        {
            public GameObject Group;     // group transform (pos/rot/scale per frame)
            public Animation Anim;       // legacy animation pada clone
            public Dictionary<string, AnimationState> States;
            public Material[] Mats;      // material instance untuk fade
            public float[] BaseAlpha;
            public float LastClipT;
            public string LastClip;
        }

        class Trans3D
        {
            public ArItemConfig Item;
            public string Phase = "in";
            public float T0;
            public float BounceSeen = -1f;
            public List<Vector3> LastPos = new List<Vector3>();
            public List<Quaternion> LastRot = new List<Quaternion>();
            public List<float> LastScale = new List<float>();
        }

        readonly Dictionary<string, ModelTemplate> _models = new Dictionary<string, ModelTemplate>();
        readonly HashSet<string> _loading = new HashSet<string>();
        readonly Dictionary<string, List<FaceInstance>> _instances = new Dictionary<string, List<FaceInstance>>();
        readonly Dictionary<string, Trans3D> _transitions = new Dictionary<string, Trans3D>();
        Transform _root;
        Transform _cacheRoot;

        void EnsureInit()
        {
            if (_root == null)
            {
                _root = new GameObject("Renderer3D").transform;
                _root.SetParent(transform, false);
            }
            if (_cacheRoot == null)
            {
                _cacheRoot = new GameObject("ModelCache").transform;
                _cacheRoot.SetParent(transform, false);
                _cacheRoot.gameObject.SetActive(false);
            }
        }

        /// <summary>Preload semua model 3D aktif (dipanggil saat item berubah).</summary>
        public void Preload(IEnumerable<ArItemConfig> items)
        {
            EnsureInit();
            foreach (var item in items)
            {
                if (item.type != "3d") continue;
                if (_models.ContainsKey(item.id) || _loading.Contains(item.id)) continue;
                _loading.Add(item.id);
                LoadAndRegister(item);
            }
        }

        async void LoadAndRegister(ArItemConfig item)
        {
            var tpl = await LoadTemplate(item);
            _loading.Remove(item.id);
            if (tpl != null) _models[item.id] = tpl;
        }

        /// <summary>Load + normalisasi template GLB dari Resources (.bytes).</summary>
        async Task<ModelTemplate> LoadTemplate(ArItemConfig item)
        {
            var bytes = ArAssetCatalog.LoadBytes(item.src);
            if (bytes == null)
            {
                Debug.LogWarning($"[Renderer3D] asset tidak ditemukan: {item.src}");
                return null;
            }
            try
            {
                var gltf = new GltfImport();
                var settings = new ImportSettings { AnimationMethod = AnimationMethod.Legacy };
                bool ok = await gltf.Load(bytes, null, settings);
                if (!ok)
                {
                    Debug.LogWarning($"[Renderer3D] gagal parse GLB: {item.src}");
                    gltf.Dispose();
                    return null;
                }
                var holder = new GameObject($"tpl_{item.id}");
                holder.transform.SetParent(_cacheRoot, false);
                await gltf.InstantiateMainSceneAsync(holder.transform);
                gltf.Dispose();
                if (holder.transform.childCount == 0) { Destroy(holder); return null; }

                // Grup "norm" menampung semua root node glTF (bisa >1) —
                // normalisasi diterapkan ke grup, bukan node individual.
                var inner = new GameObject("norm").transform;
                inner.SetParent(holder.transform, false);
                for (int i = holder.transform.childCount - 1; i >= 0; i--)
                {
                    var c = holder.transform.GetChild(i);
                    if (c != inner) c.SetParent(inner, true);
                }

                // Normalisasi: bbox → maxDim = 1, center di origin (parity web).
                var bounds = CalcBounds(holder);
                float maxDim = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 1e-4f);
                float s = 1f / maxDim;
                inner.localScale = Vector3.one * s;
                inner.localPosition = -bounds.center * s;

                // Kumpulkan nama clip animasi.
                var clips = new List<string>();
                var anim = holder.GetComponentInChildren<Animation>(true);
                if (anim != null)
                    foreach (AnimationState st in anim) clips.Add(st.name);

                holder.SetActive(false);
                return new ModelTemplate { Root = holder, Clips = clips.ToArray() };
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Renderer3D] error load {item.src}: {e.Message}");
                return null;
            }
        }

        static Bounds CalcBounds(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            return b;
        }

        /// <summary>Ambil/buat clone model untuk slot wajah.</summary>
        FaceInstance InstanceAt(ArItemConfig item, int faceIdx)
        {
            if (!_models.TryGetValue(item.id, out var tpl) || tpl == null) return null;
            if (!_instances.TryGetValue(item.id, out var arr))
            {
                arr = new List<FaceInstance>();
                _instances[item.id] = arr;
            }
            while (arr.Count <= faceIdx)
            {
                var clone = Instantiate(tpl.Root, _root);
                clone.name = $"model_{item.id}_{arr.Count}";
                clone.SetActive(true);
                var anim = clone.GetComponentInChildren<Animation>(true);
                var inst = new FaceInstance
                {
                    Group = clone,
                    Anim = anim,
                    States = new Dictionary<string, AnimationState>(),
                };
                if (anim != null)
                    foreach (AnimationState st in anim) inst.States[st.name] = st;
                // Material per-instance → konversi ke URP Lit transparent untuk fade.
                var mats = new List<Material>();
                var alphas = new List<float>();
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    var src = r.sharedMaterials;
                    var dst = new Material[src.Length];
                    for (int i = 0; i < src.Length; i++)
                    {
                        dst[i] = ToFadeable(src[i]);
                        mats.Add(dst[i]);
                        alphas.Add(dst[i].HasProperty("_BaseColor") ? dst[i].GetColor("_BaseColor").a : 1f);
                    }
                    r.sharedMaterials = dst;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                inst.Mats = mats.ToArray();
                inst.BaseAlpha = alphas.ToArray();
                ApplyInitialAnim(item, inst);
                arr.Add(inst);
            }
            return arr[faceIdx];
        }

        /// <summary>Konversi material import → URP Lit transparent (fadeable).</summary>
        static Material ToFadeable(Material src)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (src != null)
            {
                if (src.HasProperty("_BaseColor")) m.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                else if (src.HasProperty("_Color")) m.SetColor("_BaseColor", src.GetColor("_Color"));
                var tex = src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap")
                    : src.HasProperty("_BaseColorTexture") ? src.GetTexture("_BaseColorTexture")
                    : src.mainTexture;
                if (tex != null) m.SetTexture("_BaseMap", tex);
                if (src.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
            }
            // Transparent + cull off + queue di atas 2D.
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.renderQueue = 3020;
            return m;
        }

        /// <summary>State animasi awal: loop → play looping; reactive → freeze frame 0.</summary>
        void ApplyInitialAnim(ArItemConfig item, FaceInstance inst)
        {
            if (inst.Anim == null || inst.States.Count == 0) return;
            string clip = PickClip(item, inst);
            if (clip == null) return;
            inst.LastClip = clip;
            var st = inst.States[clip];
            var anim = item.Anim;
            bool reactive = anim != null && anim.Type == "reactive";
            if (reactive)
            {
                st.wrapMode = WrapMode.ClampForever;
                st.time = 0;
                st.speed = 0;
                inst.Anim.Play(clip);
            }
            else
            {
                st.wrapMode = WrapMode.Loop;
                st.speed = anim != null && anim.Speed != 0 ? anim.Speed : 1f;
                inst.Anim.Play(clip);
            }
        }

        /// <summary>Pilih clip: pakai nama dari config bila ada, else clip pertama.</summary>
        static string PickClip(ArItemConfig item, FaceInstance inst)
        {
            var anim = item.Anim;
            if (anim != null && !string.IsNullOrEmpty(anim.ClipName) && inst.States.ContainsKey(anim.ClipName))
                return anim.ClipName;
            foreach (var k in inst.States.Keys) return k;
            return null;
        }

        /// <summary>Update semua item 3D aktif: transform per wajah + transisi + animasi.</summary>
        public void Render(List<FacePose> poses, List<ArItemConfig> activeItems)
        {
            EnsureInit();
            float now = BoothApp.Ticker.ElapsedTime;
            float dt = BoothApp.Ticker.DeltaTime;
            var activeIds = new HashSet<string>();

            foreach (var item in activeItems)
            {
                if (item.type != "3d") continue;
                activeIds.Add(item.id);
                Preload(new[] { item });
                var rt = item.Runtime;
                if (!_transitions.TryGetValue(item.id, out var tr) || tr.Phase == "out")
                {
                    tr = new Trans3D { Item = item, Phase = "in", T0 = now, BounceSeen = -1f };
                    _transitions[item.id] = tr;
                }
                if (rt != null && rt.BounceAt != tr.BounceSeen)
                {
                    tr.BounceSeen = rt.BounceAt;
                    if (rt.BounceAt > 0) { tr.Phase = "in"; tr.T0 = rt.BounceAt; }
                }
                float p = AnimEase.Clamp01((now - tr.T0) / Transition.Attach);
                float alpha = p, scaleMul = AnimEase.EaseOutBack(p);
                DrawAll(item, poses, tr, alpha, scaleMul, now, dt);
            }

            var done = new List<string>();
            foreach (var kv in _transitions)
            {
                string id = kv.Key;
                var tr = kv.Value;
                if (activeIds.Contains(id)) continue;
                if (tr.Phase == "in") { tr.Phase = "out"; tr.T0 = now; }
                float p = AnimEase.Clamp01((now - tr.T0) / Transition.Detach);
                if (p >= 1f)
                {
                    HideInstances(id);
                    done.Add(id);
                    continue;
                }
                DrawAll(tr.Item, poses, tr, 1f - p, 1f, now, dt);
            }
            foreach (var id in done) _transitions.Remove(id);
        }

        void HideInstances(string itemId)
        {
            if (_instances.TryGetValue(itemId, out var arr))
                foreach (var i in arr) if (i.Group != null) i.Group.SetActive(false);
        }

        void DrawAll(ArItemConfig item, List<FacePose> poses, Trans3D tr,
                     float alpha, float scaleMul, float now, float dt)
        {
            for (int i = 0; i < poses.Count; i++)
            {
                var inst = InstanceAt(item, i);
                if (inst == null) continue;
                PoseInstance(item, inst, poses[i], alpha, scaleMul, i, tr);
                UpdateAnim(item, inst, now, dt);
            }
            if (_instances.TryGetValue(item.id, out var arr))
            {
                for (int i = poses.Count; i < arr.Count; i++)
                    if (arr[i].Group != null) arr[i].Group.SetActive(false);
                if (poses.Count > 0)
                {
                    if (tr.LastPos.Count > poses.Count)
                    {
                        tr.LastPos.RemoveRange(poses.Count, tr.LastPos.Count - poses.Count);
                        tr.LastRot.RemoveRange(poses.Count, tr.LastRot.Count - poses.Count);
                        tr.LastScale.RemoveRange(poses.Count, tr.LastScale.Count - poses.Count);
                    }
                }
                else
                {
                    // Tanpa wajah: pakai posisi terakhir (parity fade-out web).
                    for (int i = 0; i < arr.Count && i < tr.LastPos.Count; i++)
                    {
                        var inst = arr[i];
                        inst.Group.SetActive(true);
                        inst.Group.transform.position = tr.LastPos[i];
                        inst.Group.transform.rotation = tr.LastRot[i];
                        inst.Group.transform.localScale = Vector3.one * tr.LastScale[i];
                        SetAlpha(inst, alpha);
                    }
                }
            }
        }

        /// <summary>Transform world dari pose — parity ar-renderer-3d.js.</summary>
        void PoseInstance(ArItemConfig item, FaceInstance inst, FacePose pose,
                          float alpha, float scaleMul, int faceIdx, Trans3D tr)
        {
            var anchor = ParticleEngine.GetAnchor(pose, item.anchor);
            var off = item.offset;
            var rot = item.rotation;
            float fwW = Stage.FaceWidthWorld(pose);

            var pos = Stage.WorldFromNorm(anchor.x, anchor.y);
            pos.x += (off?.x ?? 0) * fwW;
            pos.y -= (off?.y ?? 0) * fwW;
            // three: pos.z += off.z*fw (viewer +z); unity viewer di -z → negate, base ZModel.
            pos.z = BoothStage.ZModel - (off?.z ?? 0) * fwW;

            // three euler YXZ: x=-pitch+rotX, y=yaw+rotY, z=-roll+rotZ (rad+deg)
            var q = BoothStage.ThreeToUnityEuler(
                -pose.Pitch * Mathf.Rad2Deg + (rot?.x ?? 0),
                pose.Yaw * Mathf.Rad2Deg + (rot?.y ?? 0),
                -pose.Roll * Mathf.Rad2Deg + (rot?.z ?? 0));

            float s = fwW * (item.scale != 0 ? item.scale : 1f) * scaleMul;

            inst.Group.SetActive(true);
            inst.Group.transform.position = pos;
            inst.Group.transform.rotation = q;
            inst.Group.transform.localScale = Vector3.one * s;
            SetAlpha(inst, alpha);

            while (tr.LastPos.Count <= faceIdx)
            {
                tr.LastPos.Add(pos); tr.LastRot.Add(q); tr.LastScale.Add(s);
            }
            tr.LastPos[faceIdx] = pos; tr.LastRot[faceIdx] = q; tr.LastScale[faceIdx] = s;
        }

        /// <summary>Set alpha semua material instance (fade in/out).</summary>
        static void SetAlpha(FaceInstance inst, float alpha)
        {
            for (int i = 0; i < inst.Mats.Length; i++)
            {
                var c = inst.Mats[i].GetColor("_BaseColor");
                c.a = inst.BaseAlpha[i] * alpha;
                inst.Mats[i].SetColor("_BaseColor", c);
            }
        }

        /// <summary>Update animasi per frame: play_once (ClampForever → resume),
        /// speed_boost, toggle pause.</summary>
        void UpdateAnim(ArItemConfig item, FaceInstance inst, float now, float dt)
        {
            if (inst.Anim == null || inst.States.Count == 0) return;
            string clip = inst.LastClip ?? PickClip(item, inst);
            if (clip == null) return;
            var st = inst.States[clip];
            var anim = item.Anim;
            var rt = item.Runtime;
            if (anim == null) return;

            float baseSpeed = anim.Speed != 0 ? anim.Speed : 1f;
            bool reactive = anim.Type == "reactive";

            if (reactive)
            {
                // play_once: mulai dari awal, ClampForever, speed penuh.
                if (rt != null && rt.PlayOnceAt > 0 && rt.PlayOnceAt != inst.LastClipT)
                {
                    inst.LastClipT = rt.PlayOnceAt;
                    st.time = 0;
                    st.wrapMode = WrapMode.ClampForever;
                    st.speed = baseSpeed;
                    inst.Anim.Play(clip);
                }
                // Setelah selesai satu siklus: resume loop kalau playing, else freeze.
                if (st.wrapMode == WrapMode.ClampForever && st.time >= st.length)
                {
                    if (rt?.Playing ?? false)
                    {
                        st.wrapMode = WrapMode.Loop;
                        st.speed = baseSpeed;
                    }
                    else
                    {
                        st.speed = 0;
                        st.time = st.length;
                    }
                }
                if (st.wrapMode == WrapMode.Loop)
                    st.speed = (rt?.Playing ?? false) ? baseSpeed * (rt?.SpeedMul ?? 1f) : 0;
            }
            else
            {
                st.wrapMode = WrapMode.Loop;
                bool playing = rt?.Playing ?? true;
                st.speed = playing ? baseSpeed * (rt?.SpeedMul ?? 1f) : 0;
            }
        }
    }
}
