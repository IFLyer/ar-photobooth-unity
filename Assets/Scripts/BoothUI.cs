// BoothUI.cs — port of js/ui.js
// Seluruh UI dibangun runtime (uGUI): live view mirror, branding, status
// indicator + FPS, selector thumbnail (toggle + active state + badge animasi),
// shutter + countdown 3-2-1 + flash, preview modal (retake/simpan/tutup),
// fullscreen button, loading & error banner.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ArBooth
{
    public class BoothUI : MonoBehaviour
    {
        public BoothStage Stage;
        public BoothApp App;

        static readonly Color Bg = new Color(0.06f, 0.06f, 0.09f);
        static readonly Color Accent = new Color(1f, 0.83f, 0.4f);
        static readonly Color OkGreen = new Color(0.35f, 0.9f, 0.5f);
        static readonly Color WarnYellow = new Color(1f, 0.83f, 0.4f);
        static readonly Color PanelBg = new Color(0f, 0f, 0f, 0.75f);

        Canvas _canvas;
        RawImage _live;
        Text _statusText;
        Image _statusDot;
        Text _countdown;
        Image _flash;
        GameObject _loading;
        Text _loadingText;
        GameObject _error;
        Text _errorText;
        GameObject _preview;
        RawImage _previewImg;
        Button _shutter;
        readonly Dictionary<string, Image> _itemBorders = new Dictionary<string, Image>();
        Coroutine _toast;
        bool _capturing;
        Texture2D _lastPhoto;

        Font _font;
        Sprite _knob, _bg;

        void Awake()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _knob = Resources.GetBuiltinResource<Sprite>("UI/Skin/Knob.psd");
            _bg = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            Build();
        }

        // ---------- konstruksi UI ----------

        void Build()
        {
            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<InputSystemUIInputModule>();
            }
            var cgo = new GameObject("BoothCanvas");
            cgo.transform.SetParent(transform, false);
            _canvas = cgo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = cgo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            cgo.AddComponent<GraphicRaycaster>();
            var rt = (RectTransform)cgo.transform;

            // Live view (mirror via scale.x = -1, cover-crop via EnvelopeParent).
            var live = NewGO("LiveView", rt);
            Stretch(live);
            _live = live.AddComponent<RawImage>();
            var fit = live.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            live.transform.localScale = new Vector3(-1, 1, 1);

            BuildBrand(rt);
            BuildStatus(rt);
            BuildSelectorBar(rt);
            BuildShutter(rt);
            BuildFullscreenBtn(rt);
            BuildCountdown(rt);
            BuildFlash(rt);
            BuildPreview(rt);
            BuildError(rt);
            BuildLoading(rt);
        }

        GameObject NewGO(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static void Stretch(GameObject go)
        {
            var r = (RectTransform)go.transform;
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        static void Anchor(GameObject go, Vector2 min, Vector2 max, Vector2 pos, Vector2 size)
        {
            var r = (RectTransform)go.transform;
            r.anchorMin = min;
            r.anchorMax = max;
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = pos;
            r.sizeDelta = size;
        }

        Text MakeText(Transform parent, string name, int size, TextAnchor align, FontStyle style = FontStyle.Normal)
        {
            var go = NewGO(name, parent);
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.alignment = align;
            t.fontStyle = style;
            t.color = Color.white;
            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.6f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
            return t;
        }

        Image MakeImage(GameObject go, Sprite sprite, Color col)
        {
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = col;
            return img;
        }

        Button MakeButton(GameObject go)
        {
            var b = go.AddComponent<Button>();
            var c = b.colors;
            c.highlightedColor = new Color(1f, 1f, 1f, 0.85f);
            c.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            b.colors = c;
            return b;
        }

        void BuildBrand(RectTransform root)
        {
            var bar = NewGO("Brand", root);
            Anchor(bar, new Vector2(0, 1), new Vector2(0, 1), new Vector2(150, -44), new Vector2(280, 64));
            var logo = NewGO("Logo", bar.transform);
            Anchor(logo, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(-110, 0), new Vector2(56, 56));
            var logoImg = MakeImage(logo, null, Color.white);
            logoImg.preserveAspect = true;
            var ltex = ArAssetCatalog.LoadTexture("assets/branding/logo.png");
            if (ltex != null)
                logoImg.sprite = Sprite.Create(ltex,
                    new Rect(0, 0, ltex.width, ltex.height), new Vector2(0.5f, 0.5f));
            var name = MakeText(bar.transform, "Name", 30, TextAnchor.MiddleLeft, FontStyle.Bold);
            var nr = (RectTransform)name.transform;
            nr.anchorMin = new Vector2(0, 0); nr.anchorMax = new Vector2(1, 1);
            nr.offsetMin = new Vector2(-60, 0); nr.offsetMax = Vector2.zero;
        }

        void BuildStatus(RectTransform root)
        {
            var pill = NewGO("StatusPill", root);
            Anchor(pill, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -40), new Vector2(420, 44));
            var bg = MakeImage(pill, _bg, new Color(0, 0, 0, 0.55f));
            bg.type = Image.Type.Sliced;
            var dot = NewGO("Dot", pill.transform);
            Anchor(dot, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(28, 0), new Vector2(14, 14));
            _statusDot = MakeImage(dot, _knob, WarnYellow);
            _statusText = MakeText(pill.transform, "Text", 22, TextAnchor.MiddleCenter);
            var tr = (RectTransform)_statusText.transform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(50, 0); tr.offsetMax = new Vector2(-10, 0);
            _statusText.text = "Memuat…";
        }

        void BuildSelectorBar(RectTransform root)
        {
            var holder = NewGO("Selector", root);
            Anchor(holder, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 150));
            var scroll = holder.AddComponent<ScrollRect>();
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.scrollSensitivity = 30;
            var vp = NewGO("Viewport", holder.transform);
            Stretch(vp);
            var mask = vp.AddComponent<RectMask2D>();
            scroll.viewport = (RectTransform)vp.transform;
            var content = NewGO("Content", vp.transform);
            var cr = (RectTransform)content.transform;
            cr.anchorMin = new Vector2(0, 0.5f);
            cr.anchorMax = new Vector2(0, 0.5f);
            cr.pivot = new Vector2(0, 0.5f);
            var hlg = content.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 14;
            hlg.padding = new RectOffset(20, 20, 14, 14);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
            var csf = content.AddComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = cr;
            scroll.movementType = ScrollRect.MovementType.Clamped;
        }

        /// <summary>Bangun tombol item di selector (dipanggil setelah config termuat).</summary>
        public void BuildSelector(List<ArItemConfig> items, BrandingJson branding)
        {
            var content = _canvas.transform.Find("Selector/Viewport/Content");
            if (content == null) return;
            foreach (var item in items)
            {
                var btn = NewGO("item_" + item.id, content);
                var le = btn.AddComponent<LayoutElement>();
                le.preferredWidth = 116; le.preferredHeight = 122;
                var border = MakeImage(btn, _bg, new Color(1, 1, 1, 0.14f));
                border.type = Image.Type.Sliced;
                _itemBorders[item.id] = border;

                var thumb = NewGO("Thumb", btn.transform);
                Anchor(thumb, new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                    new Vector2(0, -12), new Vector2(84, 84));
                var timg = MakeImage(thumb, null, Color.white);
                timg.preserveAspect = true;
                var tt = ArAssetCatalog.LoadTexture(
                    !string.IsNullOrEmpty(item.thumbnail) ? item.thumbnail : item.src);
                if (tt != null)
                    timg.sprite = Sprite.Create(tt,
                        new Rect(0, 0, tt.width, tt.height), new Vector2(0.5f, 0.5f));

                var label = MakeText(btn.transform, "Label", 17, TextAnchor.MiddleCenter);
                label.text = item.name ?? item.id;
                var lr = (RectTransform)label.transform;
                lr.anchorMin = new Vector2(0, 0); lr.anchorMax = new Vector2(1, 0);
                lr.pivot = new Vector2(0.5f, 0);
                lr.anchoredPosition = new Vector2(0, 8);
                lr.sizeDelta = new Vector2(0, 26);
                label.color = new Color(1, 1, 1, 0.85f);

                // Badge titik emas untuk item beranimasi (parity .animated di CSS).
                if (item.animation != null)
                {
                    var badge = NewGO("Badge", btn.transform);
                    Anchor(badge, new Vector2(1, 1), new Vector2(1, 1),
                        new Vector2(-12, -12), new Vector2(14, 14));
                    MakeImage(badge, _knob, Accent);
                }

                string id = item.id;
                MakeButton(btn).onClick.AddListener(() => App.Manager.Toggle(id));
            }
            // Terapkan branding: judul = name dari config.
            var nameText = _canvas.transform.Find("Brand/Name");
            if (nameText != null && branding != null && !string.IsNullOrEmpty(branding.name))
                nameText.GetComponent<Text>().text = branding.name;
        }

        /// <summary>Refresh highlight aktif semua tombol item.</summary>
        public void RefreshActive(ArManager mgr)
        {
            foreach (var kv in _itemBorders)
                kv.Value.color = mgr.IsActive(kv.Key)
                    ? Accent
                    : new Color(1, 1, 1, 0.14f);
        }

        void BuildShutter(RectTransform root)
        {
            var go = NewGO("Shutter", root);
            Anchor(go, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 190), new Vector2(96, 96));
            var ring = MakeImage(go, _knob, Color.white);
            ring.type = Image.Type.Simple;
            var inner = NewGO("Inner", go.transform);
            Anchor(inner, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(72, 72));
            MakeImage(inner, _knob, new Color(0.95f, 0.95f, 0.95f));
            _shutter = MakeButton(go);
            _shutter.onClick.AddListener(() => App.OnShutter());
        }

        void BuildFullscreenBtn(RectTransform root)
        {
            var go = NewGO("Fullscreen", root);
            Anchor(go, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-60, -44), new Vector2(52, 52));
            var bg = MakeImage(go, _bg, new Color(0, 0, 0, 0.55f));
            bg.type = Image.Type.Sliced;
            var label = MakeText(go.transform, "Icon", 26, TextAnchor.MiddleCenter, FontStyle.Bold);
            label.text = "⛶";
            var lr = (RectTransform)label.transform;
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
            MakeButton(go).onClick.AddListener(() => App.ToggleFullscreen());
        }

        void BuildCountdown(RectTransform root)
        {
            var go = NewGO("Countdown", root);
            Stretch(go);
            _countdown = MakeText(go.transform, "Num", 260, TextAnchor.MiddleCenter, FontStyle.Bold);
            var r = (RectTransform)_countdown.transform;
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
            go.SetActive(false);
        }

        void BuildFlash(RectTransform root)
        {
            var go = NewGO("Flash", root);
            Stretch(go);
            _flash = MakeImage(go, null, new Color(1, 1, 1, 0));
            _flash.raycastTarget = false;
        }

        void BuildPreview(RectTransform root)
        {
            _preview = NewGO("Preview", root);
            Stretch(_preview);
            var bg = MakeImage(_preview, null, PanelBg);
            var imgGo = NewGO("Photo", _preview.transform);
            Anchor(imgGo, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 40), new Vector2(1280, 720));
            _previewImg = imgGo.AddComponent<RawImage>();
            var fit = imgGo.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            var row = NewGO("Buttons", _preview.transform);
            Anchor(row, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 90), new Vector2(760, 64));
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 24;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
            MakePanelButton(row.transform, "Ambil Ulang", () => App.OnRetake());
            MakePanelButton(row.transform, "Simpan Foto", () => App.OnSave(_lastPhoto));
            MakePanelButton(row.transform, "Selesai", () => HidePreview());
            _preview.SetActive(false);
        }

        Button MakePanelButton(Transform parent, string label, UnityEngine.Events.UnityAction act)
        {
            var go = NewGO("btn_" + label, parent);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 220; le.preferredHeight = 64;
            var bg = MakeImage(go, _bg, new Color(1, 1, 1, 0.18f));
            bg.type = Image.Type.Sliced;
            var t = MakeText(go.transform, "T", 26, TextAnchor.MiddleCenter);
            t.text = label;
            var tr = (RectTransform)t.transform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = tr.offsetMax = Vector2.zero;
            var b = MakeButton(go);
            b.onClick.AddListener(act);
            return b;
        }

        void BuildError(RectTransform root)
        {
            _error = NewGO("ErrorBanner", root);
            Anchor(_error, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -10), new Vector2(0, 56));
            MakeImage(_error, null, new Color(0.8f, 0.15f, 0.15f, 0.92f));
            _errorText = MakeText(_error.transform, "T", 22, TextAnchor.MiddleCenter);
            var tr = (RectTransform)_errorText.transform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = tr.offsetMax = Vector2.zero;
            _error.SetActive(false);
        }

        void BuildLoading(RectTransform root)
        {
            _loading = NewGO("Loading", root);
            Stretch(_loading);
            MakeImage(_loading, null, Bg);
            _loadingText = MakeText(_loading.transform, "T", 30, TextAnchor.MiddleCenter);
            _loadingText.text = "Menyiapkan booth…";
            var tr = (RectTransform)_loadingText.transform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = tr.offsetMax = Vector2.zero;
        }

        // ---------- API untuk BoothApp ----------

        /// <summary>Update indikator status: "ok" (wajah ada) / "warn" (mencari).</summary>
        public void SetStatus(string state, string text)
        {
            if (_statusText == null || _toast != null) return;
            _statusText.text = text;
            _statusDot.color = state == "ok" ? OkGreen
                : state == "warn" ? WarnYellow
                : new Color(1, 1, 1, 0.4f);
        }

        /// <summary>Sembunyikan overlay loading setelah kamera+tracker siap.</summary>
        public void HideStatus()
        {
            if (_loading != null) _loading.SetActive(false);
        }

        /// <summary>Pesan status/toast sementara atau sticky.</summary>
        public void ShowStatus(string msg, bool autoHide = false)
        {
            if (_statusText == null) return;
            if (_toast != null) StopCoroutine(_toast);
            _statusText.text = msg;
            if (autoHide) _toast = StartCoroutine(ToastReset(msg));
        }

        IEnumerator ToastReset(string msg)
        {
            yield return new WaitForSeconds(3f);
            _toast = null;
            if (_statusText != null && _statusText.text == msg)
                _statusText.text = "Mencari wajah…";
        }

        public void ShowError(string msg)
        {
            if (_error == null) return;
            _errorText.text = msg;
            _error.SetActive(true);
        }

        /// <summary>Countdown 3-2-1 → flash → capture → preview (parity onCapture).</summary>
        public IEnumerator CountdownAndCapture(CaptureManager capture)
        {
            if (_capturing) yield break;
            _capturing = true;
            if (_shutter != null) _shutter.interactable = false;
            try
            {
                var cd = _countdown.transform.parent.gameObject;
                for (int n = 3; n >= 1; n--)
                {
                    cd.SetActive(true);
                    _countdown.text = n.ToString();
                    _countdown.transform.localScale = Vector3.one * 1.4f;
                    float t0 = Time.unscaledTime;
                    while (Time.unscaledTime - t0 < 1f)
                    {
                        _countdown.transform.localScale =
                            Vector3.one * Mathf.Lerp(1.4f, 1f, (Time.unscaledTime - t0) / 0.35f);
                        yield return null;
                    }
                }
                cd.SetActive(false);

                // Flash putih singkat sebagai feedback shutter.
                StartCoroutine(FlashOnce());
                // Capture tepat setelah frame ini selesai dirender — RT berisi
                // video + semua overlay pada momen shutter.
                yield return new WaitForEndOfFrame();
                var photo = capture.Capture();
                if (photo != null)
                {
                    _lastPhoto = photo;
                    ShowPreview(photo);
                }
                else ShowError("Gagal mengambil foto. Coba lagi.");
            }
            finally
            {
                _capturing = false;
                if (_shutter != null) _shutter.interactable = true;
            }
        }

        IEnumerator FlashOnce()
        {
            for (float a = 0.9f; a > 0; a -= Time.unscaledDeltaTime * 4f)
            {
                _flash.color = new Color(1, 1, 1, a);
                yield return null;
            }
            _flash.color = new Color(1, 1, 1, 0);
        }

        /// <summary>Tampilkan foto hasil capture di modal preview.</summary>
        public void ShowPreview(Texture2D photo)
        {
            if (_preview == null) return;
            _previewImg.texture = photo;
            var fit = _previewImg.GetComponent<AspectRatioFitter>();
            if (fit != null) fit.aspectRatio = (float)photo.width / photo.height;
            _preview.SetActive(true);
        }

        public void HidePreview()
        {
            if (_preview != null) _preview.SetActive(false);
        }

        /// <summary>Sinkronkan RawImage live view dengan RenderTexture stage.</summary>
        public void BindLiveView()
        {
            if (_live != null && Stage.Target != null)
            {
                _live.texture = Stage.Target;
                var fit = _live.GetComponent<AspectRatioFitter>();
                if (fit != null) fit.aspectRatio = Stage.Aspect;
            }
        }
    }
}
