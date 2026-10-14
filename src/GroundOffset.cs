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
        // v3.1.35: measure again until two readings agree, so one taken at a
        // bad moment can't stick.
        private const int RemeasureFrames = 20;
        private const int MaxMeasureAttempts = 6;
        private const float SettledMetres = 0.005f;

        private static Type _pickupableType;

        /// <summary>Every live instance, for the in-game HeightPanel (v3.1.29).</summary>
        internal static readonly List<GroundOffset> All = new List<GroundOffset>();

        internal string MeshId => _meshId;
        internal float ManualLocal => _manualLocal;
        internal float AutoLocal => Plugin.AutoGroundFeet || _autoFromHost ? _autoLocal : 0f;

        /// <summary>Live manual nudge from the HeightPanel; ApplyOffset picks it up next frame.</summary>
        internal void SetManual(float local) => _manualLocal = local;

        private void Awake() => All.Add(this);
        private void OnDestroy() => All.Remove(this);

        private string _meshId;
        private Component _pickupable;

        /// <summary>The engine's PickupableMesh this offset belongs to (v3.1.34, for ModelSync).</summary>
        internal Component Pickupable => _pickupable;
        private Transform _hips;
        private Transform _meshChild;
        private float _autoLocal;
        private float _manualLocal;
        private float _appliedChildLocal;
        private Vector3 _lastHipsLocal;
        private bool _hipsOffsetLive;
        private int _framesUntilMeasure = -1;
        private int _measureAttempts;
        private bool _autoFromHost;
        private Vector3 _lastRootPosition;
        private BoxCollider _clickBox;
        private Vector3 _clickBoxBaseCenter;
        private float _clickBoxAppliedLocal;
        private int _movingFrames;
        private const int MaxWaitForStillFrames = 300;

        /// <summary>
        /// v3.1.35: the host's measured correction, so every PC grounds the
        /// model identically instead of each measuring its own.
        /// </summary>
        internal void SetAutoFromHost(float local)
        {
            _autoLocal = local;
            _autoFromHost = true;
            _framesUntilMeasure = -1;
        }

        /// <summary>
        /// Add (or refresh) the component on a freshly imported custom mesh.
        /// Safe to call repeatedly, the engine's import fires 2–3 times.
        /// </summary>
        internal static void Attach(GameObject root, object pickupable)
        {
            if (root == null) return;
            var ground = root.GetComponent<GroundOffset>() ?? root.AddComponent<GroundOffset>();
            ground._pickupable = pickupable as Component;
            var meshId = ReadMeshId(pickupable);
            // v3.1.34: the host's height wins on every PC (see ModelSync).
            ground.Init(meshId, ModelSync.ResolveHeight(ground._pickupable, meshId));
        }

        private void Init(string meshId, float manual)
        {
            _meshId = meshId;
            _manualLocal = manual;

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

            // Keep skinned bounds live (fallback measurement, and culling).
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;

            // The click box the mod adds for skeleton models moves with the
            // correction, so it stays on the visible body (v3.1.35).
            var box = GetComponent<BoxCollider>();
            if (box != _clickBox)
            {
                _clickBox = box;
                _clickBoxAppliedLocal = 0f;
                if (box != null) _clickBoxBaseCenter = box.center;
            }

            _measureAttempts = 0;
            if (!ModelSync.IsServer && ModelSync.TryGetHostAuto(_pickupable, out float hostAuto))
            {
                SetAutoFromHost(hostAuto);
                Plugin.Log.LogInfo($"GroundOffset: '{Label}' using the host's ground correction {hostAuto:+0.000;-0.000}m.");
            }
            else
            {
                _autoFromHost = false;
                _framesUntilMeasure = Plugin.AutoGroundFeet ? (IsAnimated ? AnimatedSettleFrames : StaticSettleFrames) : -1;
            }
            _lastRootPosition = transform.position;
            enabled = true;
        }

        private void LateUpdate()
        {
            ApplyOffset();

            if (_framesUntilMeasure < 0) return;

            // Only measure a model that's standing still. The engine slides
            // props into place (PickupableLazyPosition) and drags move them
            // every frame.
            var position = transform.position;
            bool moving = (position - _lastRootPosition).sqrMagnitude > 1e-8f;
            _lastRootPosition = position;
            // A NavMesh agent can keep nudging the anchor by a hair; after
            // ~5 s of that, measure anyway rather than never.
            _movingFrames = moving ? _movingFrames + 1 : 0;
            if (moving && _movingFrames < MaxWaitForStillFrames)
            {
                if (_framesUntilMeasure < 5) _framesUntilMeasure = 5;
                return;
            }

            if (_framesUntilMeasure-- > 0) return;
            Measure();
        }

        private void ApplyOffset()
        {
            float total = (Plugin.AutoGroundFeet || _autoFromHost ? _autoLocal : 0f) + _manualLocal;

            if (_clickBox != null && Mathf.Abs(total - _clickBoxAppliedLocal) > 0.0001f)
            {
                _clickBox.center = _clickBoxBaseCenter + new Vector3(0f, total, 0f);
                _clickBoxAppliedLocal = total;
            }

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
                _measureAttempts++;
                _framesUntilMeasure = _measureAttempts < MaxMeasureAttempts ? RemeasureFrames : -1;
                log.LogInfo($"GroundOffset: '{Label}' has no mesh to measure yet{(_framesUntilMeasure > 0 ? ", trying again shortly" : ", leaving height as is")}.");
                return;
            }

            float scale = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));
            // How far the lowest point currently sits below the origin, plus the
            // manual nudge that's already in the applied total, gives the
            // automatic correction on its own.
            float residualLocal = (transform.position.y - lowestY) / scale;
            float candidate = _autoLocal + residualLocal + _manualLocal;
            _measureAttempts++;
            bool canRetry = _measureAttempts < MaxMeasureAttempts;

            if (Mathf.Abs(candidate * scale) > MaxAutoOffsetMetres)
            {
                log.LogWarning($"GroundOffset: '{Label}' measured a {candidate * scale:F2}m correction (attempt {_measureAttempts}), too large to be a foot offset. {(canRetry ? "Measuring again." : "Giving up, set it by hand in the Model Heights panel (F8).")}");
                _framesUntilMeasure = canRetry ? RemeasureFrames : -1;
                return;
            }

            float change = (candidate - _autoLocal) * scale;
            _autoLocal = candidate;
            if (Mathf.Abs(change) > SettledMetres && canRetry)
            {
                // Apply it, then check again: the next reading should agree.
                _framesUntilMeasure = RemeasureFrames;
                return;
            }

            _framesUntilMeasure = -1;
            log.LogInfo($"GroundOffset: '{Label}' feet held on the ground, auto {_autoLocal * scale:+0.000;-0.000}m, manual {_manualLocal * scale:+0.000;-0.000}m ({(_hips != null ? "hips" : "mesh child")}, {_measureAttempts} reading(s)).");

            // The host's reading is the one every player uses.
            if (ModelSync.IsServer) ModelSync.PushAuto(this, _autoLocal);
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

                        // v3.1.35: the bake is exact; Unity's skinned bounds came
                        // out up to a metre loose and are only a fallback now.
                        bool fromBounds = baked.vertexCount == 0;
                        if (fromBounds) bakedLowest = smr.bounds.min.y;
                        Plugin.Log.LogInfo($"GroundOffset: '{Label}' lowest point {bakedLowest:F3}{(fromBounds ? " (bounds)" : "")}, origin {transform.position.y:F3}.");
                        lowestY = Mathf.Min(lowestY, bakedLowest);
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
        /// (the engine's GFX transform).
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
