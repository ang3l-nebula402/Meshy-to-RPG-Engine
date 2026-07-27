using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// Bridges our per-mesh clip catalog into the engine's native Animation
    /// panel UI, so imported custom characters get proper "Idle", "Walking",
    /// "Attack" buttons that match the built-in character's look.
    ///
    /// Data flow (from RpgEngine.dll decompilation):
    ///
    ///   AnimationList.ReGenerateButtons()
    ///     └─ SpawnAnimationButtons(target, pools…)
    ///          └─ target.AnimationsForCurrentState()
    ///              └─ PickupableExtensions.FindAllAnimations(target, activeMode)
    ///                   ↑ iterates target.animator.animations, SKIPS if no Animator.
    ///                     Custom Mesh imports have no Animator (legacy Animation),
    ///                     so nothing is contributed → no buttons appear.
    ///
    /// Button click flow:
    ///
    ///   NavAnimationButton.DoAnimation() → Animate() → targets.SetAnimation(action)
    ///     └─ Pickupable.SetAnimation(string action)  (virtual)
    ///          └─ animator.SetTrigger(action)  ← no-op for us
    ///
    /// Fixes:
    ///
    ///   1. Postfix FindAllAnimations to append our clip names as
    ///      (name, AnimationDelegate.Type.Trigger, [pickupable]) tuples.
    ///      No dependency on an Animator being present.
    ///
    ///   2. Prefix the parameterless Pickupable.SetAnimation(string), if the
    ///      pickupable has our exposer and the action matches a clip name,
    ///      route the click to PickupableAnimationExposer.PlayClip and skip
    ///      the original (which would try Animator.SetTrigger on nothing).
    /// </summary>
    internal static class NativeAnimationUiPatch
    {
        // AnimationDelegate.Type.Trigger, from decompilation.
        // The enum values are FloatClamped=1, FloatAny=2, Int=3, Bool=4,
        // Trigger=9, String=10. Type.Trigger is the "plain button" variant
        // that renders via pool_animationButtonPrefab.
        private const int TriggerTypeEnumValue = 9;

        private static Type _animationDelegateType;   // resolved at Apply time
        private static object _triggerEnumValue;

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;

            // Resolve the AnimationDelegate.Type enum + Trigger value once.
            _animationDelegateType = AccessTools.TypeByName("RpgEngine.Animation.AnimationDelegate+Type")
                                 ?? AccessTools.Inner(AccessTools.TypeByName("RpgEngine.Animation.AnimationDelegate"), "Type");
            if (_animationDelegateType == null)
            {
                log.LogError("NativeAnimationUiPatch: AnimationDelegate.Type enum not found. Cannot integrate with native UI.");
                return;
            }
            _triggerEnumValue = Enum.ToObject(_animationDelegateType, TriggerTypeEnumValue);
            log.LogInfo($"AnimationDelegate.Type resolved; Trigger = {_triggerEnumValue} (int {TriggerTypeEnumValue})");

            // ── Patch 1: FindAllAnimations postfix (adds our clips to the panel) ──
            // NOTE: there are FIVE PickupableExtensions classes across different
            // namespaces (Permissions, Parenting, Layers, Animation, NavState).
            // The one we want lives under RpgEngine.Animation.
            var pickupableExtType = AccessTools.TypeByName("RpgEngine.Animation.PickupableExtensions");
            if (pickupableExtType == null)
            {
                log.LogError("NativeAnimationUiPatch: RpgEngine.Animation.PickupableExtensions not found.");
                return;
            }
            // Enumerate by name, AccessTools.Method(type, name) with no
            // signature argument only matches zero-arg methods, but
            // FindAllAnimations takes (Pickupable self, EnabledModes query).
            var findAll = pickupableExtType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FindAllAnimations");
            if (findAll == null)
            {
                log.LogError("NativeAnimationUiPatch: FindAllAnimations method not found.");
                return;
            }
            var faParms = string.Join(", ", findAll.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            log.LogInfo($"Target: PickupableExtensions.FindAllAnimations({faParms})");
            try
            {
                harmony.Patch(
                    original: findAll,
                    postfix: new HarmonyMethod(typeof(NativeAnimationUiPatch), nameof(FindAllAnimations_Postfix)));
                log.LogInfo("Patched PickupableExtensions.FindAllAnimations (postfix, appends our clips).");
            }
            catch (Exception ex)
            {
                log.LogError($"Failed to patch FindAllAnimations: {ex.Message}");
            }

            // ── Patch 2: PickupableExtensions.SetAnimation(IList<Pickupable>, string) prefix ──
            // We patch the IList overload rather than the per-Pickupable virtual
            // because the IList overload gates the per-pickupable call behind
            // `if (LobbyManager.isServer)`, if that's false (offline / mid-lobby /
            // local-only mode), the virtual never runs, so a prefix there would
            // miss button clicks. The IList overload ALWAYS runs from
            // NavAnimationButton.Animate() so it's a reliable catch.
            var setAnimIList = pickupableExtType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "SetAnimation" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType == typeof(string) &&
                    m.GetParameters()[0].ParameterType.IsGenericType &&
                    m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IList<>));
            if (setAnimIList == null)
            {
                log.LogError("NativeAnimationUiPatch: SetAnimation(IList<Pickupable>, string) trigger overload not found.");
                return;
            }
            try
            {
                harmony.Patch(
                    original: setAnimIList,
                    prefix: new HarmonyMethod(typeof(NativeAnimationUiPatch), nameof(SetAnimationIList_Prefix)));
                log.LogInfo("Patched PickupableExtensions.SetAnimation(IList, string) (prefix, routes clicks to our exposer).");
            }
            catch (Exception ex)
            {
                log.LogError($"Failed to patch SetAnimation(IList): {ex.Message}");
            }

            // Also hook NavAnimationButton.Animate / DoAnimation / DoButton
            // as diagnostic tripwires so we can see how far a click gets.
            var navBtnType = AccessTools.TypeByName("RpgEngine.NavState.NavAnimationButton");
            if (navBtnType != null)
            {
                foreach (var name in new[] { "DoButton", "DoAnimation", "Animate" })
                {
                    var m = AccessTools.Method(navBtnType, name);
                    if (m == null) continue;
                    try
                    {
                        harmony.Patch(
                            original: m,
                            prefix: new HarmonyMethod(typeof(NativeAnimationUiPatch), nameof(NavButton_Trace_Prefix)));
                        log.LogInfo($"Trace-patched NavAnimationButton.{name}");
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning($"Could not trace-patch NavAnimationButton.{name}: {ex.Message}");
                    }
                }
            }

            // Also keep the per-Pickupable prefix as belt-and-braces for the
            // isServer=true path.
            var pickupableType = AccessTools.TypeByName("RpgEngine.Pickupable");
            if (pickupableType != null)
            {
                var setAnim = pickupableType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "SetAnimation" &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));
                if (setAnim != null)
                {
                    try
                    {
                        harmony.Patch(
                            original: setAnim,
                            prefix: new HarmonyMethod(typeof(NativeAnimationUiPatch), nameof(SetAnimationTrigger_Prefix)));
                        log.LogInfo("Also patched Pickupable.SetAnimation(string) (prefix, belt-and-braces).");
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning($"Could not patch per-Pickupable SetAnimation: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Postfix on PickupableExtensions.FindAllAnimations. The original
        /// yields (name, Type, IList&lt;Pickupable&gt;) tuples for every
        /// Animator parameter it finds across the pickupable's clipboard
        /// (root + children). We append our clip names as Trigger tuples for
        /// any pickupable in that tree that has our exposer.
        ///
        /// CRITICAL: we must return an IEnumerable whose element type EXACTLY
        /// matches the original, the consumer's `foreach` unpacks each item
        /// as a `ValueTuple<string, AnimationDelegate.Type, IList<Pickupable>>`,
        /// and a mistyped list crashes the C# runtime hard when iterated (was
        /// the cause of a native-side crash in v1.3.2).
        /// </summary>
        private static void FindAllAnimations_Postfix(object self, ref System.Collections.IEnumerable __result)
        {
            try
            {
                if (self == null || __result == null) return;

                var selfComp = self as Component;
                var selfName = selfComp?.name ?? "<null>";
                var selfType = self.GetType().Name;

                var appended = AppendOurClips(self, __result);
                if (appended != null)
                {
                    __result = appended;
                    return;
                }

                // Materialize the original into a typed list so we can log its
                // size AND so we hand back a safely re-iterable collection
                // (the engine may enumerate the result more than once; the
                // original might be a yield-return generator that can't).
                var origInterfaces = __result.GetType().GetInterfaces()
                    .Concat(new[] { __result.GetType() });
                var enumerableIface = origInterfaces
                    .FirstOrDefault(i => i.IsGenericType &&
                                         i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>));
                if (enumerableIface == null) return;
                var tupleType = enumerableIface.GetGenericArguments()[0];
                var outerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(tupleType);
                var outerList = (System.Collections.IList)Activator.CreateInstance(outerListType);
                foreach (var item in __result) if (item != null) outerList.Add(item);
                Plugin.Log.LogInfo($"[panel] FindAllAnimations({selfType} '{selfName}') → {outerList.Count} animation(s).");
                __result = (System.Collections.IEnumerable)outerList;
            }
            catch (Exception ex)
            {
                // Never let this postfix take the game down.
                Plugin.Log.LogWarning($"FindAllAnimations_Postfix threw (leaving original result untouched): {ex}");
            }
        }

        private static System.Collections.IEnumerable AppendOurClips(object pickupable, System.Collections.IEnumerable original)
        {
            var pickupableComponent = pickupable as Component;
            if (pickupableComponent == null) return null;

            var exposer = pickupableComponent.GetComponentInChildren<PickupableAnimationExposer>();
            if (exposer == null || exposer.ClipNames == null || exposer.ClipNames.Count == 0)
                return null; // nothing to add

            // Discover the exact element type the original enumerable uses.
            // GetType().GetGenericArguments()[0] on an IEnumerable<T> gives us
            // that T, the ValueTuple<string, AnimationDelegate.Type, IList<Pickupable>>.
            var origInterfaces = original.GetType().GetInterfaces()
                .Concat(new[] { original.GetType() });
            var enumerableIface = origInterfaces
                .FirstOrDefault(i => i.IsGenericType &&
                                     i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>));
            if (enumerableIface == null)
            {
                Plugin.Log.LogWarning("FindAllAnimations return type isn't IEnumerable<T>, skipping append.");
                return null;
            }
            var tupleType = enumerableIface.GetGenericArguments()[0];

            // Build the typed inner IList<Pickupable> that goes inside each tuple.
            // We use whichever concrete Pickupable subclass this instance is ,
            // the tuple's IList<Pickupable> is invariant so we need the base type.
            var pickupableBaseType = AccessTools.TypeByName("RpgEngine.Pickupable") ?? pickupable.GetType();
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(pickupableBaseType);
            var innerList = Activator.CreateInstance(listType);
            listType.GetMethod("Add").Invoke(innerList, new[] { pickupable });

            // Build a strongly-typed List<TupleType>, same element type as original.
            var outerListType = typeof(System.Collections.Generic.List<>).MakeGenericType(tupleType);
            var outerList = (System.Collections.IList)Activator.CreateInstance(outerListType);

            // Copy the original items in first (preserves engine's own animations).
            foreach (var item in original)
            {
                if (item != null) outerList.Add(item);
            }

            // Append our clips.
            foreach (var name in exposer.ClipNames)
            {
                if (string.IsNullOrEmpty(name) || name.StartsWith("_")) continue;
                var tuple = Activator.CreateInstance(tupleType, name, _triggerEnumValue, innerList);
                outerList.Add(tuple);
            }

            Plugin.Log.LogInfo($"FindAllAnimations postfix: appended {exposer.ClipNames.Count} clip(s) to native panel for '{pickupableComponent.name}'.");
            return (System.Collections.IEnumerable)outerList;
        }

        /// <summary>
        /// Prefix on the IList overload: PickupableExtensions.SetAnimation(
        /// IList&lt;Pickupable&gt; self, string action). Called every time
        /// NavAnimationButton.Animate() fires, regardless of network state.
        ///
        /// Walks the targets list, and for each pickupable that has our
        /// exposer with a matching clip, plays via our path. If ALL targets
        /// were handled by us, returns false to skip the original (which
        /// would try Animator.SetTrigger + NetworkSend on nothing).
        /// If any target was NOT ours, lets the original run so native
        /// pickupables in the same list still get their animation.
        /// </summary>
        private static bool SetAnimationIList_Prefix(System.Collections.IEnumerable self, string action)
        {
            Plugin.Log.LogInfo($"[click] SetAnimation(IList, '{action}') prefix fired.");
            if (self == null || string.IsNullOrEmpty(action)) return true;

            int handled = 0;
            int total = 0;
            try
            {
                foreach (var target in self)
                {
                    total++;
                    Plugin.Log.LogInfo($"  [click] target[{total-1}]: {target?.GetType().FullName ?? "null"}");
                    var comp = target as Component;
                    if (comp == null)
                    {
                        Plugin.Log.LogInfo($"    → not a Component; skipping.");
                        continue;
                    }
                    Plugin.Log.LogInfo($"    → Component on GameObject '{comp.gameObject.name}'");

                    var exposerSelf = comp.GetComponent<PickupableAnimationExposer>();
                    var exposerChildren = comp.GetComponentInChildren<PickupableAnimationExposer>(true);
                    Plugin.Log.LogInfo($"    → GetComponent<Exposer>={(exposerSelf != null)} | GetComponentInChildren<Exposer>={(exposerChildren != null)}");

                    var exposer = exposerSelf ?? exposerChildren;
                    if (exposer == null)
                    {
                        // v2.1.4: only intervene for OUR custom mesh (marked with
                        // HumanoidTag). Native characters must still go through the
                        // engine's original routing, hijacking those was blocking
                        // NetworkSend + Pickupable.SetAnimation bookkeeping.
                        var marker = comp.GetComponent<HumanoidTag>();
                        if (marker == null) marker = comp.GetComponentInChildren<HumanoidTag>(true);
                        if (marker == null) continue; // native, let original engine run

                        var animator = comp.GetComponentInChildren<Animator>(true);
                        if (animator == null || animator.runtimeAnimatorController == null)
                        {
                            Plugin.Log.LogInfo($"    → custom mesh but no Animator on '{comp.gameObject.name}'.");
                            continue;
                        }

                        // Diagnostic: state before + Pickupable.animator (IAnimator) resolution.
                        try
                        {
                            var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                            var animatorProp = comp.GetType().GetProperty("animator",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var iAnim = animatorProp?.GetValue(comp);
                            Plugin.Log.LogInfo($"    → custom mesh diag: animator.enabled={animator.enabled}, " +
                                               $"currentState.hash={stateInfo.shortNameHash}, " +
                                               $"pickupable.animator={(iAnim == null ? "NULL" : iAnim.GetType().Name)}");
                        }
                        catch (Exception dex) { Plugin.Log.LogInfo($"    → diag threw: {dex.Message}"); }

                        // Belt-and-braces: force-enable the Animator on every click.
                        // Some engine paths flip it off between panel opens.
                        if (!animator.enabled)
                        {
                            animator.enabled = true;
                            Plugin.Log.LogInfo($"    → re-enabled Animator on '{animator.gameObject.name}'.");
                        }

                        // With the Animator enabled, the engine's original
                        // Pickupable.SetAnimation → animator.SetTrigger will
                        // now actually fire, so we don't need to duplicate it
                        // here. The `continue` below still lets the original run.

                        // Never mark handled, always let the engine's original
                        // SetAnimation also run (for NetworkSend, other wiring).
                        continue;
                    }

                    Plugin.Log.LogInfo($"    → exposer found on '{exposer.gameObject.name}', clips=[{string.Join(",", exposer.ClipNames)}]");

                    if (exposer.ClipNames == null || !exposer.ClipNames.Contains(action))
                    {
                        Plugin.Log.LogInfo($"    → action '{action}' not in this exposer's clip list.");
                        continue;
                    }

                    if (exposer.PlayClip(action))
                    {
                        handled++;
                        Plugin.Log.LogInfo($"Native UI → PlayClip('{action}') on '{exposer.gameObject.name}'.");
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"    → PlayClip returned false.");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SetAnimationIList_Prefix threw (letting original run): {ex.Message}");
                return true;
            }

            Plugin.Log.LogInfo($"[click] result: handled {handled} of {total}");
            return handled == 0 || handled < total;
        }

        // Diagnostic tripwire on NavAnimationButton lifecycle, proves whether
        // the click even makes it past the UI layer. Fires unconditionally.
        private static void NavButton_Trace_Prefix(object __instance, MethodBase __originalMethod)
        {
            try
            {
                var actionField = __instance.GetType().GetField("action",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var action = actionField?.GetValue(__instance) as string;
                Plugin.Log.LogInfo($"[click] NavAnimationButton.{__originalMethod.Name} fired. action='{action}'");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogInfo($"[click] NavAnimationButton.{__originalMethod.Name} fired (trace threw: {ex.Message})");
            }
        }

        /// <summary>
        /// Prefix on Pickupable.SetAnimation(string). Belt-and-braces catch
        /// for the isServer=true path (where the IList extension does
        /// invoke the per-pickupable virtual).
        /// </summary>
        private static bool SetAnimationTrigger_Prefix(object __instance, string action)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return true;
                var exposer = comp.GetComponentInChildren<PickupableAnimationExposer>();
                if (exposer == null || !exposer.ClipNames.Contains(action)) return true;

                if (exposer.PlayClip(action))
                {
                    Plugin.Log.LogInfo($"Native UI → PlayClip('{action}') on '{exposer.gameObject.name}'.");
                    return false; // handled
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SetAnimationTrigger_Prefix threw (letting original run): {ex.Message}");
            }
            return true;
        }
    }
}
