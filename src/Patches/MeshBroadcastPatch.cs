using System;
using System.Reflection;
using HarmonyLib;

namespace RpgEngineAnimatedMeshMod.Patches
{
    /// <summary>
    /// v3.0: multiplayer sync for custom meshes.
    ///
    /// The engine already ships full peer transfer plumbing, packet 110
    /// (`ServerSend.UpdateGlobalRPGMesh`) broadcasts a mesh's raw bytes,
    /// packet 12 (`ClientSend.RequestRPGMesh`) is the pull request from a
    /// joining peer. The break is that when a mesh is placed on the host's
    /// machine, the bytes land in the client-side `RpgMeshCache` on that
    /// process, but `HasClients` on `ServerSend` short-circuits any
    /// broadcast unless the engine itself initiates one, which it never
    /// does for locally-placed custom meshes. So peers ask the host, the
    /// host's server-side cache has the mesh (same process, same cache),
    /// but nothing pushes it OUT to the peer that just spawned the prop.
    ///
    /// Fix: postfix `PickupableMesh.SetRPGMesh(RPGMesh)`. When the host
    /// finishes loading a mesh locally, push those bytes to every connected
    /// peer via `UpdateGlobalRPGMesh`. Peers cache them; then when their
    /// own `PickupableMesh.TryLoadMesh(name)` runs (from the Prop data the
    /// engine already syncs), the cache hit resolves and they render the
    /// real mesh instead of the `?` placeholder.
    ///
    /// Only fires on the server (host). Client-imported meshes still need
    /// distribution via a separate mechanism, deferred to a later version
    /// per user's scope choice.
    /// </summary>
    internal static class MeshBroadcastPatch
    {
        private static Type _serverSendType;
        private static MethodInfo _updateGlobalRPGMesh;
        private static PropertyInfo _isServerProp;

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;

            var pickupableMeshType = AccessTools.TypeByName("RpgEngine.PickupableMesh");
            if (pickupableMeshType == null) { log.LogWarning("MeshBroadcastPatch: PickupableMesh not found."); return; }

            var setMesh = AccessTools.Method(pickupableMeshType, "SetRPGMesh",
                new[] { AccessTools.TypeByName("RPGMesh") ?? AccessTools.TypeByName("RpgEngine.RPGMesh") });
            if (setMesh == null) { log.LogWarning("MeshBroadcastPatch: SetRPGMesh(RPGMesh) not found."); return; }

