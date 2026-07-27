using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// One-shot diagnostic: the FIRST time a native (built-in) character's
    /// animation panel opens, dump its complete SkinnedMeshRenderer skeleton
    /// to the log. This tells us the engine's canonical bone naming, which we
    /// need to know before deciding how to bridge imported meshes to native
    /// animations (rename / retarget / etc.).
    ///
    /// Non-invasive: piggy-backs on AnimationList.Enable(Pickupable), fires
    /// when the panel activates on ANY pickupable. Skips our custom imports
    /// (they have an Exposer), skips already-dumped ones. Removes itself
    /// after one successful dump.
    /// </summary>
    internal static class NativeSkeletonProbe
    {
        private static bool _dumped;

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var t = AccessTools.TypeByName("RpgEngine.AnimationList");
            if (t == null) { log.LogWarning("SkeletonProbe: AnimationList not found."); return; }
            var m = AccessTools.Method(t, "Enable", new[] { AccessTools.TypeByName("RpgEngine.Pickupable") });
            if (m == null) { log.LogWarning("SkeletonProbe: AnimationList.Enable not found."); return; }

            try
            {
                harmony.Patch(
                    original: m,
                    postfix: new HarmonyMethod(typeof(NativeSkeletonProbe), nameof(Enable_Postfix)));
                log.LogInfo("Patched AnimationList.Enable (postfix, one-shot skeleton probe for native characters).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"SkeletonProbe patch failed: {ex.Message}");
            }
        }

        private static void Enable_Postfix(object _target)
        {
            var comp = _target as Component;
            if (comp == null) return;

            // Always log a compact diagnostic when the panel activates so we
            // can see the state of key components on the target.
            LogTargetState(comp);

            if (_dumped) return;

            // Skip our own imports so we get a NATIVE character's skeleton.
            if (comp.GetComponentInChildren<PickupableAnimationExposer>() != null) return;

            // Only dump if this pickupable actually has a SkinnedMeshRenderer +
            // bones, otherwise it's not a character.
            var smrs = comp.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs.Length == 0) return;
            var withBones = smrs.FirstOrDefault(s => s.bones != null && s.bones.Length > 0);
            if (withBones == null) return;

            _dumped = true;
            var log = Plugin.Log;
            log.LogInfo($"=== [skel-probe] Native character: '{comp.name}' ({comp.GetType().FullName}) ===");
            log.LogInfo($"  SkinnedMeshRenderer '{withBones.name}' has {withBones.bones.Length} bones");
            log.LogInfo($"  rootBone: '{withBones.rootBone?.name ?? "<null>"}'");
            log.LogInfo($"  Bone list (in order used by SMR):");
            for (int i = 0; i < withBones.bones.Length; i++)
            {
                var b = withBones.bones[i];
                if (b == null) { log.LogInfo($"    [{i:D2}] <null>"); continue; }
                var path = GetPath(b, comp.transform);
                log.LogInfo($"    [{i:D2}] {b.name}   (path: {path})");
            }

            // Also dump the full Transform tree under the SMR's parent so we
            // can see any bones that aren't in the SMR.bones array.
            var root = withBones.rootBone?.parent ?? withBones.transform;
            log.LogInfo($"  Full Transform hierarchy under '{root.name}':");
            DumpHierarchy(root, 4, 0);

            // Also dump the Animator's parameter list, these are the state
            // names / trigger names the engine uses. Vital for figuring out
            // what animations we can map to.
            var animator = comp.GetComponentInChildren<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                // Cache the controller so imported meshes can inherit it.
                NativeControllerCache.Capture(animator.runtimeAnimatorController);

                log.LogInfo($"  Animator on '{animator.name}':");
                log.LogInfo($"    controller: {animator.runtimeAnimatorController.name}");
                log.LogInfo($"    avatar isHuman: {animator.avatar?.isHuman}, isValid: {animator.avatar?.isValid}");
                log.LogInfo($"    parameters ({animator.parameterCount}):");
                foreach (var p in animator.parameters)
                    log.LogInfo($"      {p.type,-8} {p.name}");
                var clips = animator.runtimeAnimatorController.animationClips;
                log.LogInfo($"    clips ({clips.Length}):");
                foreach (var c in clips.Take(40))
                    log.LogInfo($"      {c.name}   ({c.length:F2}s, humanMotion={c.humanMotion}, legacy={c.legacy})");
                if (clips.Length > 40) log.LogInfo($"      … +{clips.Length - 40} more");
            }
            else
            {
                log.LogInfo("  No Animator found on this pickupable.");
            }

            log.LogInfo("=== [skel-probe] end ===");
        }

        /// <summary>
        /// Compact per-click diagnostic, tells us whether the pickupable's
        /// .animator field resolves, whether UnityAnimator/Animator/Avatar are
        /// attached, and whether animations enumerate.
        /// </summary>
        private static void LogTargetState(Component pickupable)
        {
            var log = Plugin.Log;
            try
            {
                var t = pickupable.GetType();
                var animatorProp = t.GetProperty("animator",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var iAnim = animatorProp?.GetValue(pickupable);

                // Also get UnityEngine.Animator + AnimationList's read of animations.
                var animator = pickupable.GetComponentInChildren<Animator>();
                var iAnimatorType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Animation.IAnimator");
                var unityAnimator = iAnimatorType != null ? pickupable.GetComponentInChildren(iAnimatorType) : null;

                int paramCount = animator?.parameterCount ?? -1;
                int animationsCount = -1;
                if (iAnim != null)
                {
                    var animsProp = iAnim.GetType().GetProperty("animations");
                    var animsList = animsProp?.GetValue(iAnim) as System.Collections.IList;
                    animationsCount = animsList?.Count ?? -1;
                }

                log.LogInfo($"[panel-open] target='{pickupable.name}' " +
                            $"pickupable.animator={(iAnim == null ? "NULL" : iAnim.GetType().Name)} " +
                            $"UnityEngine.Animator={(animator == null ? "NULL" : $"present, params={paramCount}")} " +
                            $"IAnimator-comp={(unityAnimator == null ? "NULL" : unityAnimator.GetType().Name)} " +
                            $"animations.Count={animationsCount}");
            }
            catch (System.Exception ex)
            {
                log.LogWarning($"LogTargetState threw: {ex.Message}");
            }
        }

        private static void DumpHierarchy(Transform t, int maxDepth, int depth)
        {
            if (t == null || depth > maxDepth) return;
            var indent = new string(' ', depth * 2);
            Plugin.Log.LogInfo($"    {indent}{t.name}");
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), maxDepth, depth + 1);
        }

        private static string GetPath(Transform t, Transform stopAt)
        {
            var parts = new List<string>();
            while (t != null && t != stopAt)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }
    }
}
