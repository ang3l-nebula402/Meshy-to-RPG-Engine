using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// Fix for GLTFUtility's runtime SetCurve error.
    ///
    /// v0.5 flipped clip.legacy=true on EVERY SetCurve call, that broke the
    /// engine's built-in Mecanim characters (once legacy, Mecanim can't play
    /// the clip; characters piled up at origin, sunken through the ground).
    ///
    /// v0.7 walks the call stack and only flips legacy when the immediate
    /// caller is inside the Siccity.GLTFUtility namespace. Any other SetCurve
    /// caller (built-in code, mods, whatever) is left untouched.
    /// </summary>
    internal static class LegacyClipPatch
    {
        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;

            var target = AccessTools.Method(
                typeof(AnimationClip),
                nameof(AnimationClip.SetCurve),
                new[] { typeof(string), typeof(Type), typeof(string), typeof(AnimationCurve) });

            if (target == null)
            {
                log.LogWarning("AnimationClip.SetCurve not found, legacy-clip fix disabled.");
                return;
            }

            try
            {
                harmony.Patch(
                    original: target,
                    prefix: new HarmonyMethod(typeof(LegacyClipPatch), nameof(SetCurve_Prefix)));
                log.LogInfo("Patched AnimationClip.SetCurve, will flip clip.legacy=true only for GLTFUtility callers.");
            }
            catch (Exception ex)
            {
                log.LogError($"Failed to patch AnimationClip.SetCurve: {ex.Message}");
            }
        }

        private static void SetCurve_Prefix(AnimationClip __instance)
        {
            if (__instance == null || __instance.legacy) return;

            // Only flip legacy for GLTFUtility. Anything else keeps its
            // existing (Mecanim-compatible) state.
            if (!IsCalledFromGltfUtility()) return;

            __instance.legacy = true;
        }

        private static bool IsCalledFromGltfUtility()
        {
            try
            {
                // Skip our own prefix frame with skipFrames=1. Cap at 8 frames
                // so we don't walk the whole world; GLTFUtility's SetCurve
                // callsite is always within a few frames of the top.
                var st = new StackTrace(1, false);
                var count = Math.Min(st.FrameCount, 8);
                for (int i = 0; i < count; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    var ns = m?.DeclaringType?.Namespace;
                    if (ns != null && ns.StartsWith("Siccity.GLTFUtility", StringComparison.Ordinal))
                        return true;
                }
            }
            catch { /* stack trace failed, bail conservatively */ }
            return false;
        }
    }
}