            // Resolve broadcast + isServer targets once.
            _serverSendType = AccessTools.TypeByName("RpgEngine.Networking.ServerNet.ServerSend")
                           ?? AccessTools.TypeByName("RpgEngine.ServerSend");
            if (_serverSendType != null)
            {
                _updateGlobalRPGMesh = AccessTools.Method(_serverSendType, "UpdateGlobalRPGMesh");
            }
            // isServer is declared on the base UnityUtils.Steam.LobbyManager
            // (NOT on RpgEngine.Steam.RpgLobbyManager), so a direct
            // GetProperty on the subclass returns null, public static
            // inherited members need FlattenHierarchy or a lookup on the
            // declaring type. Look at the base type directly.
            var lobbyType = AccessTools.TypeByName("UnityUtils.Steam.LobbyManager")
                         ?? AccessTools.TypeByName("RpgEngine.Steam.RpgLobbyManager");
            _isServerProp = lobbyType?.GetProperty("isServer",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if (_updateGlobalRPGMesh == null || _isServerProp == null)
            {
                log.LogWarning($"MeshBroadcastPatch: missing target, ServerSend.UpdateGlobalRPGMesh={(_updateGlobalRPGMesh != null)}, LobbyManager.isServer={(_isServerProp != null)}. Broadcast disabled.");
                return;
            }

            try
            {
                harmony.Patch(
                    original: setMesh,
                    postfix: new HarmonyMethod(typeof(MeshBroadcastPatch), nameof(SetRPGMesh_Postfix)));
                log.LogInfo("Patched PickupableMesh.SetRPGMesh (postfix, broadcasts mesh bytes to peers when host loads a mesh).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"MeshBroadcastPatch: failed to patch SetRPGMesh: {ex.Message}");
            }

            // Trace peer mesh requests on server side so we know if the
            // pull-response plumbing engages at all when a peer joins.
            var serverReceiveType = AccessTools.TypeByName("RpgEngine.Networking.ServerNet.ServerReceive")
                                 ?? AccessTools.TypeByName("RpgEngine.ServerReceive");
            if (serverReceiveType != null)
            {
                var requestMesh = AccessTools.Method(serverReceiveType, "RequestRPGMesh");
                if (requestMesh != null)
                {
                    try
                    {
                        harmony.Patch(
                            original: requestMesh,
                            prefix: new HarmonyMethod(typeof(MeshBroadcastPatch), nameof(RequestRPGMesh_Prefix)));
                        log.LogInfo("Patched ServerReceive.RequestRPGMesh (prefix, logs peer mesh pull requests).");
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning($"Could not patch ServerReceive.RequestRPGMesh: {ex.Message}");
                    }
                }

                // v3.1.2: REMOVED the LoadBookComplete mass-push. Was
                // pushing every cached mesh (292+ files, possibly gigabytes)
                // to each joining peer in one burst, saturating Steam P2P
                // and starving other messages (vanilla character Move sync
                // broke because MoveAI packets were dropped in the storm).
                // Peers now use the engine's built-in pull-on-demand:
                // spawn PickupableMesh → cache miss → RequestRPGMesh →
                // host replies with just the ONE mesh they need.
            }
        }

        // Runs on the HOST when a peer sends packet 12 asking for a mesh.
        // Signature: public static void RequestRPGMesh(SteamId _fromClient, Packet _packet)
        // We don't touch the args (Packet is a stream; consuming would break original).
        private static void RequestRPGMesh_Prefix()
        {
            Plugin.Log.LogInfo($"[sync-server] Peer requested a mesh, original handler will read id + reply if cached.");
        }

        // DEAD CODE (retained for reference): the LoadBookComplete mass-push.
        // Kept unregistered, removed the harmony.Patch() call above in v3.1.2.
        // Safe to delete entirely once we're sure the pull path is reliable.
        private static void LoadBookComplete_Postfix(object _fromClient)
        {
            try
            {
                if (!(bool)_isServerProp.GetValue(null)) return;   // shouldn't reach here as a non-server, but guard.

                var cacheType = AccessTools.TypeByName("RpgMeshCache")
                             ?? AccessTools.TypeByName("RpgEngine.RpgMeshCache");
                if (cacheType == null) { Plugin.Log.LogWarning("[sync-push] RpgMeshCache type not found."); return; }
                var instField = cacheType.GetField("instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var cacheInst = instField?.GetValue(null);
                if (cacheInst == null) { Plugin.Log.LogWarning("[sync-push] RpgMeshCache.instance is null."); return; }

                // Keys enumerates ALL cached mesh names (Values would materialize each RPGMesh which is heavier).
                var keysProp = cacheType.GetProperty("Keys",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var keys = keysProp?.GetValue(cacheInst) as System.Collections.IEnumerable;
                if (keys == null) { Plugin.Log.LogWarning("[sync-push] RpgMeshCache.Keys not accessible."); return; }

                // Get bytes overload: TryGetValue(string, out byte[]).
                var tryGetBytes = AccessTools.Method(cacheType, "TryGetValue",
                    new[] { typeof(string), typeof(byte[]).MakeByRefType() });
                if (tryGetBytes == null) { Plugin.Log.LogWarning("[sync-push] TryGetValue(string, out byte[]) not found."); return; }

                int pushed = 0, skipped = 0;
                foreach (var keyObj in keys)
                {
                    var key = keyObj as string;
                    if (string.IsNullOrEmpty(key)) continue;
                    var args = new object[] { key, null };
                    var ok = (bool)tryGetBytes.Invoke(cacheInst, args);
                    var bytes = args[1] as byte[];
                    if (!ok || bytes == null || bytes.Length == 0) { skipped++; continue; }

                    // ServerSend.UpdateGlobalRPGMesh(id, bytes, _toClient: peer)
                    _updateGlobalRPGMesh.Invoke(null, new object[] { key, bytes, _fromClient });
                    pushed++;
                }
                Plugin.Log.LogInfo($"[sync-push] Peer joined (LoadBookComplete). Pushed {pushed} cached mesh(es) to them; skipped {skipped} empty.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"LoadBookComplete_Postfix threw: {ex}");
            }
        }

        private static void SetRPGMesh_Postfix(object value)
        {
            try
            {
                if (value == null) return;

                // Only the host broadcasts. Peer-imported meshes → later version.
                var isServer = (bool)_isServerProp.GetValue(null);
                if (!isServer)
                {
                    Plugin.Log.LogInfo("[sync] SetRPGMesh fired but not server, skipping broadcast.");
                    return;
                }

                // Reflect out id + bytes from RPGMesh.
                // CRITICAL: `binaryFile` is the RAW GLB. But packet 110
                // (UpdateGlobalRPGMesh) expects bytes in RPGMeshPacket
                // SERIALIZED format (version + id + textures + mesh +
                // playModeActive + binaryFile length + binaryFile +
                // binaryFileType). Sending raw GLB here would corrupt the
                // peer's cache, causing Deserialize to throw FormatException
                // and forcing a slow re-request. So we extract the id here,
                // but pull the SERIALIZED bytes from the host's RpgMeshCache
                // below (which was populated by the engine's Write(RPGMesh)
                // path at import time and stores the serialized form).
                var rpgMeshType = value.GetType();
                var idField = rpgMeshType.GetField("id") ?? (MemberInfo)rpgMeshType.GetProperty("id");
                string id = (idField is FieldInfo fi ? fi.GetValue(value) : ((PropertyInfo)idField).GetValue(value)) as string;
                if (string.IsNullOrEmpty(id))
                {
                    Plugin.Log.LogInfo($"[sync] Skip (no id on RPGMesh).");
                    return;
                }
                byte[] bytes = null;   // populated from cache after we ensure it's stored

                // 1. Ensure the host's RpgMeshCache has this mesh. The engine
                //    normally writes on FileBrowser import; if we're running
                //    on a re-cached path or the write silently skipped, the
                //    server-side lookup (ServerReceive.RequestRPGMesh) will
                //    miss and any peer's request goes unanswered. Belt-and-
                //    braces: Write() is idempotent.
                var cacheType = AccessTools.TypeByName("RpgMeshCache")
                             ?? AccessTools.TypeByName("RpgEngine.RpgMeshCache");
                object cacheInst = null;
                bool inCache = false;
                if (cacheType != null)
                {
                    var uniqueMonoBase = cacheType.BaseType?.BaseType; // ItemCache<Self,T> → UniqueMono<Self> → ...
                    // easier: search static `instance` on cacheType (inherited).
                    var instField = cacheType.GetField("instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    cacheInst = instField?.GetValue(null);
                    if (cacheInst != null)
                    {
                        // TryGetValue(name, out RPGMesh), probe if it already has it.
                        var tryGet = AccessTools.Method(cacheType, "TryGetValue",
                            new[] { typeof(string), rpgMeshType.MakeByRefType() });
                        if (tryGet != null)
                        {
                            var probeArgs = new object[] { id, null };
                            inCache = (bool)tryGet.Invoke(cacheInst, probeArgs);
                        }

                        if (!inCache)
                        {
                            // Write(RPGMesh data, bool persist=true) is inherited from ItemCache.
                            // Also triggers Serialize which produces the RPGMeshPacket-format
                            // bytes we need for the broadcast.
                            var write = AccessTools.Method(cacheType, "Write",
                                new[] { rpgMeshType, typeof(bool) });
                            if (write != null)
                            {
                                write.Invoke(cacheInst, new object[] { value, true });
                                Plugin.Log.LogInfo($"[sync] Force-wrote '{id}' to RpgMeshCache (was missing).");
                            }
                        }

                        // Now pull the SERIALIZED bytes out via the byte[] overload.
                        // These are what packet 110's receiver expects to Deserialize.
                        var tryGetBytes = AccessTools.Method(cacheType, "TryGetValue",
                            new[] { typeof(string), typeof(byte[]).MakeByRefType() });
                        if (tryGetBytes != null)
                        {
                            var bytesArgs = new object[] { id, null };
                            if ((bool)tryGetBytes.Invoke(cacheInst, bytesArgs))
                            {
                                bytes = bytesArgs[1] as byte[];
                            }
                        }
                    }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    Plugin.Log.LogWarning($"[sync] Could not retrieve serialized bytes for '{id}' from RpgMeshCache. Broadcast skipped.");
                    return;
                }

                // 2. Check HasClients before broadcasting, UpdateGlobalRPGMesh
                //    is a no-op if there are no peers connected yet, and we
                //    want to know that's why nothing happened.
                // HasClients is PRIVATE static, must include NonPublic flag.
                // (Previous diagnostic missed this and misleadingly reported False.)
                var hasClientsProp = _serverSendType.GetProperty("HasClients",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var hasClients = hasClientsProp != null
                    ? (bool)hasClientsProp.GetValue(null)
                    : (bool?)null;

                // 3. Fire the broadcast (fire-and-forget).
                var args = new object[] { id, bytes, null };
                _updateGlobalRPGMesh.Invoke(null, args);
                Plugin.Log.LogInfo($"[sync] Broadcast '{id}' ({bytes.Length:N0} bytes). isServer={isServer}, hasClients={(hasClients?.ToString() ?? "<unknown>")}, inCacheBefore={inCache}. Peers who join later will pull-request from the cache.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"MeshBroadcastPatch postfix threw (mesh not synced): {ex}");
            }
        }
    }
}
