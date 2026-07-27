using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// v3.1.22 self-heal for corrupted RpgMeshCache files.
    ///
    /// When Deserialize throws (FormatException from truncated / wrong-format
    /// bytes), the cached file on disk stays broken and every subsequent load
    /// attempt hits the same slow re-request-from-server path. This finalizer
    /// intercepts the exception, deletes the corrupt file, and lets the
    /// exception propagate so the engine's existing fallback (request from
    /// server) still runs. Next launch: file is gone, no failed deserialize,
    /// clean fetch.
    /// </summary>
    internal static class CorruptCacheSelfHealPatch
    {
        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            var cacheType = AccessTools.TypeByName("RpgMeshCache")
                         ?? AccessTools.TypeByName("RpgEngine.RpgMeshCache");
            if (cacheType == null)
            {
                log.LogWarning("CorruptCacheSelfHealPatch: RpgMeshCache not found.");
                return;
            }

            // Signature: public override RPGMesh Deserialize(string name, byte[] bytes)
            var deserialize = AccessTools.Method(cacheType, "Deserialize",
                new[] { typeof(string), typeof(byte[]) });
            if (deserialize == null)
            {
                log.LogWarning("CorruptCacheSelfHealPatch: RpgMeshCache.Deserialize not found.");
                return;
            }

            try
            {
                harmony.Patch(
                    original: deserialize,
                    finalizer: new HarmonyMethod(typeof(CorruptCacheSelfHealPatch), nameof(Deserialize_Finalizer)));
                log.LogInfo("Patched RpgMeshCache.Deserialize (finalizer, deletes corrupt cache files on FormatException).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"CorruptCacheSelfHealPatch: failed to patch Deserialize: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs after Deserialize (whether it succeeded or threw). If it threw
        /// a FormatException, delete the on-disk cache file for that mesh id
        /// so the engine's request-from-server fallback can replace it with
        /// clean data next time. Returning the exception unchanged lets the
        /// engine's normal error path still run (so the fallback still fires).
        /// </summary>
        private static Exception Deserialize_Finalizer(
            object __instance, string name, Exception __exception)
        {
            if (__exception == null) return null;
            // Only handle format-style exceptions. Anything else (NRE etc.)
            // should propagate normally so real bugs aren't silently swallowed.
            if (!(__exception is FormatException) && !(__exception is IndexOutOfRangeException))
                return __exception;

            try
            {
                // Read folderPath + fileExtension off the instance so we don't
                // hardcode them (both are declared on RpgMeshCache but overriden
                // from ItemCache abstract properties).
                var t = __instance.GetType();
                var folderProp = t.GetProperty("folderPath",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var extProp = t.GetProperty("fileExtension",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var folder = folderProp?.GetValue(__instance) as string;
                var ext = extProp?.GetValue(__instance) as string;

                if (!string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(name))
                {
                    var path = Path.Combine(folder, name + "." + ext);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        Plugin.Log.LogWarning($"[self-heal] Deleted corrupt cache file '{path}' (FormatException on load). Engine will re-fetch from server.");
                    }
                }
            }
            catch (Exception cleanupEx)
            {
                Plugin.Log.LogWarning($"[self-heal] Cleanup itself threw (non-fatal): {cleanupEx.Message}");
            }

            // Let the FormatException propagate so the engine's existing
            // exception handling (which triggers RequestItemFromServer)
            // still runs.
            return __exception;
        }
    }
}
