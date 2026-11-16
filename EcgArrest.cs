using HarmonyLib;
using UnityEngine;

namespace EcgPlus
{
    // Shocks during cardiac arrest (game: heart rate < 20, shown by EcgPlus as VF).
    // Vanilla: fibrillation always resets to 0, but only 20% of shocks restart the heart (HR = 200);
    // the other 80% leave HR at 0, i.e. still in arrest.
    // Here, closer to real defibrillation: shocks restart the heart often while VF is still coarse (90%)
    // and less as it decays to fine VF (30%) - the same coarse->fine measure the monitor draws.
    // VF often comes back: only 20% of shocks are clean restarts (fib 0, same as vanilla at any time);
    // the other restarts keep fibrillation at 60-80 (VT/flutter on the monitor). Whether that climbs
    // back to arrest depends on perfusion: the game raises fib while BP < 88, SpO2 < 60 or HR > 200,
    // and lets it fall otherwise. The shock itself still sets BP to 40, as in vanilla.
    [HarmonyPatch(typeof(Item), nameof(Item.Defibrillate))]
    static class ArrestShockPatch
    {
        const float RestartCoarse = 0.9f, RestartFine = 0.3f, Clean = 0.2f, RefibMin = 60f, RefibMax = 80f;

        static bool Prefix(Item.DefibInfo info)
        {
            if (!Plugin.arrestShock.Value) return true;
            var limb = info.limb;
            var body = limb != null ? limb.body : null;
            if (body == null || !body.alive || limb != body.limbs[1] || !body.inCardiacArrest) return true;

            // same side effects as vanilla
            limb.skinHealth -= 5f;
            limb.pain += 20f;
            body.Ragdoll();

            float restart = Mathf.Lerp(RestartFine, RestartCoarse, Rhythm.ArrestLeft(body));
            float r = Random.value;
            body.fibrillationProgress = 0f;
            if (r < restart)
            {
                body.heartRate = 200f;
                SteamManager.TryUnlockAchievement("DEADMANWALKING");
                if (r >= Clean) body.fibrillationProgress = Random.Range(RefibMin, RefibMax);
            }
            body.defibShockedFrames = 20;
            body.heartProg = -1f;
            body.bloodPressure = 40f;
            return false;
        }
    }
}
