using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EcgPlus
{
    // Manual defib minigame: a large monitor (about the size of "ecgview") replaces the small vanilla screen.
    // The vanilla screen is one background sprite with its grid and captions baked in, so we draw our own panel
    // (black, white grid, pixel-font captions) and move the minigame's value texts / icons into it. The minigame
    // keeps writing those texts every frame, so no information is lost. The device body and its buttons are
    // shifted down to make room; the slanted top of the casing ends up hidden behind the panel.
    //
    //  +----+-----------+--------+-----------+-------------+------+
    //  | SH | CHRG 0/100| BPM 70 | BP  / SPO2| POW.EST/CUR | 60hz |
    //  +----+-----------+--------+-----------+-------------+------+
    //  |                         ECG                              |
    //  +----------------------------------------------------------+
    [HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.Start))]
    static class DefibScreenPatch
    {
        // panel geometry in the minigame root's local units
        const float X0 = -630f, X1 = 630f, Y1 = 345f, Y0 = -262f, Line = 5f;
        const float RowY = 215f;                                   // info row | ECG separator
        static readonly float[] Cols = { -510f, -210f, -20f, 210f, 490f };
        const float ShiftDevice = -190f;                           // move casing + buttons down

        internal static EcgMonitor monitor;
        // the minigame window clips its content; while our wider screen is up, its masks are switched off
        static readonly System.Collections.Generic.List<Behaviour> masks = new System.Collections.Generic.List<Behaviour>();

        // called every frame from the plugin: restore the masks once the minigame is gone
        internal static void Watch()
        {
            if (masks.Count == 0 || (monitor != null && monitor.Alive)) return;
            foreach (var m in masks) if (m != null) m.enabled = true;
            masks.Clear();
            monitor = null;
        }

        static void Postfix(ManualDefibMinigame __instance)
        {
            if (!Plugin.on.Value) return;
            try { Build(__instance); }
            catch (Exception e) { BepInEx.Logging.Logger.CreateLogSource("EcgPlus").LogError(e); }
        }

        static void Build(ManualDefibMinigame mg)
        {
            var root = Minigame.game.spawnedMiniGame as RectTransform;
            if (root == null || root.childCount < 4) return;
            var screen = (RectTransform)root.GetChild(0);
            if (screen.childCount < 11) return;
            // grab everything by index before anything is inserted or reordered
            var kids = new RectTransform[11];
            for (int i = 0; i < 11; i++) kids[i] = (RectTransform)screen.GetChild(i);
            var buttons = new[] { (RectTransform)root.GetChild(1), (RectTransform)root.GetChild(2), (RectTransform)root.GetChild(3) };

            var ecgImg = kids[4].GetComponent<RawImage>();
            var bpm = kids[5].GetComponent<TextMeshProUGUI>();
            TMP_FontAsset font = bpm.font;
            Material fontMat = bpm.fontSharedMaterial;

            for (var t = root.parent; t != null; t = t.parent)
            {
                foreach (var m in t.GetComponents<RectMask2D>()) if (m.enabled) { m.enabled = false; masks.Add(m); }
                foreach (var m in t.GetComponents<Mask>()) if (m.enabled) { m.enabled = false; masks.Add(m); }
                if (t.GetComponent<Canvas>() != null && t.GetComponent<Canvas>().isRootCanvas) break;
            }

            // make room: casing + buttons go down, the panel takes the space above
            screen.anchoredPosition += new Vector2(0f, ShiftDevice);
            foreach (var b in buttons) b.anchoredPosition += new Vector2(0f, ShiftDevice);

            var panel = NewRect("EcgPlusDefibPanel", root);
            panel.anchoredPosition = Vector2.zero;
            panel.sizeDelta = Vector2.zero;

            // black fill + white grid
            Box(panel, X0, Y0, X1, Y1, Color.black);
            Color w = Color.white;
            Box(panel, X0, Y1 - Line, X1, Y1, w);
            Box(panel, X0, Y0, X1, Y0 + Line, w);
            Box(panel, X0, Y0, X0 + Line, Y1, w);
            Box(panel, X1 - Line, Y0, X1, Y1, w);
            Box(panel, X0, RowY - Line / 2, X1, RowY + Line / 2, w);
            foreach (var c in Cols) Box(panel, c - Line / 2, RowY, c + Line / 2, Y1, w);

            float top = Y1 - Line, rowA = 300f, rowB = 245f, mid = (RowY + Y1) / 2f - 8f;
            Color grey = new Color(0.55f, 0.55f, 0.55f);

            // SH
            Caption(panel, "SH", X0 + Line + 6f, top - 2f, font, fontMat, grey);
            Place(panel, kids[0], (X0 + Cols[0]) / 2f, mid, Cols[0] - X0 - 20f, TextAlignmentOptions.Center);
            // CHRG  now / target J
            Caption(panel, "CHRG", Cols[0] + Line + 6f, top - 2f, font, fontMat, grey);
            Place(panel, kids[1], -405f, mid, 120f, TextAlignmentOptions.MidlineRight);
            Text(panel, "/", -330f, mid, 24f, grey, font, fontMat, 33.33f);
            Place(panel, kids[2], -255f, mid, 120f, TextAlignmentOptions.MidlineLeft);
            Text(panel, "J", -222f, mid - 10f, 16f, w, font, fontMat, 20f);
            // HR (counted from our ECG) / PR (pulse: the game's heart rate, vanilla's BPM text)
            var bpmTmp = kids[5].GetComponent<TextMeshProUGUI>();
            var bpTmp = kids[8].GetComponent<TextMeshProUGUI>();
            Inline(panel, "HR", Cols[1] + Line + 8f, rowA, font, fontMat, grey);
            hrColor = bpmTmp != null ? bpmTmp.color : w;
            hrText = NewText(panel, "", font, fontMat, hrColor, bpTmp != null ? bpTmp.fontSize : 33.33f);
            Place(panel, (RectTransform)hrText.transform, -85f, rowA, 110f, TextAlignmentOptions.MidlineLeft);
            Inline(panel, "PR", Cols[1] + Line + 8f, rowB, font, fontMat, grey);
            if (bpmTmp != null && bpTmp != null) { bpmTmp.fontSize = bpTmp.fontSize; bpmTmp.enableAutoSizing = false; }
            Place(panel, kids[5], -85f, rowB, 110f, TextAlignmentOptions.MidlineLeft);
            // BP / SpO2
            Inline(panel, "BP", Cols[2] + Line + 8f, rowA, font, fontMat, grey);
            Place(panel, kids[8], 125f, rowA, 160f, TextAlignmentOptions.MidlineLeft);
            Inline(panel, "SPO2", Cols[2] + Line + 8f, rowB, font, fontMat, grey);
            Place(panel, kids[9], 135f, rowB, 140f, TextAlignmentOptions.MidlineLeft);
            // POW.EST / CUR J
            Inline(panel, "POW.EST", Cols[3] + Line + 8f, rowA, font, fontMat, grey);
            Place(panel, kids[6], 410f, rowA, 150f, TextAlignmentOptions.MidlineLeft);
            Inline(panel, "CUR", Cols[3] + Line + 8f, rowB, font, fontMat, grey);
            Place(panel, kids[7], 395f, rowB, 120f, TextAlignmentOptions.MidlineLeft);
            Text(panel, "J", 474f, rowB - 8f, 16f, w, font, fontMat, 20f);
            // 60hz + battery + charging lamp
            Caption(panel, "60hz", Cols[4] + Line + 6f, top - 2f, font, fontMat, grey);
            var bat = kids[3];
            Place(panel, bat, 585f, 290f, bat.sizeDelta.x, TextAlignmentOptions.Center);
            Vector2 bs = bat.sizeDelta;
            float bx0 = 585f - bs.x / 2f - 4f, bx1 = 585f + bs.x / 2f + 4f, by0 = 290f - bs.y / 2f - 4f, by1 = 290f + bs.y / 2f + 4f;
            Box(panel, bx0, by0, bx1, by0 + 3f, w); Box(panel, bx0, by1 - 3f, bx1, by1, w);
            Box(panel, bx0, by0, bx0 + 3f, by1, w); Box(panel, bx1 - 3f, by0, bx1, by1, w);
            Box(panel, bx1, 290f - 6f, bx1 + 4f, 290f + 6f, w);
            Place(panel, kids[10], 560f, 242f, kids[10].sizeDelta.x, TextAlignmentOptions.Center);

            // ECG area: hide the vanilla trace, draw ours
            kids[4].GetComponent<ECGVisualizer>().enabled = false;
            ecgImg.enabled = false;
            var row = NewRect("EcgPlusTrace", panel);
            SetRect(row, X0 + Line + 4f, Y0 + Line + 4f, X1 - Line - 4f, RowY - Line / 2 - 4f);
            var raw = row.gameObject.AddComponent<RawImage>();
            raw.raycastTarget = false;
            raw.color = ecgImg.color;

            var rh = NewRect("Rhythm", panel);
            rh.pivot = new Vector2(1f, 1f);
            rh.anchoredPosition = new Vector2(X1 - Line - 12f, RowY - Line - 6f);
            rh.sizeDelta = new Vector2(500f, 34f);
            var rhythm = rh.gameObject.AddComponent<TextMeshProUGUI>();
            rhythm.alignment = TextAlignmentOptions.TopRight;
            rhythm.enableWordWrapping = false;
            rhythm.raycastTarget = false;
            rhythm.fontSize = 26f;
            rhythm.color = EcgMonitor.MarkColor;
            Ui.ApplyLabelFont(rhythm);

            bool onTorso = Traverse.Create(mg).Field("onTorso").GetValue<bool>();
            float rw = row.sizeDelta.x, rhh = row.sizeDelta.y;
            monitor = new EcgMonitor(raw, Mathf.RoundToInt(rw / 2f), Mathf.RoundToInt(rhh / 2f), 0.007f, true, 24f) { rhythm = rhythm, flat = !onTorso };

            // marks on/off soft key, right of the SHCK button (clicked with the minigame hand, see DefibUpdatePatch)
            const float bx = 470f, by = -350f, bw = 170f, bh = 56f;
            Box(panel, bx - bw / 2, by - bh / 2, bx + bw / 2, by + bh / 2, w);
            var face = NewRect("EcgPlusMarkKey", panel);
            SetRect(face, bx - bw / 2 + 4f, by - bh / 2 + 4f, bx + bw / 2 - 4f, by + bh / 2 - 4f);
            var faceImg = face.gameObject.AddComponent<Image>();
            faceImg.color = Color.black;
            faceImg.raycastTarget = true;
            markKey = face.gameObject;
            markLabel = NewText(face, "", font, fontMat, grey, 22f);
            var lrt = (RectTransform)markLabel.transform;
            lrt.anchoredPosition = Vector2.zero;
            lrt.sizeDelta = new Vector2(bw - 8f, bh - 8f);
            markLabel.alignment = TextAlignmentOptions.Center;
            RefreshKey();
        }

        internal static GameObject markKey;
        internal static TextMeshProUGUI hrText;
        internal static Color hrColor = Color.white;
        internal static TextMeshProUGUI markLabel;
        static string lastOn = "all";

        internal static bool MarksOn => EcgMonitor.markWaves || EcgMonitor.markTypes;

        // auto labels need the INT to recognize the manual defibrillator itself (vanilla Recognition, INT 8)
        internal static bool CanRead =>
            Item.GlobalItems != null && Item.GlobalItems.TryGetValue("manualdefibrillator", out var info) && info.rec != null && info.rec.recognizable;

        // toggle: off <-> whatever was last on (console "ecgview mark waves/types/all" still picks the mode)
        internal static void ToggleMarks()
        {
            if (!CanRead) return;
            if (MarksOn)
            {
                lastOn = EcgMonitor.markWaves && EcgMonitor.markTypes ? "all" : EcgMonitor.markWaves ? "waves" : "types";
                EcgMonitor.SetMarks("off", EcgMonitor.markForce);
            }
            else EcgMonitor.SetMarks(lastOn, EcgMonitor.markForce);
            RefreshKey();
        }

        internal static void RefreshKey()
        {
            if (markLabel == null) return;
            if (!CanRead)
            {
                markLabel.text = "MARK ---";
                markLabel.color = new Color(0.3f, 0.3f, 0.3f);
                return;
            }
            bool on = MarksOn;
            markLabel.text = on ? "MARK ON" : "MARK OFF";
            markLabel.color = on ? EcgMonitor.MarkColor : new Color(0.55f, 0.55f, 0.55f);
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }

        static void SetRect(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchoredPosition = new Vector2((x0 + x1) / 2f, (y0 + y1) / 2f);
            rt.sizeDelta = new Vector2(x1 - x0, y1 - y0);
        }

        static void Box(RectTransform parent, float x0, float y0, float x1, float y1, Color c)
        {
            var rt = NewRect("Box", parent);
            SetRect(rt, x0, y0, x1, y1);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
        }

        static TextMeshProUGUI NewText(RectTransform parent, string text, TMP_FontAsset font, Material mat, Color c, float size)
        {
            var rt = NewRect("Txt_" + text, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = font; t.fontSharedMaterial = mat;
            t.fontSize = size;
            t.color = c;
            t.enableWordWrapping = false;
            t.raycastTarget = false;
            t.text = text;
            return t;
        }

        // caption in the top-left corner of a cell
        static void Caption(RectTransform parent, string text, float left, float top, TMP_FontAsset font, Material mat, Color c)
        {
            var t = NewText(parent, text, font, mat, c, 18f);
            var rt = (RectTransform)t.transform;
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(left, top);
            rt.sizeDelta = new Vector2(160f, 24f);
            t.alignment = TextAlignmentOptions.TopLeft;
        }

        // caption on the same line as a value
        static void Inline(RectTransform parent, string text, float left, float y, TMP_FontAsset font, Material mat, Color c)
        {
            var t = NewText(parent, text, font, mat, c, 18f);
            var rt = (RectTransform)t.transform;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(left, y);
            rt.sizeDelta = new Vector2(120f, 24f);
            t.alignment = TextAlignmentOptions.MidlineLeft;
        }

        static void Text(RectTransform parent, string text, float x, float y, float w, Color c, TMP_FontAsset font, Material mat, float size)
        {
            var t = NewText(parent, text, font, mat, c, size);
            var rt = (RectTransform)t.transform;
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, 50f);
            t.alignment = TextAlignmentOptions.Center;
        }

        // move a vanilla element into the panel (it keeps its own font, color and updates)
        static void Place(RectTransform panel, RectTransform rt, float x, float y, float width, TextAlignmentOptions align)
        {
            rt.SetParent(panel, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
            var t = rt.GetComponent<TextMeshProUGUI>();
            if (t != null) { t.alignment = align; t.enableWordWrapping = false; }
            rt.SetAsLastSibling();
        }
    }

    // drive the monitor every frame while the minigame is open
    [HarmonyPatch(typeof(ManualDefibMinigame), "Update")]
    static class DefibUpdatePatch
    {
        static void Postfix(System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult> uiCasts)
        {
            var m = DefibScreenPatch.monitor;
            if (m == null) return;
            if (!m.Alive) { DefibScreenPatch.monitor = null; return; }
            if (Minigame.game != null && Minigame.game.handStartedClicking && uiCasts != null && DefibScreenPatch.markKey != null)
                foreach (var c in uiCasts)
                    if (c.gameObject == DefibScreenPatch.markKey) { DefibScreenPatch.ToggleMarks(); break; }
            m.noMarks = !DefibScreenPatch.CanRead;
            if (DefibScreenPatch.hrText != null && PlayerCamera.main != null && PlayerCamera.main.body != null)
            {
                var body = PlayerCamera.main.body;
                float hr = m.flat ? -1f : Rhythm.EcgRate(body);
                var t = DefibScreenPatch.hrText;
                t.text = hr < 0f ? "---" : Mathf.RoundToInt(hr).ToString();
                // alarm: the ECG rate and the pulse disagree (flutter: pulse deficit) or can't be counted (VF / arrest).
                // Flashes red at 2 Hz; nothing to alarm on while the paddles are off the chest.
                float pr = body.heartRate;
                bool alarm = !m.flat && (hr < 0f || Mathf.Abs(hr - pr) > 0.1f * Mathf.Max(pr, 1f));
                bool red = alarm && Mathf.Repeat(Time.unscaledTime * 2f, 1f) < 0.5f;
                t.color = red ? new Color(1f, 0.2f, 0.2f) : DefibScreenPatch.hrColor;
            }
            DefibScreenPatch.RefreshKey();
            var b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (b == null) return;
            try { m.Tick(b); } catch (Exception) { }
        }
    }
}
