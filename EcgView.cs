using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EcgPlus
{
    // Big ECG window ("ecgview" console command, Esc closes): centered, 66% of the screen width,
    // drawn at 4x the time resolution of the in-game monitor (7 ms per column instead of 28 ms).
    // Frame sprite, font and trace color are borrowed from the health panel at runtime.
    // "ecgview sync": a second trace on top draws the vanilla curve locked to the same beats (Rhythm.SampleVanilla).
    internal static class EcgView
    {
        const int W = 640, H = 200;

        static GameObject root;
        static Canvas canvas;
        static RectTransform panel;
        static Image frame;
        static RawImage trace, vtrace;
        static TextMeshProUGUI header, rhythm, vtag, ptag;
        static EcgMonitor mon, vmon;
        static bool styled, sync;

        public static bool IsOpen => root != null && root.activeSelf;

        // plain "ecgview" toggles; "ecgview sync" opens the comparison (or switches an open plain view to it)
        public static void Toggle(bool syncMode = false)
        {
            if (IsOpen && sync == syncMode) { root.SetActive(false); return; }
            if (PlayerCamera.main == null || PlayerCamera.main.body == null) throw new Exception("No world is loaded.");
            if (root == null) Build();
            sync = syncMode;
            vtrace.gameObject.SetActive(sync);
            vtag.gameObject.SetActive(sync);
            ptag.gameObject.SetActive(sync);
            mon.Reset();
            vmon.Reset();
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

            var vt = new GameObject("VanillaTrace", typeof(RectTransform)).GetComponent<RectTransform>();
            vt.SetParent(panel, false);
            vt.anchorMin = Vector2.zero; vt.anchorMax = Vector2.one;
            vtrace = vt.gameObject.AddComponent<RawImage>();
            vtrace.raycastTarget = false;
            vtrace.color = trace.color;
            vmon = new EcgMonitor(vtrace, W, H, 0.007f, true, 28f) { noMarks = true, source = Rhythm.SampleVanilla };

            vtag = Tag("VANILLA", vt);
            ptag = Tag("PLUS", tr);
            root.SetActive(false);
        }

        static TextMeshProUGUI Tag(string text, RectTransform parent)
        {
            var rt = new GameObject("Tag", typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(8f, -6f);
            rt.sizeDelta = new Vector2(200f, 30f);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.raycastTarget = false;
            t.text = text;
            t.fontSize = 18f;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.enableWordWrapping = false;
            t.color = new Color(0.6f, 0.6f, 0.6f, 0.9f);
            return t;
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
            if (src != null) { trace.color = vtrace.color = src.color; trace.material = vtrace.material = src.material; }
            foreach (var t in new[] { vtag, ptag })
                if (bpm != null) { t.font = bpm.font; t.fontSharedMaterial = bpm.fontSharedMaterial; }
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
            // HR = counted from the ECG (ventricular rate), PR = pulse (the game's heart rate); they part in flutter
            float hr = Plugin.on.Value ? Rhythm.EcgRate(b) : b.heartRate;
            string hrs = hr < 0f ? "---" : Mathf.RoundToInt(hr).ToString();
            header.text = "HR " + hrs + "  PR " + Mathf.RoundToInt(b.heartRate) + "  |  " + b.bloodPressureReadout;
            mon.Tick(b);
            if (sync) vmon.Tick(b);
        }

        static void Layout()
        {
            float sf = Mathf.Max(0.01f, canvas.scaleFactor);
            float w = Screen.width * 0.66f / sf;
            float traceH = w * H / W;
            float pad = 10f, headH = Mathf.Max(18f, header.fontSize * 1.4f);
            float gap = sync ? pad : 0f, total = sync ? traceH * 2 + gap : traceH;
            panel.sizeDelta = new Vector2(w + pad * 2, total + headH + pad * 2);
            var hdr = (RectTransform)header.transform;
            hdr.offsetMin = new Vector2(pad * 1.5f, -pad - headH);
            hdr.offsetMax = new Vector2(-pad * 1.5f, -pad);
            // plus: bottom trace (or the only one); vanilla: above it
            var tr = (RectTransform)trace.transform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = new Vector2(1f, 0f);
            tr.offsetMin = new Vector2(pad, pad);
            tr.offsetMax = new Vector2(-pad, pad + traceH);
            if (sync)
            {
                var vt = (RectTransform)vtrace.transform;
                vt.anchorMin = Vector2.zero; vt.anchorMax = new Vector2(1f, 0f);
                vt.offsetMin = new Vector2(pad, pad + traceH + gap);
                vt.offsetMax = new Vector2(-pad, pad + traceH * 2 + gap);
            }
        }
    }
}
