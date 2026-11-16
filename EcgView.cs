using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EcgPlus
{
    // Big ECG window ("ecgview" console command, Esc closes): centered, 66% of the screen width,
    // drawn at 4x the time resolution of the in-game monitor (7 ms per column instead of 28 ms).
    // Frame sprite, font and trace color are borrowed from the health panel at runtime.
    internal static class EcgView
    {
        const int W = 640, H = 200;

        static GameObject root;
        static Canvas canvas;
        static RectTransform panel;
        static Image frame;
        static RawImage trace;
        static TextMeshProUGUI header, rhythm;
        static EcgMonitor mon;
        static bool styled;

        public static bool IsOpen => root != null && root.activeSelf;

        public static void Toggle()
        {
            if (IsOpen) { root.SetActive(false); return; }
            if (PlayerCamera.main == null || PlayerCamera.main.body == null) throw new Exception("No world is loaded.");
            if (root == null) Build();
            mon.Reset();
            styled = false;
            root.SetActive(true);
        }

        public static string SetMarks(string what, bool force) => EcgMonitor.SetMarks(what, force);

        static void Build()
        {
            root = new GameObject("EcgPlusView");
            UnityEngine.Object.DontDestroyOnLoad(root);
            canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;

            panel = new GameObject("Frame", typeof(RectTransform)).GetComponent<RectTransform>();
            panel.SetParent(root.transform, false);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            frame = panel.gameObject.AddComponent<Image>();
            frame.raycastTarget = false;
            frame.color = new Color(0.08f, 0.16f, 0.12f, 0.95f);

            var hdr = new GameObject("Header", typeof(RectTransform)).GetComponent<RectTransform>();
            hdr.SetParent(panel, false);
            hdr.anchorMin = new Vector2(0f, 1f); hdr.anchorMax = new Vector2(1f, 1f); hdr.pivot = new Vector2(0.5f, 1f);
            header = hdr.gameObject.AddComponent<TextMeshProUGUI>();
            header.raycastTarget = false;
            header.alignment = TextAlignmentOptions.MidlineLeft;
            header.enableWordWrapping = false;

            var rh = new GameObject("Rhythm", typeof(RectTransform)).GetComponent<RectTransform>();
            rh.SetParent(hdr, false);
            rh.anchorMin = Vector2.zero; rh.anchorMax = Vector2.one; rh.offsetMin = rh.offsetMax = Vector2.zero;
            rhythm = rh.gameObject.AddComponent<TextMeshProUGUI>();
            rhythm.raycastTarget = false;
            rhythm.alignment = TextAlignmentOptions.MidlineRight;
            rhythm.enableWordWrapping = false;
            rhythm.color = EcgMonitor.MarkColor;

            var tr = new GameObject("Trace", typeof(RectTransform)).GetComponent<RectTransform>();
            tr.SetParent(panel, false);
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            trace = tr.gameObject.AddComponent<RawImage>();
            trace.raycastTarget = false;
            trace.color = new Color(0.35f, 1f, 0.55f, 1f);

            mon = new EcgMonitor(trace, W, H, 0.007f, true, 28f) { rhythm = rhythm };
            root.SetActive(false);
        }

        // borrow the health panel's look once it exists (frame sprite, font, trace tint, UI scale)
        static void Style()
        {
            styled = true;
            var wv = PlayerCamera.main != null ? PlayerCamera.main.woundView : null;
            if (wv == null) return;
            var view = wv.GetComponent<WoundView>();
            var bpm = view != null ? view.bpmText : null;
            if (bpm != null)
            {
                header.font = bpm.font;
                header.fontSharedMaterial = bpm.fontSharedMaterial;
                header.color = bpm.color;
                if (bpm.canvas != null) canvas.scaleFactor = bpm.canvas.rootCanvas.scaleFactor;
                // nearest ancestor panel with a 9-sliced sprite = the monitor frame
                for (var t = bpm.transform.parent; t != null; t = t.parent)
                {
                    var img = t.GetComponent<Image>();
                    if (img != null && img.sprite != null && img.sprite.border != Vector4.zero)
                    {
                        frame.sprite = img.sprite;
                        frame.type = Image.Type.Sliced;
                        frame.pixelsPerUnitMultiplier = img.pixelsPerUnitMultiplier;
                        frame.material = img.material;
                        frame.color = img.color;
                        break;
                    }
                }
            }
            Ui.ApplyLabelFont(rhythm);
            var ecg = wv.GetComponentInChildren<ECGVisualizer>(true);
            var src = ecg != null ? ecg.GetComponent<RawImage>() : null;
            if (src != null) { trace.color = src.color; trace.material = src.material; }
        }

        public static void Tick()
        {
            if (!IsOpen) return;
            var cam = PlayerCamera.main;
            if (cam == null || cam.body == null) { root.SetActive(false); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { root.SetActive(false); return; }
            if (!styled) Style();
            Layout();

            var b = cam.body;
            var view = cam.woundView != null ? cam.woundView.GetComponent<WoundView>() : null;
            header.text = view != null && view.bpmText != null ? view.bpmText.text : Mathf.RoundToInt(b.heartRate) + " | " + b.bloodPressureReadout;
            mon.Tick(b);
        }

        static void Layout()
        {
            float sf = Mathf.Max(0.01f, canvas.scaleFactor);
            float w = Screen.width * 0.66f / sf;
            float traceH = w * H / W;
            float pad = 10f, headH = Mathf.Max(18f, header.fontSize * 1.4f);
            panel.sizeDelta = new Vector2(w + pad * 2, traceH + headH + pad * 2);
            var hdr = (RectTransform)header.transform;
            hdr.offsetMin = new Vector2(pad * 1.5f, -pad - headH);
            hdr.offsetMax = new Vector2(-pad * 1.5f, -pad);
            var tr = (RectTransform)trace.transform;
            tr.offsetMin = new Vector2(pad, pad);
            tr.offsetMax = new Vector2(-pad, -pad - headH);
        }
    }
}
