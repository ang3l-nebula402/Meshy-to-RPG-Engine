using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// The engine rotates characters to face their aim target via
    /// LookAtState.TryLookAt, called every frame while the AimAt picker is
    /// active. But TryLookAt is hard-gated on `_base.target is PickupableAI`
    ///, same trap as MoveTo. Our tagged custom mesh is a PickupableMesh,
    /// so it never rotates on aimed actions (bow shots, etc.).
    ///
    /// Prefix TryLookAt so tagged meshes get the LookAt.LookAt(...) static
    /// call the AI branch runs. Don't touch native pickupables, original
    /// path handles those correctly.
    /// </summary>
    internal static class LookAtInterceptPatch
    {
        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var t = AccessTools.TypeByName("RpgEngine.NavState.LookAtState");
            if (t == null) { log.LogWarning("LookAtInterceptPatch: LookAtState not found."); return; }

            var tryLookAt = t.GetMethod("TryLookAt",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (tryLookAt == null) { log.LogWarning("LookAtInterceptPatch: TryLookAt not found."); return; }

            try
            {
                harmony.Patch(
                    original: tryLookAt,
                    prefix: new HarmonyMethod(typeof(LookAtInterceptPatch), nameof(TryLookAt_Prefix)));
                log.LogInfo("Patched LookAtState.TryLookAt (prefix, rotates our tagged custom meshes toward aim target).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"LookAtInterceptPatch: failed to patch TryLookAt: {ex.Message}");
            }
        }

        // Signature: private void TryLookAt(bool TCP = true)
        private static bool TryLookAt_Prefix(object __instance, bool TCP)
        {
            try
            {
                // Read the private `_base` field (NavStateController owner).
                var baseField = __instance.GetType().GetField("_base",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var navCtrl = baseField?.GetValue(__instance);
                if (navCtrl == null) return true;

                // Read NavStateController.target (its own target field/prop).
                object rawTarget = null;
                for (var t = navCtrl.GetType(); t != null && rawTarget == null; t = t.BaseType)
                {
                    var f = t.GetField("target",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null) rawTarget = f.GetValue(navCtrl);
                    if (rawTarget != null) break;
                    var p = t.GetProperty("target",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (p != null) rawTarget = p.GetValue(navCtrl);
                }
                var target = rawTarget as Component;
                if (target == null) return true;

                // If PickupableAI, defer to the original, it already works.
                var aiType = AccessTools.TypeByName("RpgEngine.PickupableAI");
                if (aiType != null && aiType.IsInstanceOfType(target)) return true;

                // Only handle our tagged custom meshes.
                if (target.GetComponent<HumanoidTag>() == null) return true;

                // TryRayCast to get the aim point.
                var navCtrlType = navCtrl.GetType();
                var tryRay = AccessTools.Method(navCtrlType, "TryRayCast",
                    new[] { typeof(RaycastHit).MakeByRefType() });
                if (tryRay == null) return true;
                var args = new object[] { default(RaycastHit) };
                var ok = (bool)tryRay.Invoke(navCtrl, args);
                if (!ok) return false;   // no hit, skip original (which would also skip)
                var hit = (RaycastHit)args[0];

                // Call the static LookAtState.LookAt(target, position, TCP).
                var lookAtType = __instance.GetType();
                var lookAtStatic = lookAtType.GetMethod("LookAt",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { AccessTools.TypeByName("RpgEngine.Pickupable"), typeof(Vector3), typeof(bool) },
                    modifiers: null);
                if (lookAtStatic == null) return true;
                lookAtStatic.Invoke(null, new object[] { target, hit.point, TCP });
                return false;   // handled
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LookAtInterceptPatch prefix threw (letting original run): {ex}");
                return true;
            }
        }
    }
}
