using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// v3.1.3, peer-side movement bridge.
    ///
    /// When the host clicks Move on a custom mesh and clicks a destination,
    /// our MoveToInterceptPatch calls Puppet.SetDestination on the host
    /// (smooth walk) and fires NetworkSend.MoveAI to peers. The peer then
    /// receives ClientReceive.MoveAIStart → looks up the pickupable →
    /// calls MoveToGlobalLocation(vector).
    ///
    /// Problem: for PickupableMesh (custom), MoveToGlobalLocation is the base
    /// Pickupable virtual which just SetPosition-teleports. PickupableAI
    /// overrides it to use puppet.SetDestination for smooth NavMesh walk.
    ///
    /// Fix: prefix Pickupable.MoveToGlobalLocation. If the target has our
    /// HumanoidTag AND a Puppet + NavMeshAgent on the mesh, do the AI-style
    /// SetDestination instead of teleport. Peer walks smoothly, animator's
    /// Movement Speed float drives the walk animation.
    /// </summary>
    internal static class PeerMovementPatch
    {
        private static Type _puppetType;

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            _puppetType = AccessTools.TypeByName("RpgEngine.Puppet");

            var pickupableType = AccessTools.TypeByName("RpgEngine.Pickupable");
            if (pickupableType == null) { log.LogWarning("PeerMovementPatch: Pickupable not found."); return; }

            var mtg = AccessTools.Method(pickupableType, "MoveToGlobalLocation",
                new[] { typeof(Vector3) });
            if (mtg == null) { log.LogWarning("PeerMovementPatch: MoveToGlobalLocation(Vector3) not found."); return; }

            try
            {
                harmony.Patch(
                    original: mtg,
                    prefix: new HarmonyMethod(typeof(PeerMovementPatch), nameof(MoveToGlobalLocation_Prefix)));
                log.LogInfo("Patched Pickupable.MoveToGlobalLocation (prefix, smooth NavMesh walk for tagged custom meshes on peers too).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"PeerMovementPatch: failed: {ex.Message}");
            }
        }

        // Signature: public virtual void MoveToGlobalLocation(Vector3 value)
        private static bool MoveToGlobalLocation_Prefix(object __instance, Vector3 value)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return true;
                if (comp.GetComponent<HumanoidTag>() == null) return true;   // not ours; let base run

                // PickupableAI already overrides this and works, pass through.
                var aiType = AccessTools.TypeByName("RpgEngine.PickupableAI");
                if (aiType != null && aiType.IsInstanceOfType(comp)) return true;

                // Our custom mesh: use Puppet for smooth walk instead of teleport.
                if (_puppetType == null) return true;
                var puppet = comp.GetComponent(_puppetType);
                if (puppet == null) return true;

                var setDest = AccessTools.Method(_puppetType, "SetDestination",
                    new[] { typeof(Vector3) });
                if (setDest == null) return true;

                setDest.Invoke(puppet, new object[] { value });
                Plugin.Log.LogInfo($"[peer-move] '{comp.name}' → puppet.SetDestination({value}), walking (not teleporting).");
                return false;   // skip base SetPosition teleport
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"PeerMovementPatch prefix threw (letting original run): {ex}");
                return true;
            }
        }
    }
}
