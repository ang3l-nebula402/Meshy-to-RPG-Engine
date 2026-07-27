using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// The RPG Engine calls Siccity.GLTFUtility.Importer.LoadFromBytes(bytes,
    /// settings), the overload WITHOUT the AnimationClip[] out parameter. The
    /// clips are still parsed internally but discarded on return.
    ///
    /// We hook the inner Importer methods that DO expose the animations out
    /// param, capture the clips into a static buffer as they're produced, then
    /// let CustomMeshCreatorPatch's postfix pick them up and attach to the
    /// resulting SkinnedMeshRenderer via a legacy Animation component.
    ///
    /// Timeline of one import:
    ///   1. engine calls Importer.LoadFromBytes(bytes, settings)
    ///   2. → calls ImportGLB(bytes, settings, out animations)          ← our hook
    ///          → LoadInternal(..., out animations)                     ← backup hook
    ///   3. our postfix captures `animations` into RecentAnimations
    ///   4. engine's LoadFromBytes returns the root GameObject (no anims)
    ///   5. LoadGlbData / LoadFromBinaryFile / SetRPGMesh all complete
    ///   6. CustomMeshCreatorPatch.ImportComplete_Postfix fires
    ///   7. AnimationBuilder looks in RecentAnimations for anything captured
    ///      in the last few seconds and attaches it to the root.
    /// </summary>
    internal static class GltfImporterCapturePatch
    {
        /// <summary>
        /// Most-recent capture of animation clips from GLTFUtility, with a
        /// timestamp so stale captures don't leak between imports.
        /// </summary>
        internal static AnimationClip[] RecentAnimations;
        internal static float RecentAnimationsTime;

        /// <summary>
        /// Grab a captured animation set if it happened within the last few
        /// seconds. Consumes it (single-use per import).
        /// </summary>
        internal static AnimationClip[] ConsumeRecent(float maxAgeSeconds = 10f)
        {
            if (RecentAnimations == null) return null;
            if (Time.realtimeSinceStartup - RecentAnimationsTime > maxAgeSeconds) return null;
            var clips = RecentAnimations;
            RecentAnimations = null;
            return clips;
        }

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var importerType = AccessTools.TypeByName("Siccity.GLTFUtility.Importer");
            if (importerType == null)
            {
                log.LogWarning("Siccity.GLTFUtility.Importer not found, cannot capture animation clips.");
                return;
            }

            // Hook every static Importer method that has an `AnimationClip[]&`
            // (out/ref) parameter. That covers ImportGLB and LoadInternal
            // regardless of overload variations across GLTFUtility versions.
            var targets = importerType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(AnimationClip[]).MakeByRefType()))
                .ToList();

            if (targets.Count == 0)
            {
                log.LogWarning("No Importer method with an out AnimationClip[] parameter was found. " +
                               "Animation capture disabled.");
                // Log methods so we can see if the signatures changed
                foreach (var m in importerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    log.LogInfo($"    Importer.{m.Name}({parms}) → {m.ReturnType.Name}");
                }
                return;
            }

            foreach (var method in targets)
            {
                var parms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                log.LogInfo($"Hooking Importer.{method.Name}({parms}) → {method.ReturnType.Name}");
                try
                {
                    // Two postfixes:
                    //   • Generic diagnostic (logs args, tries __args capture)
                    //   • Typed capture, Harmony matches `animations` by name
                    //     for ref/out params, which is the reliable route.
                    harmony.Patch(
                        original: method,
                        postfix: new HarmonyMethod(typeof(GltfImporterCapturePatch), nameof(Importer_Postfix)));
                    harmony.Patch(
                        original: method,
                        postfix: new HarmonyMethod(typeof(GltfImporterCapturePatch), nameof(Importer_TypedPostfix)));
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Could not hook Importer.{method.Name}: {ex.Message}");
                }
            }
        }

        // Typed postfix, Harmony matches parameter `animations` by name
        // against the original method's out-param, which is the documented
        // reliable way to read ref/out values.
        private static void Importer_TypedPostfix(ref AnimationClip[] animations, MethodBase __originalMethod)
        {
            var log = Plugin.Log;
            if (animations == null)
            {
                log.LogInfo($"[capture-typed] Importer.{__originalMethod?.Name}: animations parameter is null.");
                return;
            }

            var populated = animations.Where(c => c != null).ToArray();
            log.LogInfo($"[capture-typed] Importer.{__originalMethod?.Name}: animations.Length={animations.Length}, non-null={populated.Length}");
            if (populated.Length == 0) return;

            RecentAnimations = populated;
            RecentAnimationsTime = Time.realtimeSinceStartup;
            log.LogInfo($"[capture-typed] → Captured {populated.Length} clip(s): " +
                        string.Join(", ", populated.Select(c => c.name)));
        }

        // Generic postfix, reads the AnimationClip[] out param from __args.
        // Harmony passes ref/out params through __args as the actual runtime
        // array reference, so we can capture it without knowing the exact
        // parameter position at compile time.
        private static void Importer_Postfix(object[] __args, MethodBase __originalMethod)
        {
            var log = Plugin.Log;
            var methodName = __originalMethod?.Name ?? "?";

            // Always log a fired-marker so we know the postfix ran, even when
            // there was nothing to capture.
            log.LogInfo($"[capture] Importer.{methodName} postfix fired. arg count={__args?.Length ?? 0}");

            if (__args == null) return;

            var found = false;
            for (int i = 0; i < __args.Length; i++)
            {
                var arg = __args[i];
                var typeName = arg?.GetType().FullName ?? "null";
                log.LogInfo($"  arg[{i}]: {typeName}");
                if (arg is AnimationClip[] clips)
                {
                    found = true;
                    log.LogInfo($"    → AnimationClip[] length={clips.Length}, non-null count={clips.Count(c => c != null)}");
                    if (clips.Length > 0 && clips.Any(c => c != null))
                    {
                        RecentAnimations = clips.Where(c => c != null).ToArray();
                        RecentAnimationsTime = Time.realtimeSinceStartup;
                        log.LogInfo($"  → Captured {RecentAnimations.Length} clip(s): " +
                                    string.Join(", ", RecentAnimations.Select(c => c.name)));
                    }
                    else
                    {
                        log.LogInfo("  → array present but empty or all null; not captured.");
                    }
                }
            }

            if (!found)
                log.LogInfo("  → no AnimationClip[] argument seen. Harmony may not surface out-params via __args on this build.");
        }
    }
}
