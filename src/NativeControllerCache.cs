using System.Linq;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// Caches the engine's shared humanoid RuntimeAnimatorController (C_Humanoid)
    /// so we can assign it to our imported meshes. The controller carries all
    /// the state transitions, dance/emote/combat clips, and locomotion blend
    /// tree, inheriting it means our imports get the full native animation
    /// library for free.
    ///
    /// Two capture strategies:
    ///   1. Eager: on plugin startup, Resources.FindObjectsOfTypeAll pulls in
    ///      every loaded RuntimeAnimatorController, filter for "C_Humanoid".
    ///      Works whenever the controller has already been loaded by the
    ///      engine (usually at startup for the character system).
    ///   2. Lazy: NativeSkeletonProbe caches it from the first native
    ///      character clicked. Fallback if the eager search finds nothing.
    /// </summary>
    internal static class NativeControllerCache
    {
        internal static RuntimeAnimatorController Controller;

        /// <summary>
        /// Try to find the engine's C_Humanoid controller across all loaded
        /// Unity objects. Cheap enough to call on demand.
        /// </summary>
        internal static RuntimeAnimatorController TryFind()
        {
            if (Controller != null) return Controller;

            var all = Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>();
            // Prefer exact-name match "C_Humanoid"; fall back to any name
            // containing "Humanoid" so we survive minor engine renames.
            Controller = all.FirstOrDefault(c => c.name == "C_Humanoid")
                      ?? all.FirstOrDefault(c => c.name != null && c.name.Contains("Humanoid"));
            if (Controller != null)
            {
                Plugin.Log.LogInfo($"NativeControllerCache: cached controller '{Controller.name}' " +
                                   $"({Controller.animationClips.Length} clips).");
            }
            return Controller;
        }

        internal static void Capture(RuntimeAnimatorController controller)
        {
            if (Controller != null || controller == null) return;
            Controller = controller;
            Plugin.Log.LogInfo($"NativeControllerCache: captured controller '{Controller.name}' from live character.");
        }
    }
}
