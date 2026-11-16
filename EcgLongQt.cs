using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EcgPlus
{
    // Long QT syndrome: a challenge run setting ("longqt", in the vanilla challenges group).
    // Off: nothing here runs, normal characters and vanilla fibrillation are untouched.
    // On: fibrillation creeps up whatever the player does, with no base rate of its own. Three triggers
    // (the real LQTS subtypes) cover nearly every state between them:
    //   LQT1  exertion: moving fast, running out of stamina, climbing, working out; doubled while swimming
    //   LQT3  rest: sitting down (the idle sit pose), sleeping, unconscious, slow heart rate (counted per second, so a
    //         character who sleeps more often but shorter, like Milky, is not punished per sleep)
    //   LQT2  stress: adrenaline, horror, pain, plus a kick on sudden loud noise or a hard hit
    // Cold speeds all of it up below 36C. Vanilla's own recovery (-0.75/s while no vanilla trigger is
    // active) is cancelled, so only amiodarone and shocks bring it down. No sudden episodes.
    // Rates are per minute of game time and are scaled by the vanilla "fibrillationrate" run setting.
    internal static class LongQt
    {
        internal const string Key = "longqt";

        static ConfigEntry<float> overall, l1Max, l1Water, l3Sleep, l3Rest, l3Brady, l2Max, coldPerDeg, qtProlong;
        static readonly Dictionary<string, ConfigEntry<float>> tunables = new Dictionary<string, ConfigEntry<float>>();

        internal static void Bind(ConfigFile cfg)
        {
            overall = F(cfg, "Overall", 1.4f, "Multiplies every long QT rate and kick below. 1.4 = about 37 fib per layer at a normal pace, one treatment (at 50-60) every ~1.5 layers.");
            l1Max = F(cfg, "Lqt1Max", 1.2f, "LQT1 (exertion), fib per minute at full speed with stamina gone.");
            l1Water = F(cfg, "Lqt1WaterMult", 2f, "LQT1 multiplier while swimming.");
            l3Sleep = F(cfg, "Lqt3Sleep", 0.8f, "LQT3 (rest), fib per minute while asleep or unconscious.");
            l3Rest = F(cfg, "Lqt3Rest", 0.4f, "LQT3, fib per minute while sitting (the character sits down after 12 s idle; small steps do not count).");
            l3Brady = F(cfg, "Lqt3Brady", 0.3f, "LQT3, extra fib per minute at heart rate 40 (from 0 at 60).");
            l2Max = F(cfg, "Lqt2Max", 1.0f, "LQT2 (stress), fib per minute at full adrenaline / horror.");
            coldPerDeg = F(cfg, "ColdPerDegree", 0.15f, "Extra rate per degree below 36C (0.15 = x1.6 at 32C).");
            qtProlong = F(cfg, "QtProlong", 1.2f, "QT length on the monitor (1.2 = QTc 480 ms instead of 400).");
        }

        static ConfigEntry<float> F(ConfigFile cfg, string key, float def, string desc)
        {
            var e = cfg.Bind("LongQT", key, def, desc);
            tunables[key.ToLowerInvariant()] = e;
            return e;
        }

        internal static bool Active
        {
            get
            {
                var rs = WorldGeneration.runSettings;
                return rs != null && rs.TryGetValue(Key, out var v) && v is bool on && on;
            }
        }

        internal static float QtFactor => Active ? qtProlong.Value : 1f;

        // ---- run setting: appended to the challenges group, off in every preset ----
        internal static void Register()
        {
            if (RunSettings.settingTypes.Exists(s => s.name == Key)) return;
            RunSettings.settingTypes.Add(new RunSettingBool(Key));
            var normal = RunSettings.GetPreset("normal").presetValues;
            if (!normal.ContainsKey(Key)) normal.Add(Key, false);
        }

        // ---- per-frame rise (called after vanilla HandleCirculation, player only) ----
        static readonly AccessTools.FieldRef<Body, bool> movingAllowed = AccessTools.FieldRefAccess<Body, bool>("movingAllowed");
        static float lastPain = -1f, lastHearing = -1f;
        internal static float r1, r3, r2, cold = 1f, avg;   // per minute, for the readout

        // per-layer stats for tuning (recorded with or without the challenge on)
        struct LayerStat { public int depth; public float game, real, gained; }
        static readonly List<LayerStat> layers = new List<LayerStat>();
        static LayerStat cur = new LayerStat { depth = -1 };

        static void TrackLayer(float dt, float gained)
        {
            if (WorldGeneration.world == null) return;
            int d = WorldGeneration.world.biomeDepth;
            if (d != cur.depth)
            {
                if (cur.depth >= 0 && cur.game > 5f) { layers.Add(cur); if (layers.Count > 12) layers.RemoveAt(0); }
                cur = new LayerStat { depth = d };
            }
            cur.game += dt;
            if (Time.timeScale > 0f) cur.real += Time.unscaledDeltaTime;
            cur.gained += gained;
        }

        static string Fmt(LayerStat l) => string.Format("layer {0}: {1:0.0} min game / {2:0.0} min real, +{3:0.0} fib", l.depth + 1, l.game / 60f, l.real / 60f, l.gained);

        internal static void Tick(Body b, float fibBefore, bool risingBefore)
        {
            if (!b.alive) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (!Active) { TrackLayer(dt, 0f); return; }

            // undo vanilla recovery: it subtracts 0.75/s while fib > 0 and nothing vanilla drives it
            if (fibBefore > 0f && !risingBefore && b.fibrillationProgress < fibBefore) b.fibrillationProgress = fibBefore;

            if (b.inCardiacArrest) { r1 = r2 = r3 = 0f; lastPain = -1f; lastHearing = -1f; TrackLayer(dt, 0f); return; }

            Vector2 v = b.rb != null ? b.rb.velocity : Vector2.zero;
            bool climbing = (bool)b.currentClimbable;
            bool swimming = b.bodyAffect != null && b.bodyAffect.wasWater;
            bool resting = b.sleeping || !b.conscious;

            // LQT1: exertion
            float move = Mathf.Clamp01(Mathf.Abs(v.x) / Mathf.Max(b.maxSpeed, 1f));
            if (climbing || swimming) move = Mathf.Max(move, Mathf.Clamp01(v.magnitude / Mathf.Max(b.maxSpeed, 1f)), 0.5f);
            float fatigue = Mathf.Clamp01((100f - b.stamina) / 70f);
            if (b.exercising) fatigue = 1f;
            r1 = resting ? 0f : l1Max.Value * (0.25f * move + 0.75f * fatigue) * (swimming ? l1Water.Value : 1f);

            // LQT3: rest
            // the vanilla idle sit pose: after 12 s without input (Body.idleTime), not working out
            bool sitting = b.idleTime > 12f && movingAllowed(b) && !b.exercising;
            float brady = b.heartRate > 0f ? Mathf.Clamp01((60f - b.heartRate) / 20f) : 0f;
            r3 = (resting ? l3Sleep.Value : (sitting ? l3Rest.Value : 0f)) + l3Brady.Value * brady;

            // LQT2: stress
            float stress = Mathf.Clamp01(b.curAdrenaline / 100f + b.horrifiedLevel / 100f + 0.5f * Mathf.Clamp01(b.averagePain / 100f));
            r2 = l2Max.Value * stress;

            // LQT2 kicks: sudden loud noise (hearing loss jumps) or a hard hit (pain jumps); shocks excluded
            float kick = 0f;
            if (lastHearing >= 0f && b.hearingLoss - lastHearing > 5f) kick += Mathf.Min(3f, 0.15f * (b.hearingLoss - lastHearing));
            if (lastPain >= 0f && b.averagePain - lastPain > 8f && b.defibShockedFrames <= 0) kick += Mathf.Min(3f, 0.1f * (b.averagePain - lastPain));
            lastHearing = b.hearingLoss;
            lastPain = b.averagePain;

            cold = 1f + coldPerDeg.Value * Mathf.Max(0f, 36f - b.temperature);
            float scale = WorldGeneration.GetRunSettingFloat("fibrillationrate");
            float perMin = (r1 + r3 + r2) * cold * overall.Value;
            float add = (perMin / 60f * dt + kick * cold * overall.Value) * scale;
            b.fibrillationProgress = Mathf.Min(100f, b.fibrillationProgress + add);
            TrackLayer(dt, add);

            // 5-minute running average of the actual rise, per minute, for tuning
            float inst = add / dt * 60f;
            avg = Mathf.Lerp(avg, inst, 1f - Mathf.Exp(-dt / 300f));
        }

        // dominant subtype for the monitor title, "" when nothing notable
        internal static string Dominant()
        {
            if (!Active) return "";
            float m = Mathf.Max(r1, Mathf.Max(r3, r2));
            if (m < 0.05f) return "";
            return m == r1 ? "LQT1" : m == r3 ? "LQT3" : "LQT2";
        }

        // ---- console: "lqt" prints the breakdown, "lqt <key> <value>" tunes a rate (saved to the config) ----
        internal static string Command(string[] args)
        {
            if (args.Length >= 3)
            {
                if (!tunables.TryGetValue(args[1].ToLowerInvariant(), out var e)) return "lqt: unknown key " + args[1];
                if (!float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val)) return "lqt: bad number " + args[2];
                e.Value = val;
                return "lqt: " + e.Definition.Key + " = " + val.ToString("0.###");
            }
            var b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            string state = Active ? "on" : "off (run setting)";
            string s = "long QT " + state + "\n";
            if (b != null && Active)
                s += string.Format("fib {0:0.0}  per min: LQT1 {1:0.00}  LQT3 {2:0.00}  LQT2 {3:0.00}  cold x{4:0.00}  -> {5:0.00}  (5-min avg {6:0.00})\n",
                    b.fibrillationProgress, r1, r3, r2, cold, (r1 + r2 + r3) * cold * overall.Value, avg);
            foreach (var l in layers) s += Fmt(l) + "\n";
            if (cur.depth >= 0) s += Fmt(cur) + " (current)\n";
            s += "keys:";
            foreach (var kv in tunables) s += " " + kv.Value.Definition.Key + "=" + kv.Value.Value.ToString("0.###");
            return s;
        }

        // ---- save: keep "longqt" out of save.sv so the save still loads without the mod ----
        // Vanilla resolves every saved setting name with First(), which throws on an unknown name.
        // The flag goes to a sidecar file tagged with save.sv's size and write time instead.
        static string SavePath => Application.persistentDataPath + "\\save.sv";
        static string SidePath => Application.persistentDataPath + "\\save.sv.ecgplus";

        internal static object StripForSave()
        {
            var rs = WorldGeneration.runSettings;
            if (rs == null || !rs.TryGetValue(Key, out var v)) return null;
            rs.Remove(Key);
            return v;
        }

        internal static void RestoreAfterSave(object v, bool saved)
        {
            if (v == null) return;
            var rs = WorldGeneration.runSettings;
            if (rs != null) rs[Key] = v;
            if (!saved) return;
            try
            {
                if (v is bool on && on && File.Exists(SavePath))
                {
                    var fi = new FileInfo(SavePath);
                    File.WriteAllText(SidePath, "longqt " + fi.Length + " " + fi.LastWriteTimeUtc.Ticks);
                }
                else if (File.Exists(SidePath)) File.Delete(SidePath);
            }
            catch (Exception) { }
        }

        internal static void AfterLoad()
        {
            var rs = WorldGeneration.runSettings;
            if (rs == null || rs.ContainsKey(Key)) return;
            bool on = false;
            try
            {
                if (File.Exists(SidePath) && File.Exists(SavePath))
                {
                    var p = File.ReadAllText(SidePath).Split(' ');
                    var fi = new FileInfo(SavePath);
                    on = p.Length == 3 && p[0] == "longqt" && p[1] == fi.Length.ToString() && p[2] == fi.LastWriteTimeUtc.Ticks.ToString();
                }
            }
            catch (Exception) { }
            rs[Key] = on;
        }

        // wording by the user (English translation approved by the user)
        internal static string Text(string str)
        {
            bool zh = Ui.Zh;
            if (str == "runset" + Key) return zh ? "长QT综合症" : "Long QT syndrome";
            if (str == "runset" + Key + "dsc")
                return zh
                    ? "你的心脏…复极很慢，这也许就是你比常人更聪明的代价。而且无论你做什么，它都一步步趋近于可怕的室颤。剧烈运动、接触冷水，恐惧、疼痛…甚至是休息与睡眠。它永远也不会自行恢复，只有胺碘酮和除颤器能救你。研究人员大发慈悲地为你准备了少许，但这无异于杯水车薪。"
                    : "Your heart... is slow to repolarize. Perhaps that is the price of being smarter than most. And whatever you do, it creeps step by step toward deadly fibrillation: hard exertion, cold water, fear, pain... even rest and sleep. It will never recover on its own; only amiodarone and a defibrillator can save you. The researchers have mercifully left you a little of both, but it is a drop in the bucket.";
            return null;
        }
    }

    // starting kit on a fresh run (layer 1, not a loaded save), like vanilla's permanent-addiction heroin:
    // a manual defibrillator with a small battery at 33% (about 2 shocks with some time to fumble),
    // a syringe already loaded with one 20 ml dose of amiodarone, and a blueprint for small batteries
    // (vanilla blueprint, pointed at that recipe: using it drops the recipe's INT requirement to 0).
    // Plus 2 INT. Skills are set up in Body.Awake, before WorldGeneration.Start, so this sticks.
    [HarmonyPatch(typeof(WorldGeneration), "Start")]
    static class LongQtKitPatch
    {
        static void Postfix(WorldGeneration __instance)
        {
            try
            {
                if (!LongQt.Active || __instance.biomeDepth != 0 || SaveSystem.loadedRun) return;
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;
                var defib = Give(body, "manualdefibrillator");
                if (defib != null && defib.battery != null)
                {
                    // the defib's own condition is its battery charge (BatteryItem.DrainCharge)
                    defib.battery.batteryType = "smallbattery";
                    defib.battery.maxCharge = 50f;
                    defib.condition = 0.33f;
                }
                var bp = Give(body, "blueprint");
                int battery = Recipes.recipes.FindIndex(r => r.result != null && r.result.id == "smallbattery");
                var bs = bp != null ? bp.GetComponent<BlueprintScript>() : null;
                if (bs != null && battery >= 0) bs.recipeIndex = battery;

                body.skills.INT += 2;
                body.skills.UpdateExpBoundaries();
                body.skills.expINT = body.skills.minINT;

                var syringe = Give(body, "syringe");
                var wc = syringe != null ? syringe.GetComponent<WaterContainerItem>() : null;
                if (wc != null)
                {
                    wc.stack = new List<LiquidStack> { new LiquidStack("amiodarone", 20f) };
                    wc.UpdateCondition();
                }
            }
            catch (Exception) { }
        }

        static Item Give(Body body, string id)
        {
            var go = Utils.Create(id, body.transform.position, 0f);
            var item = go != null ? go.GetComponent<Item>() : null;
            if (item != null) body.AutoPickUpItem(item);
            return item;
        }
    }

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGame))]
    static class LongQtSavePatch
    {
        static void Prefix(out object __state) => __state = LongQt.StripForSave();
        static Exception Finalizer(Exception __exception, object __state)
        {
            LongQt.RestoreAfterSave(__state, __exception == null);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.TryLoadGame))]
    static class LongQtLoadPatch
    {
        static void Postfix() { try { LongQt.AfterLoad(); } catch (Exception) { } }
    }

    // a settings dictionary without the key (old save, pasted settings string) must not crash the menu
    [HarmonyPatch(typeof(RunSettingDisplay), nameof(RunSettingDisplay.UpdateSettingDisplay))]
    static class LongQtDisplayPatch
    {
        static void Prefix(Dictionary<string, object> settings)
        {
            if (settings != null && !settings.ContainsKey(LongQt.Key)) settings[LongQt.Key] = false;
        }
    }

    [HarmonyPatch(typeof(Locale), nameof(Locale.GetString))]
    static class LongQtTextPatch
    {
        static void Postfix(string str, int type, ref string __result)
        {
            if (type != 3 || str == null || !str.StartsWith("runset" + LongQt.Key)) return;
            if (__result != null && __result != str) return;   // a language file defines it
            var t = LongQt.Text(str);
            if (t != null) __result = t;
        }
    }
}
