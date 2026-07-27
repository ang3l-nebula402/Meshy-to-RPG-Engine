using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// v0.3, hook the real import pipeline.
    ///
    /// The Unity stack trace from a live GLB import revealed the real chain:
    ///
    ///   RpgEngine.MeshImporter.GetMesh (coroutine)
    ///     → RpgEngine.PickupableMesh.SetRPGMesh(RPGMesh)         ← we hook here
    ///       → RpgEngine.PickupableMesh.LoadFromBinaryFile(RPGMesh)
    ///         → RpgEngine.PickupableMesh.LoadGlbData(byte[])     ← and here as an alt
    ///           → Siccity.GLTFUtility.Importer.LoadFromBytes(byte[], …)
    ///
    /// The v0.2 target (CustomMeshCreator.UpdateRPGMesh) turned out to be a
    /// networking sync method (its `send` param broadcasts to peers), not the
    /// import pipeline. Nothing we did there ever ran for the actual import.
    ///
    /// Hooking PickupableMesh.SetRPGMesh gives us:
    ///   • the moment the mesh finishes attaching to a placeable
    ///   • access to the PickupableMesh instance (so its .gameObject is our root)
    ///   • zero dependency on the RpgEngine assembly at compile time, we use
    ///     `object __instance` and resolve at runtime via Component.gameObject.
    ///
    /// The known GLB shader crash (GLTFUtility trying to build a URP material
    /// with a null shader) is NOT our concern for animation, the mesh and
    /// skeleton are parsed before materials. If GLB parsing produced a
    /// SkinnedMeshRenderer with bones, we install our legacy Animation.
    /// </summary>
    internal static class CustomMeshCreatorPatch
    {
        // Multiple candidate targets, we try each and hook whichever exists.
        private static readonly (string Type, string Method)[] Targets = new[]
        {
            ("RpgEngine.PickupableMesh", "SetRPGMesh"),
            ("RpgEngine.PickupableMesh", "LoadFromBinaryFile"),
            ("RpgEngine.PickupableMesh", "LoadGlbData"),
        };

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var patched = 0;

            foreach (var (typeName, methodName) in Targets)
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null)
                {
                    log.LogWarning($"Type '{typeName}' not found, skipping {methodName}.");
                    continue;
                }

                var method = t.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == methodName);

                if (method == null)
                {
                    log.LogWarning($"Method '{methodName}' not found on {t.FullName}, skipping.");
                    // Log actual method list for reference
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        log.LogInfo($"    {t.Name}.{m.Name}({parms}) → {m.ReturnType.Name}");
                    }
                    continue;
                }

                var pparms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                log.LogInfo($"Target: {t.Name}.{method.Name}({pparms}) → {method.ReturnType.Name}");

                try
                {
                    // Postfix runs on the normal path. Finalizer runs even when
                    // the original throws, critical for GLB imports where a
                    // GLTFUtility material crash used to silence us entirely.
                    harmony.Patch(
                        original: method,
                        postfix:    new HarmonyMethod(typeof(CustomMeshCreatorPatch), nameof(ImportComplete_Postfix)),
                        finalizer:  new HarmonyMethod(typeof(CustomMeshCreatorPatch), nameof(ImportComplete_Finalizer)));
                    log.LogInfo($"Patched {t.Name}.{method.Name} (postfix + finalizer).");
                    patched++;
                }
                catch (Exception ex)
                {
                    log.LogError($"Failed to patch {method.Name}: {ex.Message}");
                }
            }

            log.LogInfo($"CustomMeshCreatorPatch: {patched} target(s) hooked.");
        }

        /// <summary>
        /// Runs after any import-pipeline method completes. We deduplicate
        /// per GameObject so multiple hooks on the same import don't stack
        /// multiple Animation components.
        ///
        /// Void-safe. Never throws to the caller.
        /// </summary>
        private static void ImportComplete_Postfix(object __instance)
        {
            var log = Plugin.Log;
            try
            {
                var root = ResolveGameObject(__instance);
                if (root == null)
                {
                    log.LogInfo($"ImportComplete: instance was {__instance?.GetType().FullName ?? "null"} but couldn't resolve a GameObject.");
                    return;
                }

                // De-dup: if we already installed our exposer, don't re-run.
                if (root.GetComponentInChildren<PickupableAnimationExposer>() != null)
                {
                    log.LogInfo($"ImportComplete: '{root.name}' already has our exposer, skipping.");
                    return;
                }

                // v3.1.4 diag: log the pickupable's prop guid + world position
                // so we can tell if two "batch" cycles are for the same Prop
                // (spawn duplication bug) or different Props (lingering-Prop desync).
                string diagGuid = "<?>", diagPos = "<?>";
                try
                {
                    var propProp = __instance.GetType().GetProperty("prop",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var propObj = propProp?.GetValue(__instance);
                    if (propObj != null)
                    {
                        var guidField = propObj.GetType().GetField("guid")
                                     ?? (System.Reflection.MemberInfo)propObj.GetType().GetProperty("guid");
                        diagGuid = (guidField is System.Reflection.FieldInfo gf
                            ? gf.GetValue(propObj)
                            : ((System.Reflection.PropertyInfo)guidField)?.GetValue(propObj)) as string ?? "<null>";
                    }
                    diagPos = root.transform.position.ToString("F2");
                }
                catch { /* diagnostic, best-effort */ }
                log.LogInfo($"ImportComplete: inspecting '{root.name}' (prop.guid={diagGuid}, pos={diagPos})");

                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                log.LogInfo($"  SkinnedMeshRenderer count: {skinned.Length}");
                if (skinned.Length == 0)
                {
                    log.LogInfo("  → static mesh (no SMR); leaving untouched.");
                    return;
                }

                var boneCounts = skinned.Select(s => s.bones?.Length ?? 0).ToArray();
                log.LogInfo($"  Bone counts per SMR: [{string.Join(", ", boneCounts)}]");
                if (!boneCounts.Any(b => b > 0))
                {
                    log.LogInfo("  → SMR present but no bones bound; leaving untouched.");
                    return;
                }

                // Count clips we can find on the object BEFORE we mess with it.
                var precount = 0;
                foreach (var a in root.GetComponentsInChildren<Animation>(true))
                    foreach (AnimationState _ in a) precount++;
                foreach (var an in root.GetComponentsInChildren<Animator>(true))
                    if (an.runtimeAnimatorController != null)
                        precount += an.runtimeAnimatorController.animationClips.Length;
                log.LogInfo($"  Pre-existing animation clip count on '{root.name}': {precount}");

                log.LogInfo($"  → skeleton detected. Installing legacy Animation on '{root.name}'.");
                AnimationBuilder.InstallAnimator(root);
            }
            catch (Exception ex)
            {
                log.LogWarning($"ImportComplete_Postfix threw (game continues): {ex}");
            }
        }

        /// <summary>
        /// Harmony finalizer: runs after the original method whether it
        /// completed normally or threw. Returning null means "don't rethrow"
        /// which we DON'T want (we want the engine to see the exception as
        /// normal so it can display its usual error). We just log it and, if
        /// we can still find a mesh + skeleton on the instance, install the
        /// Animation anyway. Returns __exception unchanged so it propagates.
        /// </summary>
        private static Exception ImportComplete_Finalizer(object __instance, Exception __exception, MethodBase __originalMethod)
        {
            var log = Plugin.Log;
            if (__exception != null)
            {
                // Full exception details so we can see what's actually throwing.
                // The user was reporting "cannot reselect imported mesh", likely
                // because this exception is preventing collider setup downstream.
                log.LogWarning(
                    $"Import method finalizer: {__originalMethod?.Name} threw " +
                    $"{__exception.GetType().FullName}: {__exception.Message}\n" +
                    $"Stack:\n{__exception.StackTrace}");
            }

            try
            {
                var root = ResolveGameObject(__instance);
                if (root == null) goto swallowDecision;

                if (root.GetComponentInChildren<PickupableAnimationExposer>() != null)
                    goto swallowDecision;

                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (skinned.Length == 0 || !skinned.Any(s => (s.bones?.Length ?? 0) > 0))
                    goto swallowDecision;

                log.LogInfo($"Finalizer path: '{root.name}' has skeleton, installing Animation despite original exception.");
                AnimationBuilder.InstallAnimator(root);
            }
            catch (Exception ex)
            {
                log.LogWarning($"ImportComplete_Finalizer suppressed inner error: {ex.Message}");
            }

            swallowDecision:
            // If we swallow the exception, the engine keeps going and finishes
            // its normal post-import work (colliders, selection, etc.).
            // If we rethrow, the engine sees the original failure, accurate,
            // but leaves the mesh unclickable when the failure was in a step
            // we don't care about (animation parsing).
            //
            // Default: swallow when the original method is a GLB-specific one
            // (LoadGlbData / LoadFromBinaryFile / SetRPGMesh), for these, the
            // engine's own error handling is already reporting the shader/
            // animation issues visibly (missing texture, T-pose), and we just
            // need the mesh to be interactable.
            if (Plugin.SwallowImportExceptions && __exception != null)
            {
                log.LogInfo($"Swallowing exception from {__originalMethod?.Name} so downstream engine setup can complete.");
                return null;
            }
            return __exception;
        }

        private static GameObject ResolveGameObject(object instance)
        {
            if (instance == null) return null;
            if (instance is GameObject go) return go;
            if (instance is Component comp) return comp.gameObject;

            // Reflection fallback for any wrapper class
            var t = instance.GetType();
            foreach (var name in new[] { "gameObject", "root", "meshRoot", "target", "attachedGameObject" })
            {
                var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(instance);
                    if (val is GameObject g) return g;
                    if (val is Component c) return c.gameObject;
                }
            }
            return null;
        }
    }
}
