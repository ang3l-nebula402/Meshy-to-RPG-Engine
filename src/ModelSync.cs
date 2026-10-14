using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// v3.1.34: keep imported models the same on every PC.
    ///
    /// Heights: the host's Model Heights values (F8) are the one source of
    /// truth. Each placed model carries its height in prop.genericData (the
    /// engine's per-prop extra data, saved with the map and sent to players by
    /// NetworkSend.UpdateGenericPropData). The engine ignores generic data on
    /// meshes, so a postfix on Pickupable.UpdateGenericPropData stores and
    /// applies it on the players' side.
    ///
    /// Transforms: a player drops any location/scale packet for a prop it
    /// hasn't spawned yet, and drags go out over UDP, so a quick place-and-scale
    /// on a model a player is still downloading could leave them out of date.
    /// The host watches every imported model and, once it stops changing,
    /// resends its final position/rotation/scale (and height) reliably, a few
    /// extra times for fresh placements to outlast a slow download.
    /// </summary>
    internal static class ModelSync
    {
        private const string HeightKey = "am_heightAdjust";
        // v3.1.35: the host's measured feet-on-ground correction, so players
        // use the same number instead of measuring their own.
        private const string GroundKey = "am_groundAuto";

        private static PropertyInfo _isServer;
        private static PropertyInfo _propProperty;
        private static PropertyInfo _guidProperty;
        private static MethodInfo _sendLocation;
        private static MethodInfo _sendGenericData;
        private static Type _pickupableMeshType;
        private static bool _resolved;

        internal static void ApplyAll(Harmony harmony)
        {
            var log = Plugin.Log;
            Resolve();
            var pickupableType = AccessTools.TypeByName("RpgEngine.Pickupable");
            var target = pickupableType == null ? null : AccessTools.Method(pickupableType, "UpdateGenericPropData");
            if (target == null)
            {
                log.LogWarning("ModelSync: Pickupable.UpdateGenericPropData not found, heights won't sync to players.");
                return;
            }
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(ModelSync), nameof(UpdateGenericPropData_Postfix)));
                log.LogInfo("Patched Pickupable.UpdateGenericPropData (postfix, model heights from the host).");
            }
            catch (Exception ex)
            {
                log.LogError($"ModelSync: failed to patch UpdateGenericPropData: {ex.Message}");
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // isServer is declared on the base UnityUtils.Steam.LobbyManager.
            var lobbyType = AccessTools.TypeByName("UnityUtils.Steam.LobbyManager")
                         ?? AccessTools.TypeByName("RpgEngine.Steam.RpgLobbyManager");
            _isServer = lobbyType?.GetProperty("isServer", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            var pickupableType = AccessTools.TypeByName("RpgEngine.Pickupable");
            _propProperty = pickupableType?.GetProperty("prop", instance);
            _guidProperty = pickupableType?.GetProperty("guid", instance);
            _pickupableMeshType = AccessTools.TypeByName("RpgEngine.PickupableMesh");

            var netSend = AccessTools.TypeByName("RpgEngine.NetworkSend");
            _sendLocation = netSend == null ? null : AccessTools.Method(netSend, "UpdatePropLocation", new[] { typeof(string), typeof(bool) });
            _sendGenericData = netSend == null ? null : AccessTools.Method(netSend, "UpdateGenericPropData", new[] { typeof(string) });

            if (_isServer == null || _propProperty == null || _guidProperty == null || _sendLocation == null || _sendGenericData == null)
                Plugin.Log.LogWarning($"ModelSync: missing engine members (isServer={_isServer != null}, prop={_propProperty != null}, guid={_guidProperty != null}, location={_sendLocation != null}, genericData={_sendGenericData != null}). Sync partly disabled.");
        }

        internal static bool IsServer
        {
            get
            {
                Resolve();
                try { return _isServer == null || (bool)_isServer.GetValue(null); }
                catch { return true; }
            }
        }

        /// <summary>
        /// The height a freshly imported model should use. On the host that's
        /// the host's own Model Heights value, written onto the prop and sent to
        /// players. On a player it's whatever the host sent, falling back to the
        /// local value until it arrives.
        /// </summary>
        internal static float ResolveHeight(Component pickupable, string meshId)
        {
            float configured = Plugin.GetHeightAdjust(meshId);
            if (pickupable == null) return configured;

            if (IsServer)
            {
                WriteHeight(pickupable, configured);
                SendGenericData(pickupable);
                return configured;
            }

            if (TryReadHeight(GenericData(pickupable), out float fromHost))
            {
                Plugin.Log.LogInfo($"ModelSync: '{meshId}' height from the host {fromHost:+0.00;-0.00;0}m.");
                return fromHost;
            }
            return configured;
        }

        /// <summary>Host side: a Model Heights change for one placed copy.</summary>
        internal static void PushHeight(GroundOffset ground, float metres)
        {
            if (ground == null || ground.Pickupable == null) return;
            WriteHeight(ground.Pickupable, metres);
            SendGenericData(ground.Pickupable);
            SyncGuard.MarkChanged(ground);
        }

        /// <summary>Host side: share a settled ground correction with players.</summary>
        internal static void PushAuto(GroundOffset ground, float local)
        {
            if (ground == null || ground.Pickupable == null) return;
            WriteFloat(ground.Pickupable, GroundKey, local);
            SendGenericData(ground.Pickupable);
            SyncGuard.MarkChanged(ground);
        }

        /// <summary>Player side: the host's ground correction, if it has arrived.</summary>
        internal static bool TryGetHostAuto(Component pickupable, out float local)
        {
            local = 0f;
            return pickupable != null && TryReadFloat(GenericData(pickupable), GroundKey, out local);
        }

        /// <summary>Host side: reliable resend of a model's transform and height.</summary>
        internal static void Resend(GroundOffset ground)
        {
            if (ground == null || ground.Pickupable == null) return;
            var guid = Guid(ground.Pickupable);
            if (string.IsNullOrEmpty(guid) || _sendLocation == null) return;
            _sendLocation.Invoke(null, new object[] { guid, true });
            SendGenericData(ground.Pickupable);
        }

        /// <summary>
        /// The engine's base UpdateGenericPropData is empty, so a mesh drops the
        /// data it's sent. Keep it on the prop (so a later import finds it) and
        /// apply the height to every live copy of this pickupable.
        /// </summary>
        private static void UpdateGenericPropData_Postfix(object __instance, object data)
        {
            try
            {
                if (data == null || _pickupableMeshType == null || !_pickupableMeshType.IsInstanceOfType(__instance)) return;
                var prop = _propProperty?.GetValue(__instance);
                var field = prop?.GetType().GetField("genericData");
                field?.SetValue(prop, data);

                var ground = (__instance as Component)?.GetComponent<GroundOffset>();
                if (ground == null) return;

                if (TryReadHeight(data, out float metres))
                {
                    if (Mathf.Abs(ground.ManualLocal - metres) > 0.0001f)
                        Plugin.Log.LogInfo($"ModelSync: '{ground.MeshId}' height from the host {metres:+0.00;-0.00;0}m.");
                    ground.SetManual(metres);
                }

                if (!IsServer && TryReadFloat(data, GroundKey, out float auto))
                {
                    if (Mathf.Abs(ground.AutoLocal - auto) > 0.0001f)
                        Plugin.Log.LogInfo($"ModelSync: '{ground.MeshId}' ground correction from the host {auto:+0.000;-0.000}m.");
                    ground.SetAutoFromHost(auto);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ModelSync: could not apply host data: {ex.Message}");
            }
        }

        private static object GenericData(Component pickupable)
        {
            var prop = _propProperty?.GetValue(pickupable);
            return prop?.GetType().GetField("genericData")?.GetValue(prop);
        }

        private static void WriteHeight(Component pickupable, float metres) => WriteFloat(pickupable, HeightKey, metres);

        private static bool TryReadHeight(object data, out float metres) => TryReadFloat(data, HeightKey, out metres);

        private static void WriteFloat(Component pickupable, string key, float value)
        {
            var data = GenericData(pickupable);
            data?.GetType().GetMethod("Set", new[] { typeof(string), typeof(float) })?.Invoke(data, new object[] { key, value });
        }

        private static bool TryReadFloat(object data, string key, out float value)
        {
            value = 0f;
            if (data == null) return false;
            var getFloat = data.GetType().GetMethod("GetFloat", new[] { typeof(string), typeof(float) });
            if (getFloat == null) return false;
            value = (float)getFloat.Invoke(data, new object[] { key, float.NaN });
            return !float.IsNaN(value);
        }

        private static void SendGenericData(Component pickupable)
        {
            var guid = Guid(pickupable);
            if (string.IsNullOrEmpty(guid) || _sendGenericData == null) return;
            try { _sendGenericData.Invoke(null, new object[] { guid }); }
            catch (Exception ex) { Plugin.Log.LogWarning($"ModelSync: could not send height for {guid}: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        private static string Guid(Component pickupable)
        {
            try { return _guidProperty?.GetValue(pickupable) as string; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Host-side watcher for ModelSync: resends an imported model's final
    /// transform once it has stopped changing.
    /// </summary>
    internal class SyncGuard : MonoBehaviour
    {
        private const float CheckIntervalSeconds = 0.25f;
        // After an edit settles, one resend is enough. A fresh placement gets
        // two more, to land after a slow model download on the players' side.
        private static readonly float[] EditResends = { 1.5f };
        private static readonly float[] NewModelResends = { 1.5f, 8f, 20f, 45f };

        private class Tracked
        {
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public float ChangedAt;
            public float[] Schedule;
            public int Sent;
        }

        private static SyncGuard _instance;
        private readonly Dictionary<GroundOffset, Tracked> _tracked = new Dictionary<GroundOffset, Tracked>();
        private float _nextCheck;

        internal static void Create()
        {
            var go = new GameObject("AnimatedMesh_SyncGuard");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SyncGuard>();
        }

        internal static void MarkChanged(GroundOffset ground)
        {
            if (_instance == null || ground == null) return;
            if (_instance._tracked.TryGetValue(ground, out var state))
            {
                bool stillFresh = state.Schedule == NewModelResends && state.Sent < state.Schedule.Length;
                state.ChangedAt = Time.unscaledTime;
                state.Sent = 0;
                state.Schedule = stillFresh ? NewModelResends : EditResends;
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + CheckIntervalSeconds;
            if (!ModelSync.IsServer) { _tracked.Clear(); return; }

            float now = Time.unscaledTime;
            var live = GroundOffset.All.Where(g => g != null).ToList();

            // Several models appearing in one tick is a map loading, and the
            // players load the same map, so only a lone new model counts as a
            // fresh placement worth the longer resend schedule.
            var unseen = live.Where(g => !_tracked.ContainsKey(g)).ToList();
            bool mapLoad = unseen.Count >= 3;
            foreach (var ground in unseen)
            {
                var t = ground.transform;
                _tracked[ground] = new Tracked
                {
                    Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale,
                    ChangedAt = now, Schedule = NewModelResends,
                    Sent = mapLoad ? NewModelResends.Length : 0,
                };
            }

            foreach (var ground in live)
            {
                if (unseen.Contains(ground)) continue;
                var state = _tracked[ground];
                var t = ground.transform;

                if ((t.localPosition - state.Position).sqrMagnitude > 1e-6f ||
                    (t.localScale - state.Scale).sqrMagnitude > 1e-8f ||
                    Quaternion.Angle(t.localRotation, state.Rotation) > 0.1f)
                {
                    // A fresh placement that's still being moved or scaled keeps
                    // the long schedule (restarted from now); anything else is an edit.
                    bool stillFresh = state.Schedule == NewModelResends && state.Sent < state.Schedule.Length;
                    state.Position = t.localPosition;
                    state.Rotation = t.localRotation;
                    state.Scale = t.localScale;
                    state.ChangedAt = now;
                    state.Sent = 0;
                    state.Schedule = stillFresh ? NewModelResends : EditResends;
                    continue;
                }

                if (state.Sent >= state.Schedule.Length || now - state.ChangedAt < state.Schedule[state.Sent]) continue;
                try
                {
                    ModelSync.Resend(ground);
                    state.Sent++;
                    Plugin.Log.LogInfo($"[sync-guard] resent '{ground.MeshId}' position, rotation, scale and height to players ({state.Sent} of {state.Schedule.Length}).");
                }
                catch (Exception ex)
                {
                    state.Sent = state.Schedule.Length;
                    Plugin.Log.LogWarning($"[sync-guard] resend failed for '{ground.MeshId}': {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            foreach (var gone in _tracked.Keys.Where(g => g == null).ToList())
                _tracked.Remove(gone);
        }
    }
}
