using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EcgPlus
{
    // One high resolution sweep monitor (texture + fading trace + optional labels).
    // Used by the big "ecgview" window and by the manual defib screen; each instance keeps its own state.
    internal class EcgMonitor
    {
        const float FadePer28ms = 0.985f;    // same persistence as the vanilla monitor

        // shared annotation settings ("ecgview mark ...")
        internal static bool markWaves, markTypes, markForce;
        internal static readonly Color MarkColor = new Color(1f, 0.85f, 0.25f, 1f);

        readonly RawImage trace;
        readonly int W, H;
        readonly float colDt, labelSize;
        readonly Texture2D tex;
        readonly Color32[] px;
        readonly float[] alpha;
        readonly byte[] grid;
        int writeX, lastY;
        double lastT;
        public TextMeshProUGUI rhythm;   // optional rhythm name label, owned by the caller
        public bool flat;                // draw a flat line (paddles not on the chest)

        class Mark { public string text; public double t; public bool below, isType, normal, placed; public float yPix; public int run; public TextMeshProUGUI ui; }
        const double SegEvery = 1.5;
        double lastSeg, lastSeenT0;
        readonly List<Mark> marks = new List<Mark>();
        readonly Stack<TextMeshProUGUI> pool = new Stack<TextMeshProUGUI>();
        readonly Dictionary<int, Mark> runMark = new Dictionary<int, Mark>();
        readonly Dictionary<int, int> runLen = new Dictionary<int, int>();
        readonly HashSet<int> runEnded = new HashSet<int>(), runSus = new HashSet<int>();
        static readonly List<(string name, float dt, bool below)> feat = new List<(string, float, bool)>();
        int markVersion = -1;
        static int settingsVersion;

        public bool Alive => trace != null;
        public RawImage Trace => trace;

        public EcgMonitor(RawImage trace, int w, int h, float colDt, bool withGrid, float labelSize)
        {
            this.trace = trace; W = Mathf.Max(16, w); H = Mathf.Max(16, h); this.colDt = colDt; this.labelSize = labelSize;
            tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            trace.texture = tex;
            px = new Color32[W * H];
            alpha = new float[W * H];
            grid = new byte[W * H];
            if (withGrid)
                for (int y = 0; y < H; y += 8)
                    for (int x = 0; x < W; x += 8)
                        grid[y * W + x] = (byte)((x % 40 == 0 && y % 40 == 0) ? 70 : 28);
            Reset();
        }

        public void Reset()
        {
            Array.Clear(alpha, 0, alpha.Length);
            ClearMarks();
            writeX = 0; lastY = H / 2;
            lastT = lastSeenT0 = Time.unscaledTime;
            lastSeg = 0;
        }

        public static string SetMarks(string what, bool force)
        {
            switch (what)
            {
                case "waves": markWaves = true; markTypes = false; break;
                case "types": markWaves = false; markTypes = true; break;
                case "all": markWaves = markTypes = true; break;
                case "off": markWaves = markTypes = false; break;
                default: throw new Exception("usage: ecgview mark waves|types|all|off [force]");
            }
            markForce = force;
            settingsVersion++;
            return "ecg marks: waves " + (markWaves ? "on" : "off") + ", types " + (markTypes ? "on" : "off") + (force ? " (forced above 150 bpm)" : "");
        }

        public void Tick(Body b)
        {
            double now = Time.unscaledTime;
            if (now - lastT > 1.0) lastT = now - 1.0;   // after a hitch, don't redraw seconds of trace at once
            float fade = Mathf.Pow(FadePer28ms, (float)(now - lastT) / 0.028f);
            for (int i = 0; i < alpha.Length; i++) alpha[i] *= fade;

            while (lastT + colDt <= now)
            {
                lastT += colDt;
                float off = (float)(now - lastT);
                float v = flat ? 0f : Plugin.on.Value ? Rhythm.Sample(b, off) : b.GetECGHeight(off);
                int y = Mathf.Clamp(Mathf.RoundToInt((v * 0.85f + 1f) * 0.5f * (H - 1)), 0, H - 1);
                writeX = (writeX + 1) % W;
                int y0 = Mathf.Min(lastY, y), y1 = Mathf.Max(lastY, y);
                for (int yy = y0; yy <= y1; yy++)
                {
                    Put(writeX, yy, 1f);
                    Put(writeX + 1, yy, 0.6f);
                    Put(writeX, yy + 1, 0.35f);
                    Put(writeX, yy - 1, 0.35f);
                }
                lastY = y;
            }

            if (markVersion != settingsVersion) { ClearMarks(); lastSeenT0 = now; lastSeg = 0; markVersion = settingsVersion; }
            UpdateMarks(b, now);

            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32(255, 255, 255, (byte)Mathf.Max(grid[i], (int)(alpha[i] * 255f)));
            tex.SetPixels32(px);
            tex.Apply(false);
        }

        void Put(int x, int y, float a)
        {
            if (x < 0 || x >= W || y < 0 || y >= H) return;
            int i = y * W + x;
            if (alpha[i] < a) alpha[i] = a;
        }

        // ---------------- labels ----------------

        void ClearMarks()
        {
            foreach (var m in marks) Recycle(m);
            marks.Clear(); runMark.Clear(); runLen.Clear(); runEnded.Clear(); runSus.Clear();
        }

        void Recycle(Mark m)
        {
            if (m.ui == null) return;
            m.ui.gameObject.SetActive(false);
            pool.Push(m.ui);
            m.ui = null;
        }

        TextMeshProUGUI NewLabel()
        {
            TextMeshProUGUI t;
            if (pool.Count > 0) t = pool.Pop();
            else
            {
                var rt = new GameObject("Mark", typeof(RectTransform)).GetComponent<RectTransform>();
                rt.SetParent(trace.transform, false);
                rt.anchorMin = rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(160f, 30f);
                t = rt.gameObject.AddComponent<TextMeshProUGUI>();
                t.raycastTarget = false;
                t.alignment = TextAlignmentOptions.Center;
                t.enableWordWrapping = false;
                Ui.ApplyLabelFont(t);
            }
            t.fontSize = labelSize;
            t.color = MarkColor;
            t.gameObject.SetActive(true);
            return t;
        }

        internal static string RhythmName(Body b)
        {
            if (!b.alive) return Ui.L("Asystole");
            if (b.inCardiacArrest) return Ui.L(Rhythm.ArrestAmp(b) > 0.4f ? "VF (coarse)" : "VF (fine)");
            float f = b.fibrillationProgress;
            if (Rhythm.VfWeight(f) >= 0.5f) return Ui.L("VF");
            var bs = Rhythm.Beats;
            if (bs.Count > 0 && bs[bs.Count - 1].kind == 1 && bs[bs.Count - 1].sus)
            {
                if (Rhythm.SineWeight(f) >= 0.5f) return Ui.L("V-flutter");
                return Ui.L(Rhythm.TriWeight(f) >= 0.5f ? "VT (triangle)" : "VT");
            }
            return Ui.L(f > 15f ? "Sinus + ectopy" : "Sinus");
        }

        static string SegName(Body b, List<Rhythm.Beat> bs)
        {
            if (!b.alive) return Ui.L("Asystole");
            if (b.inCardiacArrest) return Ui.L(Rhythm.ArrestAmp(b) > 0.4f ? "VF (coarse)" : "VF (fine)");
            float f = b.fibrillationProgress, vf = Rhythm.VfWeight(f);
            if (vf >= 0.5f) return Ui.L("VF");
            if (vf >= 0.2f) return Ui.L("V-flutter > VF");
            if (bs.Count > 0 && bs[bs.Count - 1].kind == 1 && bs[bs.Count - 1].sus)
            {
                if (Rhythm.SineWeight(f) >= 0.5f) return Ui.L("V-flutter");
                return Rhythm.TriWeight(f) >= 0.5f ? Ui.L("VT (triangle)") : (Ui.Zh ? "室速" : "VT");
            }
            return null;
        }

        string RunText(int run)
        {
            int n = runLen.TryGetValue(run, out var c) ? c : 1;
            if (runEnded.Contains(run))
                return n == 1 ? Ui.L("PVC") : n == 2 ? Ui.L("Couplet") : n > 10 ? (Ui.Zh ? "室速" : "VT") : Ui.L("NSVT") + " x" + n;   // long runs read as VT on screen
            return runSus.Contains(run) ? (Ui.Zh ? "室速" : "VT") : Ui.L("V") + " x" + n;
        }

        void UpdateMarks(Body b, double now)
        {
            bool on = Plugin.on.Value && !flat && (markWaves || markTypes);
            if (rhythm != null) rhythm.text = on && markTypes ? RhythmName(b) : "";
            if (!on) { if (marks.Count > 0) ClearMarks(); return; }

            bool skip = b.heartRate > 150f && !markForce;
            var bs = Rhythm.Beats;
            for (int i = 0; i < bs.Count; i++)
            {
                var bt = bs[i];
                if (bt.kind == 1)
                {
                    if (bt.tail) runEnded.Add(bt.run);
                    if (bt.sus) runSus.Add(bt.run);
                }
                if (bt.t0 <= lastSeenT0) continue;
                lastSeenT0 = bt.t0;
                if (bt.kind == 1) runLen[bt.run] = runLen.TryGetValue(bt.run, out var c) ? c + 1 : 1;
                if (skip) continue;
                if (markWaves)
                {
                    Rhythm.Features(bt, feat);
                    foreach (var fe in feat) marks.Add(new Mark { text = fe.name, t = bt.t0 + fe.dt, below = fe.below });
                }
                if (markTypes)
                {
                    if (bt.kind == 0)
                        marks.Add(new Mark { text = Ui.L("N"), t = bt.t0 + bt.pr + 0.45f * bt.qrs, isType = true, normal = true });
                    else if (!runMark.ContainsKey(bt.run))
                    {
                        var m = new Mark { text = "", t = bt.t0 + 0.05, isType = true, run = bt.run };
                        runMark[bt.run] = m;
                        marks.Add(m);
                    }
                }
            }

            // continuous rhythms (sustained VT, flutter, VF, arrest): name the rhythm every 1.5 s of trace
            if (markTypes)
            {
                string seg = SegName(b, bs);
                if (seg != null && lastT - lastSeg >= SegEvery)
                {
                    lastSeg = lastT;
                    marks.Add(new Mark { text = seg, t = lastT, isType = true });
                }
            }

            var tr = (RectTransform)trace.transform;
            float rw = tr.rect.width, rhgt = tr.rect.height;
            double span = W * colDt * 0.97;
            for (int i = marks.Count - 1; i >= 0; i--)
            {
                var m = marks[i];
                double age = lastT - m.t;
                if (age > span) { Recycle(m); if (m.run != 0) runMark.Remove(m.run); marks.RemoveAt(i); continue; }
                if (age < 0) continue;   // not drawn yet
                if (!m.placed)
                {
                    float v = Rhythm.Sample(b, (float)(now - m.t));
                    m.yPix = (v * 0.85f + 1f) * 0.5f * (H - 1);
                    m.placed = true;
                    m.ui = NewLabel();
                }
                if (m.run != 0) m.text = RunText(m.run);
                m.ui.text = m.text;
                float col = (float)(writeX - age / colDt);
                if (col < 0) col += W;
                float x = col / W * rw;
                float typeRow = rhgt * 0.07f + labelSize * 0.3f;
                float y = m.isType ? typeRow : m.yPix / H * rhgt + (m.below ? -1f : 1f) * labelSize * 0.9f;
                if (!m.isType && m.below && y < typeRow + labelSize * 1.2f)
                {
                    y = m.yPix / H * rhgt;
                    x += labelSize * 0.8f;
                }
                ((RectTransform)m.ui.transform).anchoredPosition = new Vector2(x, y);
                float fade = Mathf.Pow(FadePer28ms, (float)age / 0.028f);
                var c4 = MarkColor; c4.a = Mathf.Max(0.35f, fade);
                m.ui.color = c4;
                m.ui.enabled = true;
            }

            // colliding type labels: a run label (PVC / couplet / NSVT ...) wins over a "normal" one
            for (int i = 0; i < marks.Count; i++)
            {
                var a = marks[i];
                if (!a.isType || a.run == 0 || a.ui == null) continue;
                var ra = (RectTransform)a.ui.transform;
                float aw = a.ui.preferredWidth * 0.5f;
                for (int j = 0; j < marks.Count; j++)
                {
                    var n = marks[j];
                    if (!n.normal || n.ui == null) continue;
                    var rn = (RectTransform)n.ui.transform;
                    if (Mathf.Abs(ra.anchoredPosition.x - rn.anchoredPosition.x) < aw + n.ui.preferredWidth * 0.5f) n.ui.enabled = false;
                }
            }
        }
    }

    // shared UI helpers: localized label text and fonts borrowed from the health panel
    internal static class Ui
    {
        internal static bool Zh => Locale.currentLangName != null && Locale.currentLangName.StartsWith("zh");
        static readonly Dictionary<string, string> ZhText = new Dictionary<string, string>
        {
            ["N"] = "正常",
            ["PVC"] = "室早",
            ["Couplet"] = "成对室早",
            ["NSVT"] = "短阵室速",
            ["V"] = "室性",
            ["VT"] = "持续性室速",
            ["VT (triangle)"] = "室速（三角波）",
            ["V-flutter"] = "室扑",
            ["VF"] = "室颤",
            ["VF (coarse)"] = "粗颤",
            ["VF (fine)"] = "细颤",
            ["Sinus"] = "窦性心律",
            ["Sinus + ectopy"] = "窦性心律 + 异位搏动",
            ["Asystole"] = "心脏停搏",
            ["V-flutter > VF"] = "室扑转室颤",
        };
        internal static string L(string en) => Zh && ZhText.TryGetValue(en, out var zh) ? zh : en;

        static TMP_FontAsset cjkFont, pixelFont;
        static Material cjkMat, pixelMat;

        static void Find()
        {
            if (cjkFont != null && pixelFont != null) return;
            var wv = PlayerCamera.main != null && PlayerCamera.main.woundView != null ? PlayerCamera.main.woundView.GetComponent<WoundView>() : null;
            if (wv == null) return;
            if (wv.limbNameText != null) { cjkFont = wv.limbNameText.font; cjkMat = wv.limbNameText.fontSharedMaterial; }
            if (wv.bpmText != null) { pixelFont = wv.bpmText.font; pixelMat = wv.bpmText.fontSharedMaterial; }
        }

        // labels can be Chinese: use the font the health panel uses for limb names
        internal static void ApplyLabelFont(TMP_Text t)
        {
            Find();
            if (cjkFont != null) { t.font = cjkFont; t.fontSharedMaterial = cjkMat; }
            else if (pixelFont != null) { t.font = pixelFont; t.fontSharedMaterial = pixelMat; }
        }
    }
}
