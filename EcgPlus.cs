using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EcgPlus
{
    // Display-only ECG rework. Gameplay values (heart rate, BP, fibrillation, defib odds) are untouched;
    // only what Body.GetECGHeight returns to the ECG screen changes, plus two text fixes.
    [BepInPlugin("local.cu.ecgplus", "CU ECG Plus", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> on, fixST, staging, jWave, lowVoltage, textFixes;

        void Awake()
        {
            on = Config.Bind("General", "Enabled", true, "Use the reworked ECG waveform.");
            fixST = Config.Bind("Waveform", "FixSTSegment", true, "Lift the vanilla depressed ST segment back to baseline.");
            staging = Config.Bind("Waveform", "ArrhythmiaStaging", true, "PVCs -> runs of VT -> sustained VT -> flutter -> VF instead of the vanilla blend.");
            jWave = Config.Bind("Waveform", "Hypothermia", true, "J (Osborn) wave and QT prolongation when cold.");
            lowVoltage = Config.Bind("Waveform", "HemothoraxLowVoltage", true, "Smaller complexes with hemothorax.");
            textFixes = Config.Bind("Text", "Fixes", true, "Chinese text fixes: 心率不齐 -> 心律不齐, fibrillation3 -> 室扑, cardiac arrest description");
            new Harmony("local.cu.ecgplus").PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("ECG Plus loaded");
        }
    }

    [HarmonyPatch(typeof(Body), nameof(Body.GetECGHeight))]
    static class EcgPatch
    {
        static void Postfix(Body __instance, float offset, ref float __result)
        {
            if (!Plugin.on.Value) return;
            try { __result = Rhythm.Eval(__instance, offset, __result); }
            catch (Exception) { /* fall back to vanilla value */ }
        }
    }

    // Heartbeat sound follows the displayed beats: vanilla thump is suppressed, ours plays at each R wave.
    [HarmonyPatch(typeof(Body), nameof(Body.HandleCirculation))]
    static class CirculationPatch
    {
        static readonly AccessTools.FieldRef<Body, bool> didThump = AccessTools.FieldRefAccess<Body, bool>("didThump");
        static void Prefix(Body __instance)
        {
            if (Plugin.on.Value && IsPlayer(__instance)) didThump(__instance) = true;
        }
        static void Postfix(Body __instance)
        {
            if (!Plugin.on.Value || !IsPlayer(__instance)) return;
            try { Rhythm.Tick(__instance); } catch (Exception) { }
        }
        static bool IsPlayer(Body b) => PlayerCamera.main != null && PlayerCamera.main.body == b;
    }

    // console command "ecg": toggles between this mod and the vanilla monitor (not a cheat, achievements unaffected)
    [HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.RegisterAllCommands))]
    static class ConsolePatch
    {
        static void Postfix()
        {
            ConsoleScript.Commands.Add(new Command("ecg", "Switches the ECG monitor between EcgPlus and vanilla. No argument toggles.", delegate (string[] args)
            {
                string a = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                Plugin.on.Value = a == "plus" ? true : a == "vanilla" ? false : !Plugin.on.Value;
                Traverse.Create(ConsoleScript.instance).Method("LogToConsole", new[] { typeof(string) })
                    .GetValue("ECG: " + (Plugin.on.Value ? "EcgPlus" : "vanilla"));
            }, new Dictionary<int, List<string>> { { 0, new List<string> { "plus", "vanilla" } } }, ("mode", "plus / vanilla; empty = toggle")));
        }
    }

    [HarmonyPatch(typeof(Locale), nameof(Locale.GetString))]
    static class TextPatch
    {
        static void Postfix(string str, int type, ref string __result)
        {
            if (!Plugin.textFixes.Value || type != 2 || __result == null) return;
            // wording agreed with the user; Chinese and English only, other languages untouched
            if (IsChinese(__result))
            {
                if (str == "fibrillation2dsc") __result = __result.Replace("心率不齐", "心律不齐");
                else if (str == "fibrillation3") __result = "室扑";
                else if (str == "cardiacarrestdsc") __result = "室颤，心脏失去泵血功能。通过除颤或注射肾上腺素可能恢复心律。脑缺氧和死亡即将到来。";
            }
            else if (__result == "Ventricular fibrillation" && str == "fibrillation3") __result = "Ventricular flutter";
            else if (str == "cardiacarrestdsc" && __result.StartsWith("Asystole."))
                __result = "Ventricular fibrillation. The heart can no longer pump blood. May be treated via defibrillation or epinephrine. Cerebral hypoxia and death is soon to follow.";
        }

        static bool IsChinese(string s)
        {
            foreach (char c in s) if (c >= 0x4e00 && c <= 0x9fff) return true;
            return false;
        }
    }

    static class Rhythm
    {
        const int Normal = 0, Ventricular = 1;

        struct Beat
        {
            public double t0;          // P onset (normal) or QRS onset (ventricular), unscaled seconds
            public int kind;
            public float pr, qrs, qt, j;
            public bool thumped;
            public float per, fib;
            public bool tail;   // ventricular beat followed by a normal beat: no upswing into a next complex   // ventricular: cycle length and fibrillation when generated
        }

        static readonly List<Beat> beats = new List<Beat>();
        static readonly System.Random rng = new System.Random();
        static double nextT = -1, sinusNext, lastNow, arrestStart = -1;
        static float arrestBrain = 100f;
        static int nextKind, runLeft;
        static float vtRR = 0.33f;
        static Body lastBody;

        public static float Eval(Body b, float offset, float vanilla)
        {
            if (b.defibShockedFrames > 0) return vanilla;   // keep the shock artifact
            if (!b.alive) return 0f;                        // flatline only when dead

            if (b != lastBody) return vanilla;
            // The monitor draws one pixel per 28 ms and joins it to the previous pixel with a vertical line.
            // A 90 ms QRS is only ~3 pixels, so point sampling randomly skips the R or S peak.
            // Scan the whole 28 ms window and keep the extreme farthest from the last pixel instead.
            // Take the window's max/min only if it pokes out beyond both the previous pixel and the window end
            // (a real peak inside the window); otherwise use the end value, so smooth waves don't zig-zag.
            float end = Raw(b, offset, vanilla), mx = end, mn = end;
            for (int k = 1; k < 7; k++)
            {
                float v = Raw(b, offset + k * 0.004f, vanilla);
                if (v > mx) mx = v;
                if (v < mn) mn = v;
            }
            float best = end;
            float up = mx - Mathf.Max(lastOut, end), down = Mathf.Min(lastOut, end) - mn;
            if (up > 0.02f && up >= down) best = mx;
            else if (down > 0.02f) best = mn;
            lastOut = best;
            return best;
        }

        static float lastOut;

        static float Raw(Body b, float offset, float vanilla)
        {
            double now = Time.unscaledTime;
            double t = now - offset;
            float f = b.fibrillationProgress;
            float hr = b.heartRate;
            float lv = Plugin.lowVoltage.Value ? 1f - 0.55f * Mathf.Clamp01(b.hemothorax / 60f) : 1f;

            if (b.inCardiacArrest)
            {
                // Game "arrest" is shockable (defib always succeeds) -> show pulseless VF decaying from coarse to fine.
                // coarse -> fine follows the brain's decline since the arrest began, not a clock:
                // independent of timescale/run settings, and it's always fine VF by the time the brain dies
                float left = arrestBrain > 0f ? Mathf.Clamp01(b.brainHealth / arrestBrain) : 0f;
                float amp = Mathf.Lerp(0.12f, 0.7f, left);
                return VF(t) * amp * lv;
            }
            float beatsSig = 0f;
            for (int i = 0; i < beats.Count; i++) beatsSig += Wave(beats[i], (float)(t - beats[i].t0), b);

            if (!Plugin.staging.Value)
            {
                // vanilla arrhythmia blend on top of the corrected sinus beat
                float mix = b.fibrillationProgress / 90f;
                return Mathf.Lerp(beatsSig * lv, vanilla, Mathf.Clamp01(mix));
            }

            // VT turns into flutter by itself; VF blends in over 95-100, then the arrest branch above takes over
            float wVf = Smooth(95f, 100f, f);
            float sig = Mathf.Lerp(beatsSig, VF(t) * 0.75f, wVf);
            return sig * lv;
        }

        // called once per frame from Body.HandleCirculation (player only)
        public static void Tick(Body b)
        {
            double now = Time.unscaledTime;
            if (b != lastBody || now < lastNow || now - lastNow > 2.0) Reset(b, now);
            lastNow = now;

            if (!b.alive || b.inCardiacArrest)
            {
                if (b.inCardiacArrest && arrestStart < 0) { arrestStart = now; arrestBrain = b.brainHealth; }
                nextT = now + 0.5;   // first beat comes a moment after ROSC
                beats.Clear();
                return;
            }
            arrestStart = -1;
            if (b.defibShockedFrames > 0 && nextT < now + 0.5) nextT = now + 0.5;

            float f = Plugin.staging.Value ? b.fibrillationProgress : Mathf.Min(b.fibrillationProgress, 14f);
            while (nextT <= now)
            {
                AddBeat(nextT, nextKind, b, f, b.heartRate);
                if (nextT < now - 2.0) nextT = now;
            }
            // thump when a beat's R wave passes
            for (int i = 0; i < beats.Count; i++)
            {
                var bt = beats[i];
                if (bt.thumped) continue;
                double rT = bt.t0 + (bt.kind == Normal ? bt.pr + bt.qrs * 0.45f : 0.05f);
                if (now < rT) continue;
                bt.thumped = true;
                beats[i] = bt;
                if (now - rT < 0.25) Thump(b);   // vanilla also thumps at any rate
            }
        }

        // same clip/volume rules as vanilla Body.HandleCirculation
        static void Thump(Body b)
        {
            var cam = PlayerCamera.main;
            bool monitor = cam.woundView.activeSelf;
            if (!monitor && !b.isCriticallyDying) return;
            string clip = "heartthump";
            if (b.isCriticallyDying)
                clip = monitor && !WorldGeneration.unchipped ? "heartthump-heavy-monitor" : "heartthump-heavy";
            float volume = 1f - (b.fibrillationProgress - 50f) / 80f;
            Sound.Play(clip, Vector2.zero, twoDimensional: true, pitchShift: false, null, volume, 1f, noReverb: true, ignoreMixer: true);
        }

        static void Reset(Body b, double now)
        {
            lastBody = b;
            beats.Clear();
            nextT = now;
            nextKind = Normal;
            runLeft = 0;
            lastNow = now;
            arrestStart = -1;
        }

        static void AddBeat(double T, int kind, Body b, float f, float hr)
        {
            float rr = 60f / Mathf.Max(hr, 20f);
            var beat = new Beat { t0 = T, kind = kind };
            if (kind == Normal)
            {
                float temp = b.temperature;
                bool cold = Plugin.jWave.Value;
                float qtFactor = cold ? 1f + 0.035f * Mathf.Max(0f, 35f - temp) : 1f;
                beat.pr = 0.16f;
                beat.qrs = 0.09f;
                beat.qt = 0.40f * Mathf.Sqrt(rr) * qtFactor;          // Bazett, QTc 400 ms
                float need = beat.pr + beat.qt + 0.04f;
                if (need > rr) { float k = rr / need; beat.pr *= k; beat.qt *= k; beat.qrs = Mathf.Min(beat.qrs, beat.qt * 0.3f); }
                beat.j = cold ? 0.6f * Mathf.Clamp01((33f - temp) / 8f) : 0f;   // Osborn wave: from 33C, 0.3 of R at 29C, 0.6 at 25C
            }
            if (kind == Ventricular) { beat.per = vtRR; beat.fib = f; }
            beats.Add(beat);
            if (beats.Count > 6) beats.RemoveAt(0);

            // decide the next beat
            float pPvc = f < 15f ? 0f : Mathf.Lerp(0.06f, 0.35f, Mathf.InverseLerp(15f, 50f, f));
            float pRun = Mathf.InverseLerp(30f, 45f, f) * 0.35f + Mathf.InverseLerp(45f, 50f, f) * 0.3f;   // couplets/triplets from 30, runs dominate by 50
            float pSus = Smooth(49f, 52f, f);   // sustained VT from 50, same line as the game's 室性心动过速 moodle
            double r = rng.NextDouble();
            if (kind == Normal)
            {
                sinusNext = T + rr;   // the sinus node keeps its own clock through ectopy
                if (r < pSus) { SetVtRate(hr); nextKind = Ventricular; nextT = T + beat.pr + rr * 0.55f; }
                else if (r < pSus + pRun) { SetVtRate(hr); runLeft = RunExtraBeats(f); nextKind = Ventricular; nextT = T + beat.pr + rr * 0.55f; }
                else if (r < pSus + pRun + pPvc) { runLeft = 0; nextKind = Ventricular; nextT = T + beat.pr + rr * 0.55f; }
                else { nextKind = Normal; nextT = sinusNext; }
            }
            else
            {
                if (runLeft > 0) { runLeft--; SetVtRate(hr); nextKind = Ventricular; nextT = T + vtRR; }
                else if (r < pSus) { SetVtRate(hr); nextKind = Ventricular; nextT = T + vtRR; }
                else
                {
                    // first sinus P whose QRS falls outside the ventricular refractory period conducts;
                    // the ones before it are blocked -> compensatory pause
                    double sn = sinusNext;
                    while (sn + 0.16 < T + 0.42) sn += rr;
                    nextKind = Normal; nextT = sn;
                    var lastV = beats[beats.Count - 1];
                    lastV.tail = true;
                    beats[beats.Count - 1] = lastV;
                }
                // the rate may have changed while scheduling: stretch this beat to meet the next one exactly,
                // otherwise the triangle/sine wave gets a gap or a double-up at every seam
                if (nextKind == Ventricular)
                {
                    var last = beats[beats.Count - 1];
                    last.per = (float)(nextT - T);
                    beats[beats.Count - 1] = last;
                }
            }
        }

        // ventricular rate = the game's heart rate (which already climbs with fibrillation), so the monitor
        // matches the BPM readout and every mechanic keyed to it
        // extra ventricular beats after the first: 1-3 (couplet..short run) up to 45, then runs lengthen
        // smoothly toward ~25 beats at 50 so the switch to sustained VT has no visible step
        static int RunExtraBeats(float f)
        {
            float mean = f < 45f ? 2f : Mathf.Lerp(2f, 24f, Mathf.InverseLerp(45f, 50f, f) * Mathf.InverseLerp(45f, 50f, f));
            return Mathf.Max(1, Mathf.RoundToInt(mean * (0.5f + (float)rng.NextDouble())));
        }

        static void SetVtRate(float hr) { vtRR = 60f / Mathf.Max(hr, 20f); }   // exactly the game HR (>=20 outside arrest)

        static float Wave(Beat bt, float s, Body b)
        {
            if (s < 0f) return 0f;
            if (bt.kind == Ventricular)
            {
                // wide bizarre QRS, no P, discordant T
                float tmpl = s > 0.5f ? 0f : 0.9f * G(s, 0.05f, 0.03f) - 0.55f * G(s, 0.125f, 0.03f) - 0.3f * G(s, 0.3f, 0.06f);
                if (bt.fib <= 36f) return tmpl;
                // ventricular complexes fuse into a triangle wave (36-65, faint at 40), then round off into flutter's sine (71-77, flutter from 75).
                // Phase-aligned with the template (peak at 50 ms) so blending doesn't cancel amplitude.
                float x = s / bt.per;
                float sn = x < 1f ? Mathf.Sin(2f * Mathf.PI * (x + 0.25f - 0.05f / bt.per)) : 0f;
                // the wave climbs again after its trough to meet the next ventricular complex; on the last one of a
                // run (or a lone PVC) ease from the trough back to baseline instead
                float xTrough = 0.5f + 0.05f / bt.per;
                if (bt.tail && x > xTrough && x < 1f) sn = -(1f - Smooth(xTrough, 1f, x));
                float tri = x < 1f ? Mathf.Asin(sn) * (2f / Mathf.PI) : 0f;
                float periodic = 0.9f * Mathf.Lerp(tri, sn, Smooth(71f, 77f, bt.fib));
                return Mathf.Lerp(tmpl, periodic, Mathf.Pow(Mathf.Clamp01((bt.fib - 36f) / 29f), 0.8f));
            }
            float pr = bt.pr, qrs = bt.qrs, qt = bt.qt;
            if (s > pr + qt + 0.1f) return 0f;
            float c;
            if (s < pr) c = Mathf.Lerp(-0.1f, 0.164f, s / pr);
            else if (s < pr + qrs)
            {
                float u = (s - pr) / qrs;   // Q .164 -> R .285 -> S .372 -> J .441
                c = u < 0.45f ? Mathf.Lerp(0.164f, 0.285f, u / 0.45f)
                  : u < 0.72f ? Mathf.Lerp(0.285f, 0.372f, (u - 0.45f) / 0.27f)
                  : Mathf.Lerp(0.372f, 0.441f, (u - 0.72f) / 0.28f);
            }
            else if (s < pr + qt) c = Mathf.Lerp(0.441f, 0.86f, (s - pr - qrs) / (qt - qrs));
            else c = Mathf.Lerp(0.86f, 1f, (s - pr - qt) / 0.1f);
            float v = b.heartCurveNormal.Evaluate(c);
            if (Plugin.fixST.Value) v += StLift(c);
            if (bt.j > 0f) v += bt.j * G(s, pr + qrs + 0.03f, 0.032f);   // wide enough to span 2-3 monitor pixels
            return v;
        }

        static float StLift(float c)
        {
            if (c >= 0.372f && c < 0.441f) return 0.291f * (c - 0.372f) / 0.069f;
            if (c >= 0.441f && c < 0.62f) return 0.291f * (1f - (c - 0.441f) / 0.179f);
            return 0f;
        }

        static float VF(double t)
        {
            float x = (float)(t % 1000.0);
            float v = 0.5f * Mathf.Sin(2f * Mathf.PI * 4.3f * x + 1.7f * Mathf.Sin(0.9f * x))
                    + 0.35f * Mathf.Sin(2f * Mathf.PI * 6.1f * x + 2.3f * Mathf.Sin(0.53f * x + 1f))
                    + 0.25f * Mathf.Sin(2f * Mathf.PI * 7.7f * x + 1.1f * Mathf.Sin(1.37f * x + 2f));
            return v * (0.55f + 0.45f * Mathf.PerlinNoise(x * 0.7f, 0.3f));
        }

        static float G(float x, float mu, float sig) { float z = (x - mu) / sig; return Mathf.Exp(-0.5f * z * z); }
        static float Smooth(float a, float b, float x) { float u = Mathf.Clamp01((x - a) / (b - a)); return u * u * (3f - 2f * u); }
    }
}
