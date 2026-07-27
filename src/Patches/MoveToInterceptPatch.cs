using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
// PointerEventData intentionally referenced as `object` to avoid needing
// UnityEngine.UI / InputModule assembly references, Harmony matches by name.

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// The engine's MoveTo picker is hard-gated on `target is PickupableAI` ,
    /// see `MoveTo.OnLeftMouseUp` in the decompilation. When the user clicks
    /// a destination for our tagged custom mesh (which is a PickupableMesh),
    /// nothing happens because the gate rejects it.
    ///
    /// We prefix OnLeftMouseUp: if the target isn't a PickupableAI but has
    /// our HumanoidTag + a Puppet, run the same logic ourselves (raycast →
    /// puppet.ClosestDestination → target.MoveToGlobalLocation → NetworkSend
    /// → Hide the picker) and return false to skip the original.
    /// </summary>
    internal static class MoveToInterceptPatch
    {
        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var moveToType = AccessTools.TypeByName("RpgEngine.NavState.MoveTo");
            if (moveToType == null) { log.LogWarning("MoveToInterceptPatch: MoveTo not found."); return; }

            var onLeftUp = AccessTools.Method(moveToType, "OnLeftMouseUp");
            if (onLeftUp == null) { log.LogWarning("MoveToInterceptPatch: OnLeftMouseUp not found."); return; }

            try
            {
                harmony.Patch(
                    original: onLeftUp,
                    prefix: new HarmonyMethod(typeof(MoveToInterceptPatch), nameof(OnLeftMouseUp_Prefix)));
                log.LogInfo("Patched MoveTo.OnLeftMouseUp (prefix, handles our tagged custom meshes).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"MoveToInterceptPatch: failed to patch OnLeftMouseUp: {ex.Message}");
            }
        }

        private static bool OnLeftMouseUp_Prefix(object __instance, object d)
        {
            try
            {
                // `target` is a `protected Pickupable target;` FIELD on
                // StaticBase<T> (the generic base of MoveTo). GetProperty
                // returns null, and FlattenHierarchy doesn't include
                // non-public instance fields inherited from base classes ,
                // walk the base chain by hand.
                object rawTarget = null;
                for (var t = __instance.GetType(); t != null && rawTarget == null; t = t.BaseType)
                {
                    var f = t.GetField("target",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null) rawTarget = f.GetValue(__instance);
                }
                var target = rawTarget as Component;
                var targetDesc = target == null ? "<null>" : $"{target.GetType().Name} '{target.name}'";
                Plugin.Log.LogInfo($"[move] MoveTo.OnLeftMouseUp prefix fired. target={targetDesc}");
                if (target == null) return true;

                // If it's a PickupableAI, let the engine's own handler take it.
                var aiType = AccessTools.TypeByName("RpgEngine.PickupableAI");
                if (aiType != null && aiType.IsInstanceOfType(target)) return true;

                // Only handle OUR tagged custom meshes.
                var tag = target.GetComponent<HumanoidTag>();
                if (tag == null) { Plugin.Log.LogInfo($"[move] target has no HumanoidTag, passing to original."); return true; }

                // Raycast to find the destination. NavStateController inherits
                // its static `instance` field from UniqueMono<>, read it
                // directly with FlattenHierarchy (see TogglePicker for context).
                var navCtrlType = AccessTools.TypeByName("RpgEngine.NavState.NavStateController");
                if (navCtrlType == null) return true;
                var navInstField = navCtrlType.GetField("instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var navCtrl = navInstField?.GetValue(null);
                if (navCtrl == null) return true;

                // TryRayCast(out RaycastHit hit), disambiguate against the
                // (LayerMask, out RaycastHit) overload with an explicit signature.
                var tryRay = AccessTools.Method(navCtrlType, "TryRayCast",
                    new[] { typeof(RaycastHit).MakeByRefType() });
                if (tryRay == null) return true;
                var args = new object[] { default(RaycastHit) };
                var ok = (bool)tryRay.Invoke(navCtrl, args);
                if (!ok)
                {
                    HidePicker(__instance);
                    return false;
                }
                var hit = (RaycastHit)args[0];
                var point = hit.point;

                // puppet.ClosestDestination(point), use AccessTools.Method to
                // avoid AmbiguousMatchException on inherited/overloaded methods.
                var puppetType = AccessTools.TypeByName("RpgEngine.Puppet");
                var puppet = puppetType != null ? target.GetComponent(puppetType) : null;
                var vector = point;
                if (puppet != null)
                {
                    var closest = AccessTools.Method(puppetType, "ClosestDestination",
                        new[] { typeof(Vector3) });
                    if (closest != null) vector = (Vector3)closest.Invoke(puppet, new object[] { point });
                }

                // if (LobbyManager.isServer) target.MoveToGlobalLocation(vector);
                // Real type is RpgEngine.Steam.RpgLobbyManager. If we can't
                // find it at all, assume single-player (isServer = true) so
                // movement still works out of the box.
                // isServer lives on UnityUtils.Steam.LobbyManager (base of
                // RpgLobbyManager). Look at the base type directly; a lookup
                // on the subclass without FlattenHierarchy returns null.
                var lobbyType = AccessTools.TypeByName("UnityUtils.Steam.LobbyManager")
                             ?? AccessTools.TypeByName("RpgEngine.Steam.RpgLobbyManager");
                bool isServer = true;
                if (lobbyType != null)
                {
                    var isServerProp = lobbyType.GetProperty("isServer",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (isServerProp != null) isServer = (bool)isServerProp.GetValue(null);
                }
                if (isServer)
                {
                    // Base Pickupable.MoveToGlobalLocation just teleports
                    // (SetPosition); only PickupableAI's override calls
                    // puppet.SetDestination for the smooth walk. Reproduce
                    // that override manually here so our PickupableMesh walks.
                    if (puppet != null)
                    {
                        var setDest = AccessTools.Method(puppetType, "SetDestination",
                            new[] { typeof(Vector3) });
                        if (setDest != null)
                        {
                            setDest.Invoke(puppet, new object[] { vector });
                            Plugin.Log.LogInfo($"[move] puppet.SetDestination({vector}), walking.");
                        }
                        else
                        {
                            Plugin.Log.LogWarning("[move] Puppet.SetDestination not found, falling back to base teleport.");
                            var moveMethod = AccessTools.Method(target.GetType(), "MoveToGlobalLocation", new[] { typeof(Vector3) });
                            moveMethod?.Invoke(target, new object[] { vector });
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[move] no Puppet, falling back to base teleport.");
                        var moveMethod = AccessTools.Method(target.GetType(), "MoveToGlobalLocation", new[] { typeof(Vector3) });
                        moveMethod?.Invoke(target, new object[] { vector });
                    }
                }

                // NetworkSend.MoveAI(target.guid, vector), best-effort.
                var guidProp = target.GetType().GetProperty("guid");
                var guid = guidProp?.GetValue(target) as string;
                var netSendType = AccessTools.TypeByName("RpgEngine.Networking.NetworkSend")
                                ?? AccessTools.TypeByName("RpgEngine.NetworkSend");
                if (netSendType != null && guid != null)
                {
                    var moveAi = AccessTools.Method(netSendType, "MoveAI",
                        new[] { typeof(string), typeof(Vector3) });
                    moveAi?.Invoke(null, new object[] { guid, vector });
                }

                Plugin.Log.LogInfo($"[move] destination click on '{target.name}' → walking to {vector} (server={isServer}).");

                HidePicker(__instance);
                return false;   // skip original
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"MoveToInterceptPatch prefix threw (letting original run): {ex}");
                return true;
            }
        }

        private static void HidePicker(object moveToInstance)
        {
            try
            {
                var hide = moveToInstance.GetType().GetMethod("Hide", Type.EmptyTypes);
                hide?.Invoke(moveToInstance, null);
            }
            catch { }
        }
    }
}
