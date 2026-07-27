using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// Capture animation clips DIRECTLY at the source.
    ///
    /// Diagnostic run proved:
    ///   • GLTFAnimation.Import is called once per animation in the GLB
    ///   • Each call runs to completion with no exception
    ///   • Each call produces an ImportResult with a .clip field
    ///   • But somewhere in LoadInternal the resulting array never lands in
    ///     the `out AnimationClip[] animations` param, it stays null.
    ///
    /// Instead of fighting whatever internal assignment is failing, we hook
    /// GLTFAnimation.Import's postfix, extract the .clip via reflection from
    /// each returned ImportResult, and accumulate them into a batch.
    /// GltfImporterCapturePatch's downstream code (the ConsumeRecent path in
    /// AnimationBuilder) then finds them the same way it would have if the
    /// out-param had worked.
    ///
    /// The GLTFAnimationExtensions.Import wrapper marks batch boundaries so
    /// we know when a set of clips belongs to one import.
    /// </summary>
    internal static class GltfAnimationImportCapturePatch
    {
        // Per-import accumulator. The wrapper's prefix clears it, per-anim
        // postfixes append to it, the wrapper's postfix flushes it into
        // GltfImporterCapturePatch.RecentAnimations.
        private static readonly List<AnimationClip> _batch = new List<AnimationClip>();
        private static bool _batchActive;

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;

            HookWrapper(harmony,
                type: "Siccity.GLTFUtility.GLTFAnimationExtensions",
                method: "Import");

            HookPerAnimation(harmony,
                type: "Siccity.GLTFUtility.GLTFAnimation",
                method: "Import");
        }

        // Wrapper: the extension method that Importer.LoadInternal calls once
        // per import to process all animations at once. Prefix opens a batch,
        // postfix closes it and hands the clips over.
        private static void HookWrapper(Harmony harmony, string type, string method)
        {
            var t = AccessTools.TypeByName(type);
            if (t == null) { Plugin.Log.LogWarning($"Type '{type}' not found for wrapper hook."); return; }

            var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == method).ToList();

            foreach (var m in candidates)
            {
                try
                {
                    harmony.Patch(
                        original: m,
                        prefix:  new HarmonyMethod(typeof(GltfAnimationImportCapturePatch), nameof(Wrapper_Prefix)),
                        postfix: new HarmonyMethod(typeof(GltfAnimationImportCapturePatch), nameof(Wrapper_Postfix)));
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Plugin.Log.LogInfo($"[batch] Patched {t.Name}.{m.Name}({parms}), wraps per-animation captures.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[batch] Could not patch {t.Name}.{m.Name}: {ex.Message}");
                }
            }
        }

        // Per-animation: fires 10 times for a GLB with 10 animations. Each
        // returns an ImportResult; we grab the AnimationClip off it.
        private static void HookPerAnimation(Harmony harmony, string type, string method)
        {
            var t = AccessTools.TypeByName(type);
            if (t == null) { Plugin.Log.LogWarning($"Type '{type}' not found for per-anim hook."); return; }

            var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == method).ToList();

            foreach (var m in candidates)
            {
                try
                {
                    harmony.Patch(
                        original: m,
                        postfix: new HarmonyMethod(typeof(GltfAnimationImportCapturePatch), nameof(PerAnimation_Postfix)));
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Plugin.Log.LogInfo($"[per-anim] Patched {t.Name}.{m.Name}({parms}), captures each clip.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[per-anim] Could not patch {t.Name}.{m.Name}: {ex.Message}");
                }
            }
        }

        // ────────────────────────────────────────────────────────────────

        private static void Wrapper_Prefix()
        {
            _batch.Clear();
            _batchActive = true;
            Plugin.Log.LogInfo("[batch] Opening animation capture batch.");
        }

        private static void Wrapper_Postfix()
        {
            _batchActive = false;
            if (_batch.Count == 0)
            {
                Plugin.Log.LogInfo("[batch] Closed, no clips captured this import.");
                return;
            }
            var clips = _batch.ToArray();
            _batch.Clear();

            GltfImporterCapturePatch.RecentAnimations = clips;
            GltfImporterCapturePatch.RecentAnimationsTime = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo($"[batch] Closed, captured {clips.Length} clip(s): " +
                                string.Join(", ", clips.Select(c => c.name)));
        }

        // Extract .clip from the ImportResult returned by GLTFAnimation.Import.
        // Handles the several shapes the method can return across GLTFUtility
        // versions: object with .clip field, IEnumerable of such objects, or
        // IEnumerator (iterator method), for iterators we can't easily
        // materialise the values here, so we log and rely on the per-anim
        // shape being the "returns a single ImportResult" style.
        private static void PerAnimation_Postfix(object __result)
        {
            if (!_batchActive) return;
            if (__result == null) return;

            // Case 1: single ImportResult
            var clip = ExtractClip(__result);
            if (clip != null)
            {
                _batch.Add(clip);
                Plugin.Log.LogInfo($"[per-anim] captured clip '{clip.name}'");
                return;
            }

            // Case 2: enumerable of ImportResult
            if (__result is IEnumerable e && !(__result is string))
            {
                var before = _batch.Count;
                foreach (var item in e)
                {
                    var c = ExtractClip(item);
                    if (c != null) _batch.Add(c);
                }
                if (_batch.Count > before)
                {
                    Plugin.Log.LogInfo($"[per-anim] captured {_batch.Count - before} clip(s) from enumerable result.");
                    return;
                }
            }

            Plugin.Log.LogInfo($"[per-anim] result shape unrecognised: {__result.GetType().FullName}");
        }

        private static AnimationClip ExtractClip(object importResult)
        {
            if (importResult == null) return null;
            if (importResult is AnimationClip direct) return direct;

            var t = importResult.GetType();
            var f = t.GetField("clip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && typeof(AnimationClip).IsAssignableFrom(f.FieldType))
                return f.GetValue(importResult) as AnimationClip;

            var p = t.GetProperty("clip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && typeof(AnimationClip).IsAssignableFrom(p.PropertyType))
                return p.GetValue(importResult) as AnimationClip;

            return null;
        }
    }
}
