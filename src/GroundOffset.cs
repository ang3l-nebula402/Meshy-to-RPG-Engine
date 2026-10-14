using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// v3.1.28: keeps an imported model's lowest point (its feet) on the
    /// pickupable's origin, so it stands on the ground wherever it lands.
    ///
    /// The engine drops a dragged prop with its origin exactly on the ground
    /// hit point (DragDropState.Drop), and NavMesh walking does the same. Meshy
    /// models rarely have their feet at that origin: humanoid retargeting puts
    /// the hips wherever our generated Avatar says, and static exports often
    /// have their origin mid-body. So the model sank (or floated) after every
    /// drag, and a GM nudge in edit mode only lasted until the next drag.
    ///
    /// Once the model has settled into its idle pose we bake the mesh, find
    /// the lowest point, and hold a vertical offset that puts it on the origin.
    /// Humanoids get the offset on the hips bone every LateUpdate (the Animator
    /// rewrites the hips each frame, so moving a parent doesn't stick); static
    /// and legacy-animated meshes get it on the mesh child once. A per-model
    /// [HeightAdjust] config entry adds a manual nudge on top.
    ///
    /// Offsets are kept in root-local units so they scale with the prop.
    /// </summary>
    public class GroundOffset : MonoBehaviour
    {
        // IdleGuard forces Idle for the first 30 frames; measure after that.
        private const int AnimatedSettleFrames = 40;
        private const int StaticSettleFrames = 2;
        // Larger corrections mean broken bounds, not a real foot offset.
        private const float MaxAutoOffsetMetres = 2f;

        private static Type _pickupableType;

        /// <summary>Every live instance, for the in-game HeightPanel (v3.1.29).</summary>
        internal static readonly List<GroundOffset> All = new List<GroundOffset>();

        internal string MeshId => _meshId;
        internal float ManualLocal => _manualLocal;
        internal float AutoLocal => Plugin.AutoGroundFeet ? _autoLocal : 0f;

        /// <summary>Live manual nudge from the HeightPanel; ApplyOffset picks it up next frame.</summary>
        internal void SetManual(float local) => _manualLocal = local;

        private void Awake() => All.Add(this);
        private void OnDestroy() => All.Remove(this);

        private string _meshId;
        private Transform _hips;
        private Transform _meshChild;
        private float _autoLocal;
        private float _manualLocal;
        private float _appliedChildLocal;
        private Vector3 _lastHipsLocal;
        private bool _hipsOffsetLive;
        private int _framesUntilMeasure = -1;

        /// <summary>
        /// Add (or refresh) the component on a freshly imported custom mesh.
        /// Safe to call repeatedly, the engine's import fires 2–3 times.
        /// </summary>
        internal static void Attach(GameObject root, object pickupable)
        {
            if (root == null) return;
            var ground = root.GetComponent<GroundOffset>() ?? root.AddComponent<GroundOffset>();
            ground.Init(ReadMeshId(pickupable));
        }

        private void Init(string meshId)
        {
            _meshId = meshId;
            _manualLocal = Plugin.GetHeightAdjust(meshId);

            // A re-import can switch modes (mesh child → hips once the humanoid
            // Animator lands) or swap the mesh. Take back any shift already on
            // the mesh child; ApplyOffset re-applies the full total next frame.
            if (_meshChild != null && Mathf.Abs(_appliedChildLocal) > 0.0001f)
                _meshChild.localPosition -= new Vector3(0f, _appliedChildLocal, 0f);
            _appliedChildLocal = 0f;

            var animator = GetComponent<Animator>();
            _hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            _hipsOffsetLive = false;
            _meshChild = FindMeshChild();

            // Per-frame skinned bounds, used to cross-check the baked measurement.
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;

            _framesUntilMeasure = Plugin.AutoGroundFeet ? (IsAnimated ? AnimatedSettleFrames : StaticSettleFrames) : -1;
            enabled = true;
        }

        private void LateUpdate()
        {
            ApplyOffset();

            if (_framesUntilMeasure < 0) return;
            if (_framesUntilMeasure-- > 0) return;
            Measure();
        }

        private void ApplyOffset()
        {
            float total = (Plugin.AutoGroundFeet ? _autoLocal : 0f) + _manualLocal;

            if (_hips != null)
            {
                // The Animator rewrites the hips every frame. If it didn't this
                // frame (disabled, paused), they still carry last frame's
                // offset, so adding it again would ratchet the model upward.
                if (_hipsOffsetLive && _hips.localPosition == _lastHipsLocal) return;
                if (Mathf.Abs(total) > 0.0001f)
                    _hips.position += transform.TransformVector(new Vector3(0f, total, 0f));
                _lastHipsLocal = _hips.localPosition;
                _hipsOffsetLive = true;
                return;
            }

            if (_meshChild == null) return;
            float delta = total - _appliedChildLocal;
            if (Mathf.Abs(delta) < 0.0001f) return;
            _meshChild.localPosition += new Vector3(0f, delta, 0f);
            _appliedChildLocal = total;
        }

        private void Measure()
        {
            var log = Plugin.Log;
            if (!TryGetLowestWorldY(out float lowestY))
            {
                log.LogInfo($"GroundOffset: '{Label}' has no mesh to measure yet, leaving height as is.");
                return;
            }

            float scale = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));
            // How far the lowest point currently sits below the origin, plus the
            // manual nudge that's already in the applied total, gives the
            // automatic correction on its own.
            float residualLocal = (transform.position.y - lowestY) / scale;
            float candidate = _autoLocal + residualLocal + _manualLocal;
            if (Mathf.Abs(candidate * scale) > MaxAutoOffsetMetres)
            {
                log.LogWarning($"GroundOffset: '{Label}' measured a {candidate * scale:F2}m correction, too large to be a foot offset. Skipping, set it by hand in the Model Heights panel (F8).");
                return;
            }

            _autoLocal = candidate;
            log.LogInfo($"GroundOffset: '{Label}' feet held on the ground, auto {_autoLocal * scale:+0.000;-0.000}m, manual {_manualLocal * scale:+0.000;-0.000}m ({(_hips != null ? "hips" : "mesh child")}).");
        }

        private bool TryGetLowestWorldY(out float lowestY)
        {
            lowestY = float.MaxValue;
            bool found = false;
            Mesh baked = null;
            try
            {
                if (IsAnimated)
                {
                    // Only the character's own skin decides where the feet are:
                    // no particles, labels, or props held in its hands.
                    foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    {
                        if (!smr.enabled || smr.sharedMesh == null || !BelongsToThisPickupable(smr.transform)) continue;
                        if (baked == null) baked = new Mesh();

                        // useScale false: vertices come back at world scale,
                        // relative to the SMR's position and rotation. With true
                        // they're in the SMR's local units, which is 100x off on
                        // rigs under a 0.01-scaled armature (Jade's cm skeleton).
                        smr.BakeMesh(baked, false);
                        var t = smr.transform;
                        float bakedLowest = float.MaxValue;
                        foreach (var v in baked.vertices)
                        {
                            float y = t.position.y + (t.rotation * v).y;
                            if (y < bakedLowest) bakedLowest = y;
                        }

                        // Cross-check against Unity's own per-frame skinned bounds
                        // (updateWhenOffscreen). They're slightly loose, so only
                        // trust them over the bake when the bake is clearly off.
                        float boundsLowest = smr.bounds.min.y;
                        bool useBounds = smr.updateWhenOffscreen && Mathf.Abs(bakedLowest - boundsLowest) > 0.25f;
                        Plugin.Log.LogInfo($"GroundOffset: '{Label}' lowest point, baked {bakedLowest:F3} / bounds {boundsLowest:F3} / origin {transform.position.y:F3}{(useBounds ? ", using bounds" : "")}.");
                        lowestY = Mathf.Min(lowestY, useBounds ? boundsLowest : bakedLowest);
                        found = true;
                    }
                }
                else
                {
                    foreach (var mr in GetComponentsInChildren<MeshRenderer>(false))
                    {
                        // The pickupable's and GFX's own renderers hold engine
                        // placeholder meshes, the imported model sits below GFX.
                        if (!mr.enabled || mr.transform == transform || mr.transform == _meshChild) continue;
                        if (!BelongsToThisPickupable(mr.transform)) continue;
                        var filter = mr.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null) continue;
                        lowestY = Mathf.Min(lowestY, mr.bounds.min.y);
                        found = true;
                    }
                }
            }
            finally
            {
                if (baked != null) Destroy(baked);
            }
            return found;
        }

        private bool IsAnimated => _hips != null || GetComponentInChildren<SkinnedMeshRenderer>(true) != null;

        private bool BelongsToThisPickupable(Transform t)
        {
            if (_pickupableType == null)
                _pickupableType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Pickupable");
            if (_pickupableType == null) return true;
            var owner = t.GetComponentInParent(_pickupableType);
            return owner == null || owner.gameObject == gameObject;
        }

        /// <summary>
        /// The direct child of the pickupable that holds the imported mesh
        /// (the engine's GFX transform), same lookup AlignMeshToGround uses.
        /// </summary>
        private Transform FindMeshChild()
        {
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.transform == transform) continue;
                var t = renderer.transform;
                while (t.parent != null && t.parent != transform) t = t.parent;
                if (t.parent == transform) return t;
            }
            return null;
        }

        private string Label => string.IsNullOrEmpty(_meshId) ? gameObject.name : _meshId;

        /// <summary>prop.importData.meshId, read by reflection (no RpgEngine reference).</summary>
        private static string ReadMeshId(object pickupable)
        {
            try
            {
                var prop = GetMember(pickupable, "prop");
                var importData = GetMember(prop, "importData");
                return GetMember(importData, "meshId") as string;
            }
            catch
            {
                return null;
            }
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = obj.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(obj);
            return type.GetField(name, flags)?.GetValue(obj);
        }
    }
}
