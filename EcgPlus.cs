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
        internal static ConfigEntry<bool> on, fixST, staging, jWave, lowVoltage, textFixes, arrestShock;

        void Awake()
        {
            on = Config.Bind("General", "Enabled", true, "Use the reworked ECG waveform.");
            fixST = Config.Bind("Waveform", "FixSTSegment", true, "Corrected sinus beat (flat ST, lead II proportions) instead of the vanilla curve.");
            staging = Config.Bind("Waveform", "ArrhythmiaStaging", true, "PVCs -> runs of VT -> sustained VT -> flutter -> VF instead of the vanilla blend.");
            jWave = Config.Bind("Waveform", "Hypothermia", true, "J (Osborn) wave and QT prolongation when cold.");
            lowVoltage = Config.Bind("Waveform", "HemothoraxLowVoltage", true, "Smaller complexes with hemothorax.");
            textFixes = Config.Bind("Text", "Fixes", true, "Chinese text fixes: 心率不齐 -> 心律不齐, fibrillation3 -> 室扑, cardiac arrest description");
            arrestShock = Config.Bind("Gameplay", "ArrestShockRework", true, "Shocks in cardiac arrest restart the heart 90% of the time while VF is coarse, falling to 30% as it decays to fine VF (vanilla: 20%). Most restarts keep fibrillation at 60-80 and can slide back into arrest. Clean restarts stay at 20%, same as vanilla.");
            new Harmony("local.cu.ecgplus").PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("ECG Plus loaded");
        }

        void Update()
        {
            try { EcgView.Tick(); } catch (Exception e) { Logger.LogError(e); }
            try { DefibScreenPatch.Watch(); } catch (Exception e) { Logger.LogError(e); }
        }
    }

    [HarmonyPatch(typeof(Body), nameof(Body.GetECGHeight))]
    static class EcgPatch
    {
        static void Postfix(Body __instance, float offset, ref float __result)
        {
            if (!Plugin.on.Value) return;
            // vanilla ECGVisualizer maps the value straight to a pixel row; anything outside -1..1 indexes out of the texture
            try { __result = Mathf.Clamp(Rhythm.Eval(__instance, offset, __result), -1f, 1f); }
            catch (Exception) { /* fall back to vanilla value */ }
        }
    }

    // Heartbeat sound follows the displayed beats: vanilla thump is suppressed, ours plays at each R wave.
    [HarmonyPatch(typeof(ECGVisualizer), "Update")]
    static class VisualizerPatch
    {
        static void Prefix(ECGVisualizer __instance) { Rhythm.currentVis = __instance; }
        static void Postfix() { Rhythm.currentVis = null; }
    }

    // every footstep (animation event) jolts the electrodes: one impulse on the trace
    [HarmonyPatch(typeof(Body), nameof(Body.FootStep))]
    static class FootStepPatch
    {
        static void Postfix(Body __instance)
        {
            if (!Plugin.on.Value || !(PlayerCamera.main != null && PlayerCamera.main.body == __instance)) return;
            try { Rhythm.Step(__instance); } catch (Exception) { }
        }
    }

    // swinging / striking: the chest electrodes sit on the pectoral muscles -> a burst of muscle (EMG) noise
    [HarmonyPatch(typeof(Body), nameof(Body.Attack))]
    static class AttackPatch
    {
        // vanilla returns true only when the swing hits something; a swing at thin air returns false.
        // A swing happened when the cooldown was ready before and is running after.
        static void Prefix(Body __instance, out bool __state) => __state = __instance.attackCooldown <= 0f;
        static void Postfix(Body __instance, AttackInfo atk, bool __state)
        {
            if (!__state || __instance.attackCooldown <= 0f) return;
            if (!Plugin.on.Value || !(PlayerCamera.main != null && PlayerCamera.main.body == __instance)) return;
            try { Rhythm.Swing(atk); } catch (Exception) { }
        }
    }

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
            ConsoleScript.Commands.Add(new Command("ecgview", "Opens or closes a large ECG monitor (Esc also closes). ecgview mark waves|types|all|off [force] labels PQRST/J and beat types (below 150 bpm unless forced).", delegate (string[] args)
            {
                if (args.Length >= 3 && args[1] == "mark")
                {
                    string msg = EcgView.SetMarks(args[2], args.Length >= 4 && args[3] == "force");
                    Traverse.Create(ConsoleScript.instance).Method("LogToConsole", new[] { typeof(string) }).GetValue(msg);
                    return;
                }
                EcgView.Toggle();
            }, new Dictionary<int, List<string>> { { 0, new List<string> { "mark" } }, { 1, new List<string> { "waves", "types", "all", "off" } }, { 2, new List<string> { "force" } } },
               ("mark", "optional: mark"), ("what", "waves / types / all / off"), ("force", "optional: also label above 150 bpm")));
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

        internal struct Beat
        {
            public double t0;          // P onset (normal) or QRS onset (ventricular), unscaled seconds
            public int kind;
            public float pr, qrs, qt, j;
            public bool thumped;
            public float per, fib;   // ventricular: cycle length and fibrillation when generated
            public bool tail;   // ventricular beat followed by a normal beat: no upswing into a next complex
            public int run;     // ventricular: id of the run this beat belongs to (single PVC = run of 1)
            public bool sus;    // ventricular: scheduled as sustained VT rather than a counted run
            public float amp0, amp1;   // ventricular: amplitude at the start / end of this cycle (flutter degrading, fib 80-95)
        }

        static readonly List<Beat> beats = new List<Beat>();
        static readonly System.Random rng = new System.Random();
        static double nextT = -1, sinusNext, lastNow, arrestStart = -1;
        static float arrestBrain = 100f;
        static int nextKind, runLeft;
        static float vtRR = 0.33f;
        static int runCounter, curRun;
        static float lastAmp = 1f;
        static bool curSus;
        internal static List<Beat> Beats => beats;

        // Sinus P waves that keep firing on the sinus clock while the ventricles are busy (blocked by a PVC's
        // refractory period, or dissociated during a run): drawn on their own, landing on the PVC's ST/T as a notch.
        internal struct PWave { public double t0; public float amp; }
        static readonly List<PWave> buriedP = new List<PWave>();
        internal static List<PWave> BuriedP => buriedP;
        internal const float PPeak = 0.057f;   // P peak after P onset (same as the sinus beat's P at PR 160 ms)
        static float sinusRR = 0.857f;
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
            float lastOut = 0f;
            if (currentVis != null) lastOuts.TryGetValue(currentVis, out lastOut);
            float up = mx - Mathf.Max(lastOut, end), down = Mathf.Min(lastOut, end) - mn;
            if (up > 0.02f && up >= down) best = mx;
            else if (down > 0.02f) best = mn;
            if (currentVis != null) lastOuts[currentVis] = best;
            // the vanilla monitor maps -1..1 to the full texture height, so sinus R already touches the top row;
            // shrink a little so the taller PVC (~1.15) isn't clipped (same 0.85 as the big view)
            return best * 0.85f;
        }

        // several monitors sample at once (health panel, unconscious overlay, defib minigame): keep the
        // peak-sampler memory per monitor, set by the ECGVisualizer.Update prefix
        internal static object currentVis;
        static readonly Dictionary<object, float> lastOuts = new Dictionary<object, float>();

        // continuous sample for the big view (fine time steps, no peak sampler needed)
        internal static float Sample(Body b, float offset)
        {
            if (b.defibShockedFrames > 0) return (float)(rng.NextDouble() * 2.0 - 1.0);
            if (!b.alive || b != lastBody) return 0f;
            return Raw(b, offset, 0f);
        }

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
                return Fit(VF(t) * amp * lv + Artifact(t));
            }
            Schedule(b, now);
            float beatsSig = 0f;
            for (int i = 0; i < beats.Count; i++) beatsSig += Wave(beats[i], (float)(t - beats[i].t0), b);
            for (int i = 0; i < buriedP.Count; i++)
            {
                float sp = (float)(t - buriedP[i].t0);
                if (sp > 0f && sp < 0.14f) beatsSig += buriedP[i].amp * G(sp, PPeak, 0.021f);
            }

            if (!Plugin.staging.Value)
            {
                // vanilla arrhythmia blend on top of the corrected sinus beat
                float mix = b.fibrillationProgress / 90f;
                return Fit(Mathf.Lerp(beatsSig * lv, vanilla, Mathf.Clamp01(mix)) + Artifact(t));
            }

            // VT turns into flutter by itself; VF blends in over 95-100, then the arrest branch above takes over
            float wVf = Smooth(95f, 100f, f);
            float sig = Mathf.Lerp(beatsSig, VF(t) * 0.75f, wVf);
            return Fit(sig * lv + Artifact(t));
        }

        // Keep the trace on screen: the monitors show |v| up to ~1.18 (0.85 scale). Artifacts riding on an
        // R wave used to push it past the edge and flatten its tip. Above 0.95 compress smoothly toward 1.15,
        // so peaks stay pointed (sinus R ~1.0 is barely touched, the taller PVC still stands out).
        static float Fit(float v)
        {
            float a = Mathf.Abs(v);
            if (a <= 0.95f) return v;
            return Mathf.Sign(v) * (0.95f + 0.2f * Tanh((a - 0.95f) / 0.2f));
        }

        static float Tanh(float x) { float e = Mathf.Exp(2f * x); return (e - 1f) / (e + 1f); }

        // ---- artifacts (not from the heart): electrode/muscle noise picked up by the leads ----
        // Intensities are updated once per frame in Tick and eased, so every monitor draws the same smooth noise.
        static float motionAmp, shiverAmp;

        // step / landing impulses: skin and electrode jolted at foot strike
        struct Jolt { public double t0; public float amp; }
        static readonly List<Jolt> jolts = new List<Jolt>();
        static int stepSide = 1;
        static float lastVy;
        static bool wasGrounded = true;

        internal static void Step(Body b)
        {
            float vx = b.rb != null ? Mathf.Abs(b.rb.velocity.x) : 0f;
            stepSide = -stepSide;   // left / right foot pull the leads a little differently
            float amp = (0.12f + 0.18f * Mathf.Clamp01((vx - 2f) / 6f)) * (stepSide > 0 ? 1f : 0.7f);
            AddJolt(amp * (0.85f + 0.3f * (float)rng.NextDouble()));
        }

        // muscle bursts from arm work (attacks, digging, chopping), plus jolts when getting hit
        struct Burst { public double t0; public float amp, dur; }
        static readonly List<Burst> bursts = new List<Burst>();
        static float lastPain = -1f;

        internal static void Swing(AttackInfo atk)
        {
            // heavier, slower swings (longer cooldown) tense more muscle for longer; guns (no physical swing) only kick
            float cd = Mathf.Clamp(atk.cooldown, 0.2f, 2f);
            if (atk.physicalSwing)
            {
                bursts.Add(new Burst { t0 = Time.unscaledTime, amp = 0.12f + 0.12f * Mathf.InverseLerp(0.2f, 1.5f, cd), dur = 0.25f + 0.25f * cd });
                if (bursts.Count > 6) bursts.RemoveAt(0);
            }
            AddJolt(atk.physicalSwing ? 0.15f : 0.2f);
        }

        static void AddJolt(float amp)
        {
            jolts.Add(new Jolt { t0 = Time.unscaledTime, amp = amp });
            if (jolts.Count > 8) jolts.RemoveAt(0);
        }

        static void UpdateArtifacts(Body b, float dt)
        {
            // motion: running/falling, climbing, working out -> baseline wander + muscle (EMG) fuzz (steps add their own jolts)
            float speed = b.rb != null ? b.rb.velocity.magnitude : 0f;
            // landing: a jolt scaled by how hard the body hit the ground
            if (b.grounded && !wasGrounded && lastVy < -6f) AddJolt(Mathf.Min(0.6f, 0.04f * (-lastVy)));
            wasGrounded = b.grounded;
            // getting hit: a sudden rise in pain jolts the leads
            if (lastPain >= 0f && b.averagePain - lastPain > 4f && b.defibShockedFrames <= 0)
                AddJolt(Mathf.Min(0.5f, 0.15f + 0.02f * (b.averagePain - lastPain)));
            lastPain = b.averagePain;
            lastVy = b.rb != null ? b.rb.velocity.y : 0f;
            float m = 0.15f * Mathf.Clamp01(speed / 6f) + ((bool)b.currentClimbable ? 0.15f : 0f) + (b.exercising ? 0.25f : 0f);
            motionAmp = Mathf.MoveTowards(motionAmp, Mathf.Min(m, 0.45f), dt * 1.5f);
            // shivering: from ~35.5 C down to ~32 C, then the body stops shivering (and only while conscious)
            float T = b.temperature;
            float s = b.conscious ? (1f - Smooth(34.4f, 35.6f, T)) * Smooth(31.5f, 32.5f, T) : 0f;
            shiverAmp = Mathf.MoveTowards(shiverAmp, s, dt * 0.5f);
        }

        static float Artifact(double t)
        {
            float v = 0f;
            for (int i = 0; i < jolts.Count; i++)
            {
                float s = (float)(t - jolts[i].t0);
                // sharp deflection at heel strike, then a slower rebound
                if (s > 0f && s < 0.4f) v += jolts[i].amp * (G(s, 0.035f, 0.018f) - 0.45f * G(s, 0.13f, 0.05f));
            }
            float x = (float)(t % 1000.0);
            for (int i = 0; i < bursts.Count; i++)
            {
                float s = (float)(t - bursts[i].t0), d = bursts[i].dur;
                if (s <= 0f || s > d) continue;
                float env = Smooth(0f, 0.04f, s) * (1f - Smooth(d * 0.4f, d, s));
                v += bursts[i].amp * env * (Mathf.PerlinNoise(x * 45f, 9.7f) - 0.5f) * 2f;
            }
            if (motionAmp < 0.005f && shiverAmp < 0.005f) return v;
            if (motionAmp >= 0.005f)
            {
                float wander = (Mathf.PerlinNoise(x * 0.9f, 1.7f) - 0.5f) * 1.6f;          // slow baseline drift
                float emg = (Mathf.PerlinNoise(x * 35f, 4.1f) - 0.5f) * 0.7f;               // muscle fuzz
                v += motionAmp * (wander + emg);
            }
            if (shiverAmp >= 0.005f)
            {
                // fine irregular 8-14 Hz tremor, waxing and waning
                float tremor = Mathf.Sin(2f * Mathf.PI * 9.1f * x + 2f * Mathf.Sin(1.3f * x))
                             + 0.7f * Mathf.Sin(2f * Mathf.PI * 12.7f * x + 1.5f * Mathf.Sin(0.7f * x + 1f));
                float swell = 0.4f + 0.6f * Mathf.PerlinNoise(x * 1.1f, 7.3f);
                v += shiverAmp * 0.055f * tremor * swell;
            }
            return v;
        }

        // called once per frame from Body.HandleCirculation (player only)
        public static void Tick(Body b)
        {
            double now = Time.unscaledTime;
            if (b != lastBody || now < lastNow || now - lastNow > 2.0) Reset(b, now);
            float dt = (float)(now - lastNow);
            lastNow = now;
            UpdateArtifacts(b, dt);
            UpdateBreathing(b, dt);

            if (!b.alive || b.inCardiacArrest)
            {
                if (b.inCardiacArrest && arrestStart < 0) { arrestStart = now; arrestBrain = b.brainHealth; }
                nextT = now + 0.5;   // first beat comes a moment after ROSC
                beats.Clear();
                buriedP.Clear();
                return;
            }
            arrestStart = -1;
            if (b.defibShockedFrames > 0 && nextT < now + 0.5) nextT = now + 0.5;

            Schedule(b, now);
            // thump when a beat's R wave passes
            for (int i = 0; i < beats.Count; i++)
            {
                var bt = beats[i];
                if (bt.thumped) continue;
                double rT = bt.t0 + (bt.kind == Normal ? bt.pr + bt.qrs * 0.45f : PvcR);
                if (now < rT) continue;
                bt.thumped = true;
                beats[i] = bt;
                float strength = BeatStrength(bt, rT);
                lastR = rT;
                if (now - rT < 0.25 && strength > 0.2f) Thump(b, strength);   // vanilla also thumps at any rate
            }
        }

        // Generate every beat that starts by "now". Also called from the samplers: a monitor may draw before
        // HandleCirculation runs this frame, and sampling past the last scheduled beat drew a dip to baseline
        // right before the next complex.
        static void Schedule(Body b, double now)
        {
            if (b != lastBody || !b.alive || b.inCardiacArrest) return;
            float f = Plugin.staging.Value ? b.fibrillationProgress : Mathf.Min(b.fibrillationProgress, 14f);
            while (nextT <= now)
            {
                AddBeat(nextT, nextKind, b, f, b.heartRate);
                if (nextT < now - 2.0) nextT = now;
            }
        }

        // same clip/volume rules as vanilla Body.HandleCirculation
        // Pulse strength from filling time (Frank-Starling): a premature beat ejects little blood (pulse deficit,
        // its thump is faint or missing); the beat after the compensatory pause fills longer and hits harder.
        static double lastR = -1;
        static float BeatStrength(Beat bt, double rT)
        {
            if (lastR < 0) return 1f;
            float c = (float)(rT - lastR) / Mathf.Max(sinusRR, 0.2f);
            float s = c < 1f ? Mathf.Lerp(0.1f, 1f, Smooth(0.4f, 0.95f, c)) : 1f + 0.4f * Smooth(1.1f, 1.8f, c);
            // sustained VT keeps a steady (weak) beat like vanilla instead of going silent
            if (bt.kind == Ventricular && bt.sus) s = Mathf.Max(s, 0.5f);
            return s;
        }

        static void Thump(Body b, float strength)
        {
            var cam = PlayerCamera.main;
            bool monitor = cam.woundView.activeSelf || EcgView.IsOpen || (DefibScreenPatch.monitor != null && DefibScreenPatch.monitor.Alive);
            if (!monitor && !b.isCriticallyDying) return;
            string clip = "heartthump";
            if (b.isCriticallyDying)
                clip = monitor && !WorldGeneration.unchipped ? "heartthump-heavy-monitor" : "heartthump-heavy";
            float volume = (1f - (b.fibrillationProgress - 50f) / 80f) * strength;
            Sound.Play(clip, Vector2.zero, twoDimensional: true, pitchShift: false, null, volume, 1f, noReverb: true, ignoreMixer: true);
        }

        static void Reset(Body b, double now)
        {
            lastBody = b;
            beats.Clear();
            buriedP.Clear();
            nextT = now;
            nextKind = Normal;
            runLeft = 0;
            lastNow = now;
            arrestStart = -1;
            lastR = -1;
        }

        // Respiratory sinus arrhythmia: the sinus rate speeds up on inspiration and slows on expiration.
        // Strong at rest and in sleep, fades at high heart rates; gone when not breathing, unconscious
        // (other than sleep) or critically ill - a rigid, metronome rhythm is itself a bad sign.
        static float respPhase, rsaAmp;
        static void UpdateBreathing(Body b, float dt)
        {
            float bpm = Mathf.Max(4f, b.respiratoryRate * 0.25f);   // same breaths/min as the game's readout
            respPhase = (respPhase + dt * bpm / 60f) % 1f;
            float a = 0.06f * Mathf.Clamp01((120f - b.heartRate) / 50f);
            if (!b.breathing || b.isCriticallyDying || (!b.conscious && !b.sleeping)) a = 0f;
            else if (b.sleeping) a *= 1.3f;
            rsaAmp = Mathf.MoveTowards(rsaAmp, a, dt * 0.02f);
        }

        static void AddBeat(double T, int kind, Body b, float f, float hr)
        {
            float rr = 60f / Mathf.Max(hr, 20f);
            var beat = new Beat { t0 = T, kind = kind };
            if (kind == Normal)
            {
                rr *= 1f - rsaAmp * Mathf.Sin(2f * Mathf.PI * respPhase);   // shorter RR while breathing in
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
            if (kind == Ventricular)
            {
                beat.per = vtRR; beat.fib = f; beat.run = curRun; beat.sus = curSus;
                // flutter degrading toward VF: beat-to-beat size varies more and more (+-0 at 80 .. +-30% at 95),
                // ramped across the cycle so the seams stay continuous
                // 95-100: the flutter itself breaks up (+-30% -> +-80%) while VF blends in
                float irr = Mathf.InverseLerp(80f, 95f, f), brk = Smooth(95f, 100f, f);
                beat.amp0 = lastAmp;
                beat.amp1 = Mathf.Max(0.15f, 1f + ((float)rng.NextDouble() * 2f - 1f) * (0.3f * irr + 0.5f * brk));
                lastAmp = beat.amp1;
            }
            else lastAmp = 1f;
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
                sinusRR = rr;
                if (r < pSus) { SetVtRate(hr); curRun = ++runCounter; curSus = true; nextKind = Ventricular; nextT = T + beat.pr + rr * 0.55f; }
                else if (r < pSus + pRun) { SetVtRate(hr); curRun = ++runCounter; curSus = false; runLeft = RunExtraBeats(f); nextKind = Ventricular; nextT = T + beat.pr + rr * 0.55f; }
                else if (r < pSus + pRun + pPvc) { runLeft = 0; curRun = ++runCounter; curSus = false; nextKind = Ventricular; nextT = T + beat.pr + rr * 0.55f; }
                else { nextKind = Normal; nextT = sinusNext; }
            }
            else
            {
                float jit = 1f + ((float)rng.NextDouble() * 2f - 1f) * (0.1f * Mathf.InverseLerp(80f, 95f, f) + 0.3f * Smooth(95f, 100f, f));   // +-10% at 95, +-40% at 100
                if (runLeft > 0) { runLeft--; SetVtRate(hr); nextKind = Ventricular; nextT = T + vtRR * jit; }
                else if (r < pSus) { SetVtRate(hr); curSus = true; nextKind = Ventricular; nextT = T + vtRR * jit; }
                else
                {
                    // first sinus P whose QRS falls outside the ventricular refractory period conducts;
                    // the ones before it are blocked -> compensatory pause (each blocked P still shows, buried in the T)
                    double sn = sinusNext;
                    while (sn + 0.16 < T + 0.42) { AddBuriedP(sn, f); sn += rr; }
                    nextKind = Normal; nextT = sn;
                    var lastV = beats[beats.Count - 1];
                    lastV.tail = true;
                    beats[beats.Count - 1] = lastV;
                }
                // the rate may have changed while scheduling: stretch this beat to meet the next one exactly,
                // otherwise the triangle/sine wave gets a gap or a double-up at every seam
                if (nextKind == Ventricular)
                {
                    // AV dissociation during a run: the sinus node keeps its own (slower) rate
                    while (sinusNext < nextT) { AddBuriedP(sinusNext, f); sinusNext += sinusRR; }
                    var last = beats[beats.Count - 1];
                    last.per = (float)(nextT - T);
                    beats[beats.Count - 1] = last;
                }
            }
        }

        static void AddBuriedP(double t0, float f)
        {
            float amp = 0.12f * (1f - TriWeight(f));   // lost in the fused triangle / flutter wave
            if (amp <= 0.01f) return;
            buriedP.Add(new PWave { t0 = t0, amp = amp });
            if (buriedP.Count > 8) buriedP.RemoveAt(0);
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
                float tmpl = Pvc(s);
                if (bt.fib <= 36f) return tmpl;
                // ventricular complexes fuse into a triangle wave (36-65, faint at 40), then round off into flutter's sine (71-77, flutter from 75).
                // Phase-aligned with the template (R peak at PvcR) so blending doesn't cancel amplitude.
                float x = s / bt.per;
                float sn = x < 1f ? Mathf.Sin(2f * Mathf.PI * (x + 0.25f - PvcR / bt.per)) : 0f;
                // the wave climbs again after its trough to meet the next ventricular complex; on the last one of a
                // run (or a lone PVC) ease from the trough back to baseline instead
                float xTrough = 0.5f + PvcR / bt.per;
                if (bt.tail && x > xTrough && x < 1f) sn = -(1f - Smooth(xTrough, 1f, x));
                float tri = x < 1f ? Mathf.Asin(sn) * (2f / Mathf.PI) : 0f;
                float periodic = 0.9f * Mathf.Lerp(tri, sn, Smooth(71f, 77f, bt.fib));
                float ampV = Mathf.Lerp(bt.amp0, bt.amp1, Mathf.Clamp01(x));
                return Mathf.Lerp(tmpl, periodic, Mathf.Pow(Mathf.Clamp01((bt.fib - 36f) / 29f), 0.8f)) * ampV;
            }
            float pr = bt.pr, qrs = bt.qrs, qt = bt.qt;
            if (s > pr + qt + 0.1f) return 0f;
            if (Plugin.fixST.Value)
            {
                float sv = Sinus(s, pr, qrs, qt);
                if (bt.j > 0f) sv += bt.j * G(s, pr + qrs + 0.03f, 0.032f);
                return sv;
            }
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
            if (bt.j > 0f) v += bt.j * G(s, pr + qrs + 0.03f, 0.032f);   // wide enough to span 2-3 monitor pixels
            return v;
        }

        // Ventricular complex (lead II, RV-origin PVC): wide monophasic R (~150 ms), whose downstroke runs straight
        // into a deep discordant T - no separate S wave and no return to baseline in between (ST and T merge).
        // The inverted T is asymmetric like a real secondary T inversion: slow downslope, fast return.
        internal const float PvcR = 0.075f, PvcT = 0.34f;
        static float Pvc(float s)
        {
            if (s <= 0f || s > 0.62f) return 0f;
            float r = 1.25f * AG(s, PvcR, 0.048f, 0.04f);   // peak ~1.15 after the T overlap: taller than sinus R (~1.0)   // blunt upstroke: slow muscle-to-muscle conduction
            float t = 0.58f * AG(s, PvcT, 0.14f, 0.045f);
            return (r - t) * Smooth(0f, 0.05f, s);   // slurred start from baseline
        }

        static float AG(float x, float mu, float left, float right) => G(x, mu, x < mu ? left : right);

        // Sinus beat (lead II, a bit exaggerated for the small screen): S about half of R (real lead II ~1/5),
        // S returns smoothly through J onto a flat ST, then an asymmetric T (slow upstroke, quicker downstroke)
        // peaking ~2/3 of the way from J to the end of QT. Replaces the vanilla curve, whose ST dips and bumps.
        internal const float SP = 0.356f, SQ = 0.12f, SR = 0.45f, SS = 0.72f, ST = 0.65f;   // feature positions
        static float Sinus(float s, float pr, float qrs, float qt)
        {
            if (s <= 0f) return 0f;
            float L = qt - qrs;
            return 0.12f * G(s, pr * SP, pr * 0.131f)                 // P
                 - 0.22f * G(s, pr + SQ * qrs, 0.11f * qrs)           // q (deep/wide enough to survive 28 ms pixels)
                 + 1.00f * G(s, pr + SR * qrs, 0.13f * qrs)           // R
                 - 0.50f * G(s, pr + SS * qrs, 0.10f * qrs)           // S
                 + 0.30f * AG(s, pr + qrs + ST * L, 0.26f * L, 0.15f * L);   // T
        }

        static float VF(double t)
        {
            float x = (float)(t % 1000.0);
            float v = 0.5f * Mathf.Sin(2f * Mathf.PI * 4.3f * x + 1.7f * Mathf.Sin(0.9f * x))
                    + 0.35f * Mathf.Sin(2f * Mathf.PI * 6.1f * x + 2.3f * Mathf.Sin(0.53f * x + 1f))
                    + 0.25f * Mathf.Sin(2f * Mathf.PI * 7.7f * x + 1.1f * Mathf.Sin(1.37f * x + 2f));
            return v * (0.55f + 0.45f * Mathf.PerlinNoise(x * 0.7f, 0.3f));
        }

        // ---- info for the big view's annotations ----
        internal static float TriWeight(float fib) => Mathf.Pow(Mathf.Clamp01((fib - 36f) / 29f), 0.8f);
        internal static float SineWeight(float fib) => Smooth(71f, 77f, fib);
        internal static float VfWeight(float fib) => Smooth(95f, 100f, fib);
        // 1 = coarse VF right after the arrest began .. 0 = fine VF (brain lost since the arrest)
        internal static float ArrestLeft(Body b) => arrestBrain > 0f ? Mathf.Clamp01(b.brainHealth / arrestBrain) : 0f;
        internal static float ArrestAmp(Body b) => Mathf.Lerp(0.12f, 0.7f, ArrestLeft(b));

        // times (seconds after t0) of the labelled waves; below = trough (label goes under the trace)
        internal static void Features(Beat bt, List<(string name, float dt, bool below)> o)
        {
            o.Clear();
            if (bt.kind == Normal)
            {
                float pr = bt.pr, qrs = bt.qrs, qt = bt.qt;
                bool own = Plugin.fixST.Value;
                o.Add(("P", pr * SP, false));
                o.Add(("Q", own ? pr + SQ * qrs : pr, true));
                o.Add(("R", pr + SR * qrs, false));
                o.Add(("S", pr + SS * qrs, true));
                if (bt.j >= 0.1f) o.Add(("J", pr + qrs + 0.03f, false));
                o.Add(("T", pr + qrs + (own ? ST : 0.427f) * (qt - qrs), false));
            }
            else
            {
                o.Add(("R", PvcR, false));
                if (TriWeight(bt.fib) < 0.5f) o.Add(("T", PvcT, true));   // inverted, merged with ST
            }
        }

        static float G(float x, float mu, float sig) { float z = (x - mu) / sig; return Mathf.Exp(-0.5f * z * z); }
        static float Smooth(float a, float b, float x) { float u = Mathf.Clamp01((x - a) / (b - a)); return u * u * (3f - 2f * u); }
    }
}
