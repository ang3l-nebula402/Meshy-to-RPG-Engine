using System.Collections.Generic;
using System.Linq;
using RpgEngineAnimatedMeshMod.Patches;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// Given a GameObject the engine's Custom Mesh importer just spawned, wire
    /// up its animation clips so they can be triggered at runtime by name.
    ///
    /// We use Unity's LEGACY Animation component (not Mecanim's Animator)
    /// because AnimatorController construction is Editor-only, it doesn't
    /// exist in shipped Unity builds. Legacy Animation:
    ///   • works out of the box at runtime,
    ///   • supports Play("clipName") + CrossFade("clipName") by name,
    ///   • carries the clip's own AnimationCurves against the skeleton,
    ///   • integrates cleanly with SkinnedMeshRenderer + Transform hierarchy.
    ///
    /// After we set it up, we attach a small PickupableAnimationExposer
    /// component that broadcasts the available clip names, a follow-up patch
    /// can teach the engine's PickupableAnimations menu to read from this.
    /// </summary>
    internal static class AnimationBuilder
    {

        internal static void InstallAnimator(GameObject root)
        {
            var log = Plugin.Log;
            if (root == null) return;

            // v2.0 preferred path: build a Humanoid Avatar + attach an Animator
            // with the engine's C_Humanoid controller. Meshy's Mixamo-style
            // rig maps cleanly to Unity Humanoid, and Unity retargets any
            // humanoid clip onto our mesh, so the imported character
            // inherits the full native animation library (135 clips, 97 params).
            if (TryInstallHumanoidAnimator(root)) return;

            // Fallback: legacy Animation with whatever clips came in the GLB.
            // Used when Humanoid Avatar build fails (missing bones) or the
            // native C_Humanoid controller isn't cached yet.
            var clips = CollectClips(root);

            if (clips.Count == 0)
            {
                var captured = GltfImporterCapturePatch.ConsumeRecent();
                if (captured != null && captured.Length > 0)
                {
                    clips.AddRange(captured);
                    log.LogInfo($"'{root.name}': attached {captured.Length} clip(s) from GLTFUtility capture buffer.");
                }
            }

            if (clips.Count == 0)
            {
                log.LogInfo($"'{root.name}' has a skeleton but no animation clips were found. Nothing to wire.");
                return;
            }

            log.LogInfo($"'{root.name}': falling back to LEGACY animation path with {clips.Count} Meshy clip(s).");

            log.LogInfo($"'{root.name}': discovered {clips.Count} animation clip(s): {string.Join(", ", clips.Select(c => c.name))}");

            // Log clip metadata BEFORE we mutate anything, this tells us
            // whether the clips are humanoid (unplayable via legacy Animation)
            // or generic (playable), and whether they have any curve data.
            foreach (var clip in clips)
            {
                log.LogInfo($"  clip '{clip.name}': length={clip.length:F2}s, frameRate={clip.frameRate}, empty={clip.empty}, isLooping={clip.isLooping}, legacy={clip.legacy}, humanMotion={clip.humanMotion}, wrapMode={clip.wrapMode}");
            }

            // Force clips to legacy + looping. GLTFUtility usually imports
            // as Legacy already thanks to our useLegacyClips=true patch.
            foreach (var clip in clips)
            {
                clip.legacy = true;
                if (clip.wrapMode == WrapMode.Default) clip.wrapMode = WrapMode.Loop;
            }

            // Find the correct GameObject to host the Animation. Legacy Animation
            // resolves clip paths like "Armature/Hips/Spine" starting from its
            // own transform. GLTFUtility built the clips relative to the GLB's
            // imported root, which is typically the parent of the SkinnedMesh's
            // root bone (an "Armature" node). We walk up from rootBone to find
            // that node, attaching Animation there makes paths resolve.
            var smr = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault();
            var animHost = ResolveAnimationHost(root, smr) ?? root;
            log.LogInfo($"Animation host: '{animHost.name}' " +
                        (animHost == root ? "(root fallback)" : "(walked up from rootBone)"));

            // Log the hierarchy once so we can verify structure if something's off.
            LogHierarchy(root, 0);

            // Attach a legacy Animation component to the chosen host.
            // Any existing one is replaced so we can guarantee clean state.
            var legacyExisting = animHost.GetComponent<Animation>();
            if (legacyExisting != null) Object.Destroy(legacyExisting);
            var anim = animHost.AddComponent<Animation>();
            anim.playAutomatically = false;
            anim.cullingType = AnimationCullingType.AlwaysAnimate;

            foreach (var clip in clips)
            {
                anim.AddClip(clip, clip.name);
            }
            // Set the first clip as default so Play() with no arg works.
            anim.clip = clips[0];

            // Pick a sensible default: any clip whose name CONTAINS "Idle"
            // (matches Idle, Idle_5, Idle_Loop, T_Idle, etc.), falling back
            // to the shortest name (most likely a base pose), then first.
            var idle = clips.FirstOrDefault(c => c.name.IndexOf("Idle", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? clips.OrderBy(c => c.name.Length).FirstOrDefault()
                    ?? clips.First();
            var played = anim.Play(idle.name);
            log.LogInfo($"anim.Play('{idle.name}') → {played}");
            if (!played)
            {
                log.LogWarning($"Play returned false. Trying CrossFade + Sample as fallback.");
                anim.CrossFade(idle.name, 0f);
                anim.Sample();
            }

            // Brute-force: try SampleAnimation at every descendant of root.
            // Whichever one moves Hips is the correct host for these clips.
            // Then rehost the Animation component there.
            var hips = FindBone(root, "Hips");
            GameObject winningHost = null;
            if (hips != null)
            {
                var baseline = hips.localRotation;
                var candidates = root.GetComponentsInChildren<Transform>(true).ToList();
                log.LogInfo($"Path-resolution brute force: probing {candidates.Count} candidate hosts with clip '{idle.name}' at t=0.5s");

                foreach (var t in candidates)
                {
                    // Restore baseline before each probe so we're comparing clean.
                    hips.localRotation = baseline;
                    idle.SampleAnimation(t.gameObject, 0.5f);
                    var after = hips.localRotation;
                    if (Quaternion.Angle(baseline, after) > 0.01f)
                    {
                        log.LogInfo($"  ✓ '{t.name}' at path '{GetPath(t, root.transform)}' → hips moved.");
                        winningHost = t.gameObject;
                        break;
                    }
                }

                // Restore baseline in either case.
                hips.localRotation = baseline;

                if (winningHost == null)
                {
                    log.LogWarning("No candidate host resolved the clip's paths. Legacy Animation cannot play these clips, needs Playables API or rebuilt path table.");
                }
                else if (winningHost != animHost)
                {
                    log.LogInfo($"Rehosting Animation from '{animHost.name}' → '{winningHost.name}'.");
                    Object.Destroy(anim);
                    anim = winningHost.AddComponent<Animation>();
                    anim.playAutomatically = false;
                    anim.cullingType = AnimationCullingType.AlwaysAnimate;
                    foreach (var c in clips) anim.AddClip(c, c.name);
                    anim.clip = clips[0];
                    anim.Play(idle.name);
                    animHost = winningHost;
                }
            }
            else
            {
                log.LogInfo("Sample test: 'Hips' bone not found, can't verify.");
            }

            // Ensure the mesh is clickable in play mode: give it a BoxCollider
            // sized from the SkinnedMesh bounds. Skip if the engine or a prior
            // pass already added one.
            EnsureCollider(root, smr);

            // Expose the clip catalog + the actual Animation component to the
            // rest of the plugin. NativeAnimationUiPatch's SetAnimation prefix
            // calls PlayClip on the exposer, that needs to reach the Animation
            // component which we may have installed on a different GameObject
            // (see the rehost path). Passing the ref explicitly avoids a
            // GetComponent<Animation>() lookup on the wrong host.
            var exposer = root.GetComponent<PickupableAnimationExposer>()
                          ?? root.AddComponent<PickupableAnimationExposer>();
            exposer.Configure(clips.Select(c => c.name).ToList(), anim);

            log.LogInfo($"Installed legacy Animation on '{animHost.name}' with clips: {string.Join(", ", clips.Select(c => c.name))}. Playing '{idle.name}' as default.");
        }

        /// <summary>
        /// v2.0 path: build a Humanoid Avatar and attach an Animator with
        /// the engine's C_Humanoid controller. Returns true if fully wired,
        /// false if we should fall back to legacy Animation.
        /// </summary>
        private static bool TryInstallHumanoidAnimator(GameObject root)
        {
            var log = Plugin.Log;

            // Idempotent early-out: the engine's mesh-import pipeline
            // cascades through THREE entry methods (SetRPGMesh →
            // LoadFromBinaryFile → LoadGlbData), and we postfix all of them,
            // so this runs 2–3 times per import. First call installs; subsequent
            // calls used to destroy the just-added Animator + reinstall and
            // sometimes NRE mid-reinstall, leaving the mesh with no working
            // Animator. If the tag is already there, we're already done ,
            // just return success.
            if (root.GetComponent<HumanoidTag>() != null)
            {
                log.LogInfo($"'{root.name}': humanoid animator already installed (tagged), skipping duplicate install.");
                return true;
            }

            // Need both an Avatar (we build it) and a Controller (we cache
            // from a native character). Without either, we can't run
            // humanoid retargeting.
            var controller = NativeControllerCache.TryFind();
            if (controller == null)
            {
                log.LogInfo($"'{root.name}': native C_Humanoid controller not cached yet, will retry after a native character is clicked. Falling back to legacy for now.");
                return false;
            }

            var avatar = HumanoidAvatarBuilder.TryBuild(root);
            if (avatar == null)
            {
                log.LogInfo($"'{root.name}': Humanoid Avatar build failed, falling back to legacy Animation.");
                return false;
            }

            // Remove any pre-existing Animator/Animation so we start clean.
            foreach (var oldAnim in root.GetComponents<Animation>()) Object.Destroy(oldAnim);
            foreach (var oldAnimator in root.GetComponents<Animator>()) Object.Destroy(oldAnimator);
            // Also clear any Animation/Animator we may have added earlier on Armature
            // during previous imports.
            foreach (var childAnim in root.GetComponentsInChildren<Animation>(true)) Object.Destroy(childAnim);
            foreach (var childAnimator in root.GetComponentsInChildren<Animator>(true))
                if (childAnimator.gameObject != root) Object.Destroy(childAnimator);

            // Tag this root so our click-routing patch knows this pickupable
            // is one of ours (and should bypass the engine's IAnimator wrapper
            // routing, which resolves to null for custom meshes).
            if (root.GetComponent<HumanoidTag>() == null) root.AddComponent<HumanoidTag>();

            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;   // engine drives movement, we just play limbs
            animator.updateMode = AnimatorUpdateMode.Normal;
            // CRITICAL: AlwaysAnimate, Meshy imports often have SkinnedMesh
            // bounds tight to the T-pose which culls the moment the character
            // moves a limb (CullUpdateTransforms then freezes the animation).
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // CRITICAL: Unity adds Animators enabled by default, BUT if the
            // GameObject was inactive at AddComponent time (mid-import) it can
            // land disabled. Force enable so SetTrigger/Play actually run.
            animator.enabled = true;
            animator.Rebind();
            animator.Update(0f);

            // Force the state machine into a known-good starting state.
            // Without this, some peers land the animator in whichever state
            // the controller's Entry transitions selected first, sometimes a
            // Combat/Emote frame instead of Idle, leaving the character stuck
            // in a "arms raised" pose until an incoming NetworkSend.Animate
            // moves them off it. Try a few likely Idle state names; Play() is
            // a no-op if the state doesn't exist. Also pin Movement Speed to
            // 0 so the walk blend tree resolves to standing-idle.
            foreach (var stateName in new[] { "Idle", "Idle_1", "Idle 1", "Base Layer.Idle" })
            {
                try { animator.Play(stateName, 0, 0f); } catch { /* state name may not exist, try next */ }
            }
            try { animator.SetFloat("Movement Speed", 0f); } catch { /* not a param, safe to ignore */ }
            animator.Update(0f);

            // Widen SkinnedMeshRenderer bounds too, same culling trap on the
            // renderer side; if it thinks the mesh is off-screen the whole
            // rig freezes.
            foreach (var s in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                s.updateWhenOffscreen = true;

            // CRITICAL: the engine reads animation params via IAnimator, which
            // it locates through Pickupable.animator → GetComponent<IAnimator>().
            // Just adding a Unity Animator isn't enough, we also need to
            // attach RpgEngine.UnityAnimator (a MonoBehaviour that wraps the
            // Unity Animator and exposes its parameters as AnimationDelegates).
            // Without this the panel skips us because animator resolves to null.
            // Correct namespace is RpgEngine.Animation.UnityAnimator (not root
            // RpgEngine.UnityAnimator, that trap bit us earlier with
            // PickupableExtensions too, five namespaces of the same class name).
            var unityAnimatorType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Animation.UnityAnimator");
            if (unityAnimatorType != null)
            {
                try
                {
                    root.AddComponent(unityAnimatorType);
                    log.LogInfo($"Added RpgEngine.UnityAnimator on '{root.name}', panel will now enumerate {controller.animationClips.Length} clips + parameters natively.");
                }
                catch (System.Exception ex)
                {
                    log.LogWarning($"Failed to add RpgEngine.UnityAnimator: {ex.Message}");
                }
            }
            else
            {
                log.LogWarning("RpgEngine.UnityAnimator type not found, panel will not render buttons for the humanoid Animator.");
            }

            // CRITICAL: Pickupable caches `.animator` on first access. If the
            // engine touched it during import (before we added UnityAnimator),
            // it cached a null value and never re-resolves. Reset the private
            // `_animator` field via reflection so the next access re-scans.
            ResetPickupableAnimatorCache(root);

            // v2.2.2: full-parity movement WITHOUT a second Pickupable.
            //
            // Adding PickupableAI alongside PickupableMesh looks tempting
            // (RequireComponent chain gives us Puppet + NavMeshAgent for free
            // and its Awake pushes the ⁂move⁂/Movement Speed delegates in),
            // but it collides with PickupableMesh in PropManager.allProps ,
            // one of the two ends up registered with a null .prop, and the
            // engine's FindAllAnimations iterator NREs on item.prop.inGameName.
            // Result: empty animation panel on the custom mesh.
            //
            // Instead we add Puppet directly (satisfies its RequireComponent
            // on the existing PickupableMesh; auto-adds NavMeshAgent) and
            // inject the two AnimationDelegates ourselves, calling back into
            // OUR PickupableMesh. MoveToInterceptPatch then lets the engine's
            // destination picker treat our PickupableMesh the same way as a
            // PickupableAI (the picker's click handler is hard-gated on
            // `target is PickupableAI` in the vanilla flow).
            InstallMovementBits(root);

            // Ensure a clickable collider is present (same as before).
            var smr = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault();
            EnsureCollider(root, smr);

            // v3.1.35: grounding is GroundOffset's job (attached after this
            // install). The old bounds-based AlignMeshToGround shifted GFX by
            // metres on stale bounds, differently on each PC, and fought it.

            // v3.1.18: on session reload, the engine restores animator state
            // from disk via SetAnimationData AFTER our install runs. That
            // leaves the character stuck in whatever pose they were saved in
            // (arms raised, mid-combat pose, etc.). Delete+re-add works
            // because it's a fresh spawn with no restored state. Add a
            // guard component that forces the animator back to Idle for the
            // first ~30 frames so any late state restoration gets overridden.
            var guard = root.GetComponent<IdleGuard>() ?? root.AddComponent<IdleGuard>();
            guard.Init(root.GetComponent<Animator>());

            // v3.1.19: hand-hold points for prop attachment. Add a Holder
            // component to root with LeftHand + RightHand transforms as its
            // editorHoldPoints. The engine's existing ParentApplicator.
            // TrySetNewHolder path (fires when a player drags a prop onto a
            // character) checks parent.TryGetComponent<Holder>() and calls
            // Holder.LockTo. With this in place, dragging any prop onto our
            // custom mesh snaps it to the closest hand, same as native NPCs.
            InstallHandHoldPoints(root);

            // v3.1.24: trigger a NavMesh rebake around the freshly-installed
            // pickupable. This is what the engine does when GM moves a
            // character (via NetworkSend.UpdatePropLocation → ClientReceive
            // path calls NavMeshController.TryAsyncReBake). Without it, peer
            // NavMeshAgents never land on a valid NavMesh, Puppet.Update
            // force-disables the agent every frame, and animations feel
            // frozen until the GM manually bumps the character. Doing it
            // here at install time makes the "wake up" happen automatically.
            TryNavMeshRebake(root);

            // v3.1.25: rebake alone isn't the full "GM moved" wake-up. What
            // actually snaps peers into sync is the UpdatePropLocation
            // network packet the engine sends when GM's transform changes.
            // Peer's ClientReceive.UpdatePropLocation does SetParent +
            // SetPosition + (conditional) rebake, which fixes both the
            // slightly-wrong initial peer position AND the frozen-animator
            // state. Send it ourselves on the host so peers don't have to
            // wait for the GM to bump the character manually.
            TryBroadcastPropLocation(root);

            // v3.1.14: try again to tone down chrome with MaterialPropertyBlock.
            // MPB is per-renderer OVERRIDE data that doesn't touch the actual
            // material asset, so it can't strip textures the way direct
            // material.SetFloat did in v3.1.11/12. Only overrides Smoothness;
            // leaves everything else exactly as GLTFUtility set it up.
            TryTonedownShineViaMPB(root);

            log.LogInfo($"'{root.name}': installed HUMANOID Animator with '{controller.name}' + custom Avatar. " +
                        $"Native animation panel will drive it directly ({controller.animationClips.Length} clips available).");
            // v3.1.36: humanScale is the avatar's hip height in metres (about
            // 0.9 for a person). It sets where the hips sit and how fast the
            // model walks, so a number far off that means sinking or crawling.
            log.LogInfo($"'{root.name}': avatar humanScale {animator.humanScale:F3}.");

            // IMPORTANT: no PickupableAnimationExposer added, with a real
            // Animator on the pickupable, the engine's FindAllAnimations
            // finds its parameters natively and renders the correct button
            // set. Our postfix that appends Meshy clip names to the panel
            // has an "if exposer present" guard, so it doesn't fire here.
            return true;
        }

        /// <summary>
        /// Add just enough of PickupableAI's stack to get walk-to-destination
        /// behavior without introducing a second Pickupable component (which
        /// breaks PropManager registration, see AttachPickupableAI history).
        ///
        /// What we add:
        ///   • Puppet (auto-adds NavMeshAgent via [RequireComponent])
        ///   • MovementBridge, a tiny MonoBehaviour that mimics the two
        ///     AnimationDelegates PickupableAI.Awake pushes into the animator:
        ///       - "⁂move⁂" Trigger → opens the MoveTo destination picker
        ///       - "Movement Speed" Float ↔ puppet.agentSpeed
        ///     But keyed to OUR PickupableMesh (via delegate closure), not a
        ///     new PickupableAI.
        ///
        /// The engine's MoveTo click-handler is separately patched (see
        /// MoveToInterceptPatch) so its `target is PickupableAI` gate also
        /// admits our tagged mesh.
        /// </summary>
        private static void InstallMovementBits(GameObject root)
        {
            var log = Plugin.Log;

            // 1. Puppet + NavMeshAgent.
            var puppetType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Puppet");
            if (puppetType == null) { log.LogWarning("InstallMovementBits: RpgEngine.Puppet not found."); return; }
            var puppet = root.GetComponent(puppetType) ?? root.AddComponent(puppetType);
            var navAgent = root.GetComponent<UnityEngine.AI.NavMeshAgent>();

            // Unity's default NavMeshAgent has a non-zero baseOffset which is
            // the distance the agent's transform floats above the NavMesh.
            // Native RPG Engine characters have this at 0 so they stand on the
            // floor; without setting it here, our custom meshes float ~1m up
            // and any Puppet.Update snaps them back to that hover height every
            // frame (visible bump-up right after a manual drag-down).
            if (navAgent != null) navAgent.baseOffset = 0f;

            // v3.1.36: Puppet.LateUpdate zeroes motionBone's local z on every
            // frame of a walk, and native prefabs set it in the editor. Ours
            // was null, so each walking frame threw a NullReferenceException
            // into Player.log (300-470 per walk). An empty child takes the
            // write harmlessly; the humanoid hips must not, or the body would
            // jump forward or back while walking.
            var motionBoneField = puppetType.GetField("motionBone");
            if (motionBoneField != null && motionBoneField.GetValue(puppet) as Transform == null)
            {
                var motionBone = new GameObject("AM_MotionBone").transform;
                motionBone.SetParent(root.transform, false);
                motionBoneField.SetValue(puppet, motionBone);
            }

            log.LogInfo($"InstallMovementBits: Puppet={(puppet == null ? "MISSING" : "present")} | " +
                        $"NavMeshAgent={(navAgent == null ? "MISSING" : $"present, onNavMesh={navAgent.isOnNavMesh}, baseOffset={navAgent.baseOffset}")}");

            // 2. Inject AnimationDelegates into the animator's list.
            var iAnimType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Animation.IAnimator");
            var iAnim = iAnimType != null ? root.GetComponent(iAnimType) : null;
            var pickupableMesh = root.GetComponent(HarmonyLib.AccessTools.TypeByName("RpgEngine.PickupableMesh"));
            if (iAnim == null || pickupableMesh == null)
            {
                log.LogWarning("InstallMovementBits: no IAnimator or no PickupableMesh, can't inject move delegate.");
                return;
            }

            // Attach a bridge component that will inject/update the delegates
            // (must run after this frame so PickupableMesh finishes wiring).
            var bridge = root.AddComponent<MovementBridge>();
            bridge.Init((Component)pickupableMesh, (Component)puppet, iAnim);
            log.LogInfo($"InstallMovementBits: MovementBridge attached, will inject ⁂move⁂ + Movement Speed delegates next frame.");
        }

        /// <summary>
        /// Pickupable's `animator` property is a Tuple-wrapped cache, first
        /// access resolves via GetComponent&lt;IAnimator&gt;() and stores the
        /// result. If the engine touched it before we added our UnityAnimator,
        /// it cached null. Reset the private `_animator` field so the next
        /// access re-resolves and finds the component we just installed.
        /// </summary>
        private static void ResetPickupableAnimatorCache(GameObject root)
        {
            var log = Plugin.Log;
            var pickupableType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Pickupable");
            if (pickupableType == null) return;

            var comp = root.GetComponent(pickupableType);
            if (comp == null)
            {
                comp = root.GetComponentInParent(pickupableType);
                if (comp == null) { log.LogWarning("ResetPickupableAnimatorCache: no Pickupable found."); return; }
            }

            var field = pickupableType.GetField("_animator",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            if (field == null)
            {
                log.LogWarning("ResetPickupableAnimatorCache: Pickupable._animator field not found.");
                return;
            }
            field.SetValue(comp, null);
            log.LogInfo($"Reset Pickupable._animator cache on '{comp.name}', next access re-resolves.");
        }

        /// <summary>
        /// Find the GameObject that GLTFUtility's animation clips were built
        /// against. Legacy Animation resolves curve paths from the transform
        /// it's attached to, so we need the ancestor where the character's
        /// bone hierarchy begins. Heuristic: walk up from SkinnedMeshRenderer's
        /// root bone until one level below `root`.
        /// </summary>
        private static GameObject ResolveAnimationHost(GameObject root, SkinnedMeshRenderer smr)
        {
            if (smr == null) return null;
            var t = smr.rootBone ?? smr.transform;
            // Walk up until parent IS root (or null). We want the topmost
            // ancestor that is a direct descendant of root, that's typically
            // the GLB's own top-level GameObject.
            while (t.parent != null && t.parent != root.transform)
                t = t.parent;
            return t == root.transform ? root : t.gameObject;
        }

        /// <summary>
        /// URP/Lit's default properties render materials as very glossy/chrome-
        /// like, Meshy's own preview uses matte PBR defaults, so imports look
        /// nothing like the artist saw in Meshy. Iterate every material on the
        /// character and clamp smoothness/metallic to matte values, and turn
        /// off env reflections + specular highlights so scene lighting doesn't
        /// add a chrome sheen. Every URP/Lit-based material has these
        /// properties; unknown shaders will silently no-op on the SetFloat
        /// calls (Material.SetFloat is safe on missing properties).
        /// </summary>
        /// <summary>
        /// Reduce chrome shine via MaterialPropertyBlock. MPB overrides
        /// per-renderer material properties without modifying the material
        /// asset, so textures stay intact even if the override doesn't work.
        /// Safe rollback: if this method breaks anything, comment out the
        /// call and characters render exactly as v3.1.13.
        /// </summary>
        private static void TryTonedownShineViaMPB(GameObject root)
        {
            var mpb = new MaterialPropertyBlock();
            int touched = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.GetPropertyBlock(mpb);
                // Smoothness: subtle highlights (0.12).
                mpb.SetFloat("_Smoothness", 0.12f);
                mpb.SetFloat("_Glossiness", 0.12f);
                // Darken base color slightly to restore saturation, the
                // scene's bright ambient light washes textures out to near
                // white at (1,1,1) default. 0.7 gray multiplier gives back
                // the rich color depth Meshy's viewer shows.
                var tint = new Color(0.7f, 0.7f, 0.7f, 1f);
                mpb.SetColor("_BaseColor", tint);
                mpb.SetColor("_Color", tint);   // Standard-shader alias
                renderer.SetPropertyBlock(mpb);
                touched++;
            }
            Plugin.Log.LogInfo($"TryTonedownShineViaMPB: applied Smoothness=0.2 MPB override on {touched} renderer(s).");
        }

        private static void SoftenMaterials(GameObject root)
        {
            var log = Plugin.Log;
            int materialsUpdated = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                // Modify sharedMaterials in-place (each imported mesh has its
                // own material instances from GLTFUtility, so no risk of
                // contaminating other characters). Avoid renderer.materials
                // getter, it instantiates copies and setting them back can
                // strip texture keyword variants, which white-washed the
                // character in v3.1.11.
                var mats = renderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;

                    // Just the two properties that actually control chrome look.
                    // Don't touch keywords or specular/env toggles, those
                    // interact with shader variants and can nuke textures.
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.2f);
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.2f);
                    if (m.HasProperty("_Metallic"))   m.SetFloat("_Metallic",   0f);

                    materialsUpdated++;
                }
            }
            log.LogInfo($"SoftenMaterials: toned down {materialsUpdated} material(s) on '{root.name}' (smoothness→0.2, metallic→0).");
        }

        /// <summary>
        /// Add a RpgEngine.Parenting.Holder component to root with LeftHand and
        /// RightHand transforms registered as hold points. This lets the engine's
        /// existing prop-parenting UI attach any prop (weapon, torch, etc.) to
        /// the character's hand automatically, exactly as with native NPCs.
        ///
        /// Wrinkle: Holder.Awake() populates the runtime `holdPoints` dictionary
        /// from `editorHoldPoints` at add-time, before we get a chance to set
        /// the list. So we add the component, populate editorHoldPoints, then
        /// manually rebuild the holdPoints dict ourselves (mimicking Awake).
        /// </summary>
        private static void InstallHandHoldPoints(GameObject root)
        {
            var log = Plugin.Log;
            var holderType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Parenting.Holder");
            var holdPointType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Parenting.HoldPoint");
            if (holderType == null || holdPointType == null)
            {
                log.LogWarning("InstallHandHoldPoints: RpgEngine.Parenting.Holder / HoldPoint not found. Prop attachment disabled for this mesh.");
                return;
            }

            // Find the hand transforms by walking the same candidate-name list
            // HumanoidAvatarBuilder uses. Right hand first (dominant hand, so
            // it gets the closer hold point ID; the engine picks by distance
            // anyway so ordering only matters for tie-breaks).
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;

            Transform FindByCandidates(HumanBodyBones bone)
            {
                foreach (var (b, candidates) in HumanoidAvatarBuilder.BoneMapForCapture)
                {
                    if (b != bone) continue;
                    foreach (var name in candidates)
                        if (byName.TryGetValue(name, out var t)) return t;
                }
                return null;
            }
            var handTransforms = new List<Transform>();
            var right = FindByCandidates(HumanBodyBones.RightHand);
            var left  = FindByCandidates(HumanBodyBones.LeftHand);
            if (right != null) handTransforms.Add(right);
            if (left  != null) handTransforms.Add(left);
            if (handTransforms.Count == 0)
            {
                log.LogWarning("InstallHandHoldPoints: neither LeftHand nor RightHand were mapped. No hold points to install.");
                return;
            }

            // Idempotent — if we already added one, don't re-add.
            var existing = root.GetComponent(holderType);
            if (existing != null)
            {
                log.LogInfo($"InstallHandHoldPoints: Holder already present on '{root.name}', skipping.");
                return;
            }

            var holder = root.AddComponent(holderType);

            // Set editorHoldPoints via reflection (public field). Then rebuild the
            // runtime holdPoints dictionary the same way Awake does.
            var editorField = holderType.GetField("editorHoldPoints");
            var runtimeField = holderType.GetField("holdPoints");
            if (editorField == null || runtimeField == null)
            {
                log.LogWarning("InstallHandHoldPoints: Holder fields (editorHoldPoints / holdPoints) not found. Structure changed?");
                return;
            }

            editorField.SetValue(holder, handTransforms);

            // Build the dictionary <int, HoldPoint>. Constructor: HoldPoint(int id, Transform transform).
            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(int), holdPointType);
            var dict = System.Activator.CreateInstance(dictType);
            var addMethod = dictType.GetMethod("Add", new[] { typeof(int), holdPointType });
            var holdPointCtor = holdPointType.GetConstructor(new[] { typeof(int), typeof(Transform) });
            if (addMethod == null || holdPointCtor == null)
            {
                log.LogWarning("InstallHandHoldPoints: HoldPoint constructor or Dictionary.Add not found. Skipping runtime dict build.");
                return;
            }
            for (int i = 0; i < handTransforms.Count; i++)
            {
                var hp = holdPointCtor.Invoke(new object[] { i + 1, handTransforms[i] });
                addMethod.Invoke(dict, new object[] { i + 1, hp });
            }
            runtimeField.SetValue(holder, dict);

            log.LogInfo($"InstallHandHoldPoints: added Holder with {handTransforms.Count} hold point(s): {string.Join(", ", handTransforms.Select(t => t.name))}.");
        }

        /// <summary>
        /// Ask NavMeshController to re-bake the navigation mesh around this
        /// pickupable. Mirrors what the engine does when GM movement flows
        /// through ClientReceive.UpdatePropLocation. Without this, peer-side
        /// NavMeshAgents stay off-mesh and Puppet.Update ends up disabling
        /// the agent every frame — which prevents animations from playing
        /// visibly until the GM bumps the character manually. Async so it
        /// doesn't block; result ignored (we don't need to wait for it).
        /// </summary>
        private static void TryNavMeshRebake(GameObject root)
        {
            var log = Plugin.Log;
            try
            {
                var pickupableType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Pickupable");
                if (pickupableType == null) return;
                var pickupable = root.GetComponent(pickupableType);
                if (pickupable == null) return;

                var ctrlType = HarmonyLib.AccessTools.TypeByName("RpgEngine.NavMeshController");
                if (ctrlType == null) { log.LogWarning("TryNavMeshRebake: NavMeshController type not found."); return; }
                var instField = ctrlType.GetField("instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                var inst = instField?.GetValue(null);
                if (inst == null) { log.LogWarning("TryNavMeshRebake: NavMeshController.instance is null."); return; }

                var method = HarmonyLib.AccessTools.Method(ctrlType, "TryAsyncReBake", new[] { pickupableType });
                if (method == null) { log.LogWarning("TryNavMeshRebake: TryAsyncReBake(Pickupable) not found."); return; }

                method.Invoke(inst, new object[] { pickupable });
                log.LogInfo($"TryNavMeshRebake: queued rebake around '{root.name}' — agent will land on NavMesh next tick.");
            }
            catch (System.Exception ex)
            {
                log.LogWarning($"TryNavMeshRebake threw (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Host-only: send NetworkSend.UpdatePropLocation(guid, TCP:true) so
        /// peers receive the "wake up" packet automatically instead of needing
        /// the GM to manually bump the character. On peer side this triggers
        /// ClientReceive.UpdatePropLocation which does SetParent + SetPosition
        /// (and conditionally a rebake) — the exact sequence that snaps a
        /// freshly-spawned custom mesh into full sync.
        /// </summary>
        private static void TryBroadcastPropLocation(GameObject root)
        {
            var log = Plugin.Log;
            try
            {
                // Only host broadcasts; on peer this call is a no-op or wrong-direction.
                var lobbyType = HarmonyLib.AccessTools.TypeByName("UnityUtils.Steam.LobbyManager")
                             ?? HarmonyLib.AccessTools.TypeByName("RpgEngine.Steam.RpgLobbyManager");
                var isServerProp = lobbyType?.GetProperty("isServer",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
                if (isServerProp != null && !(bool)isServerProp.GetValue(null)) return;

                var pickupableType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Pickupable");
                var pickupable = pickupableType != null ? root.GetComponent(pickupableType) : null;
                if (pickupable == null) return;
                var guidProp = pickupableType.GetProperty("guid",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var guid = guidProp?.GetValue(pickupable) as string;
                if (string.IsNullOrEmpty(guid)) return;

                var netSendType = HarmonyLib.AccessTools.TypeByName("RpgEngine.NetworkSend");
                if (netSendType == null) return;
                var method = HarmonyLib.AccessTools.Method(netSendType, "UpdatePropLocation",
                    new[] { typeof(string), typeof(bool) });
                if (method == null)
                {
                    log.LogWarning("TryBroadcastPropLocation: NetworkSend.UpdatePropLocation(string, bool) not found.");
                    return;
                }

                method.Invoke(null, new object[] { guid, true });
                log.LogInfo($"TryBroadcastPropLocation: sent UpdatePropLocation for '{guid}' — peers should sync without needing GM to bump the character.");
            }
            catch (System.Exception ex)
            {
                log.LogWarning($"TryBroadcastPropLocation threw (non-fatal): {ex.Message}");
            }
        }

        private static void EnsureCollider(GameObject root, SkinnedMeshRenderer smr)
        {
            if (root == null || smr == null) return;

            // Skip only if there's already a NON-degenerate collider that will
            // catch clicks. The engine puts a MeshCollider on the CustomMesh
            // root by default, but its Mesh is either null or a placeholder,
            // which raycast misses. Detect degenerate MeshColliders and add a
            // proper BoxCollider anyway.
            var haveGood = root.GetComponentsInChildren<Collider>(true)
                .Any(c =>
                {
                    if (c is BoxCollider bc)   return bc.size.sqrMagnitude > 0.01f;
                    if (c is SphereCollider sc) return sc.radius > 0.05f;
                    if (c is CapsuleCollider cc) return cc.radius > 0.05f;
                    if (c is MeshCollider mc)  return mc.sharedMesh != null && mc.sharedMesh.vertexCount > 8;
                    return true;
                });
            if (haveGood)
            {
                Plugin.Log.LogInfo("Skipping BoxCollider, existing collider looks usable.");
                return;
            }

            var box = root.AddComponent<BoxCollider>();
            var b = smr.bounds;

            // Sanity clamp both dimensions and center. Some Meshy exports come
            // in with broken SkinnedMeshRenderer bounds (min.y in the thousands,
            // size >100m per axis). Without clamping we build a monster
            // collider that catches raycasts from across the map AND makes the
            // Tilt5 selector spam NREs every frame. Real character bounds are
            // roughly 0.5-3m per side; anything larger is bogus.
            var center = root.transform.InverseTransformPoint(b.center);
            if (center.magnitude > 5f)
            {
                Plugin.Log.LogWarning($"BoxCollider center {center} is unrealistic (bounds likely broken). Using origin instead.");
                center = new Vector3(0f, 1f, 0f);   // approximate character mid-height
            }
            box.center = center;

            var size = new Vector3(
                Mathf.Clamp(b.size.x, 0.5f, 3f),
                Mathf.Clamp(b.size.y, 0.5f, 3f),
                Mathf.Clamp(b.size.z, 0.5f, 3f));
            box.size = size;
            Plugin.Log.LogInfo($"Added BoxCollider on '{root.name}', center={box.center} size={box.size} (raw bounds size={b.size}).");
        }

        private static string GetPath(Transform t, Transform root)
        {
            var parts = new System.Collections.Generic.List<string>();
            while (t != null && t != root)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        private static Transform FindBone(GameObject root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static void LogHierarchy(GameObject go, int depth)
        {
            if (go == null) return;
            var indent = new string(' ', depth * 2);
            var components = string.Join(",", go.GetComponents<Component>().Select(c => c.GetType().Name));
            Plugin.Log.LogInfo($"  [tree] {indent}{go.name} [{components}]");
            if (depth > 4) return;   // don't dump the whole bone tree
            foreach (Transform child in go.transform) LogHierarchy(child.gameObject, depth + 1);
        }

        private static List<AnimationClip> CollectClips(GameObject root)
        {
            var found = new List<AnimationClip>();

            // Legacy Animation components on the root or children.
            foreach (var a in root.GetComponentsInChildren<Animation>(true))
            {
                foreach (AnimationState state in a)
                {
                    if (state.clip != null && !found.Contains(state.clip))
                        found.Add(state.clip);
                }
                if (a.clip != null && !found.Contains(a.clip))
                    found.Add(a.clip);
            }

            // Animator + assigned RuntimeAnimatorController.
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController == null) continue;
                foreach (var clip in animator.runtimeAnimatorController.animationClips)
                {
                    if (clip != null && !found.Contains(clip)) found.Add(clip);
                }
            }

            // Fallback: reflect over MonoBehaviour fields for any AnimationClip refs.
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var fields = mb.GetType().GetFields(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                foreach (var f in fields)
                {
                    try
                    {
                        var val = f.GetValue(mb);
                        if (val is AnimationClip clip && !found.Contains(clip))
                            found.Add(clip);
                    }
                    catch { /* skip */ }
                }
            }

            return found;
        }
    }

    /// <summary>
    /// Marker component: we attach this to any GameObject where we've installed
    /// a Humanoid Animator + C_Humanoid controller. The click-routing patch
    /// uses this to distinguish OUR custom meshes from native characters, so
    /// our direct-Animator fallback only runs on ours and never hijacks the
    /// engine's own routing for built-in chars.
    /// </summary>
    public class HumanoidTag : MonoBehaviour { }

    /// <summary>
    /// Watchdog running for the first ~30 frames after a custom-mesh install.
    /// Three jobs, all timing-related:
    ///
    /// 1. Force the animator into Idle each frame, overriding any late
    ///    state-restoration from the engine's disk-save reload path.
    /// 2. (Removed in v3.1.35: ground alignment, now GroundOffset's job.)
    /// 3. Force SkinnedMeshRenderer bounds refresh each frame so the renderer
    ///    isn't frustum-culled to invisibility before its first update
    ///    (the "won't appear on peer until GM does an action" bug).
    /// </summary>
    public class IdleGuard : MonoBehaviour
    {
        private Animator _animator;
        private int _framesLeft;
        private static readonly string[] IdleStateNames = { "Idle", "Idle_1", "Idle 1", "Base Layer.Idle" };

        public void Init(Animator animator)
        {
            _animator = animator;
            _framesLeft = 30;   // ~half a second at 60fps, long enough for any late restore
            enabled = true;
        }

        private void LateUpdate()
        {
            if (_framesLeft <= 0) { enabled = false; return; }

            // 1. Force Idle on the animator every frame (still cheap, catches
            //    late SetAnimationData restoration from the engine's disk-save).
            if (_animator != null)
            {
                foreach (var s in IdleStateNames)
                {
                    try { _animator.Play(s, 0, 0f); } catch { /* try next */ }
                }
                try { _animator.SetFloat("Movement Speed", 0f); } catch { }
            }

            // 2. Bounds reset every 5th frame, so the renderer isn't culled
            //    before its first update. (v3.1.35: ground alignment moved to
            //    GroundOffset, which measures once the pose has settled.)
            if (_framesLeft % 5 == 0)
            {
                foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    try { smr.ResetBounds(); } catch { }
                }
            }

            _framesLeft--;
            if (_framesLeft == 0) enabled = false;
        }
    }

    /// <summary>
    /// Runtime bridge that injects "⁂move⁂" (Trigger) and "Movement Speed"
    /// (FloatAny) AnimationDelegates into our custom mesh's animator.animations
    /// list, so the engine's panel renders a Move button + a speed slider.
    ///
    /// Cannot run in InstallMovementBits directly, the delegate closes over
    /// this component's own fields, so the fields must be set before we build
    /// the delegates. We defer to Update() so the first frame gives Puppet's
    /// Awake time to wire its NavMeshAgent and Pickupable's animator to fully
    /// cache before we mutate the list.
    /// </summary>
    public class MovementBridge : MonoBehaviour
    {
        private Component _pickupable;
        private Component _puppet;
        private object _iAnim;
        private bool _injected;
        // Cached reflection handles, resolved once at Init time.
        private static System.Reflection.PropertyInfo _agentSpeedProp;
        private static System.Reflection.MethodInfo _showMoveTo;
        private static System.Reflection.MethodInfo _hideMoveTo;
        private static System.Reflection.PropertyInfo _isShowingProp;
        private static System.Type _animDelegateType;
        private static System.Type _delegateTypeEnum;
        private static System.Type _enabledModesStruct;

        public void Init(Component pickupable, Component puppet, object iAnim)
        {
            _pickupable = pickupable;
            _puppet = puppet;
            _iAnim = iAnim;

            // Resolve reflection targets once.
            if (_agentSpeedProp == null)
                _agentSpeedProp = puppet.GetType().GetProperty("agentSpeed");
            if (_animDelegateType == null)
                _animDelegateType = HarmonyLib.AccessTools.TypeByName("RpgEngine.Animation.AnimationDelegate");
            if (_delegateTypeEnum == null)
                _delegateTypeEnum = HarmonyLib.AccessTools.Inner(_animDelegateType, "Type");
            if (_enabledModesStruct == null)
                _enabledModesStruct = HarmonyLib.AccessTools.Inner(_animDelegateType, "EnabledModes");

            var moveToType = HarmonyLib.AccessTools.TypeByName("RpgEngine.NavState.MoveTo");
            if (moveToType != null)
            {
                // UniqueMono<MoveTo>.instance is a static generic, reach it via reflection.
                _showMoveTo = moveToType.GetMethod("Show", new[] { HarmonyLib.AccessTools.TypeByName("RpgEngine.Pickupable") });
                _hideMoveTo = moveToType.GetMethod("Hide", System.Type.EmptyTypes);
                _isShowingProp = moveToType.BaseType?.GetProperty("isShowing");
            }

            // Inject immediately, Puppet is fully constructed by the time we
            // reach here (AddComponent<Puppet> returned after its Awake), and
            // the animator's animations list is already populated by UnityAnimator.
            try
            {
                InjectDelegates();
                _injected = true;
                enabled = false;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"MovementBridge.InjectDelegates threw (from Init): {ex}");
            }

            // NOTE: we do NOT Warp the NavMeshAgent here. NavMesh.SamplePosition
            // returns a point ON the navmesh surface, but our character's
            // transform pivot may be above the feet (mid-torso for Meshy imports),
            // so warping the transform to a floor-height point sinks the model.
            // The correct behavior is: leave transform where the user placed her,
            // and rely on agent.SetDestination() to project + navigate. If the
            // agent isn't on the mesh at destination-click time, MoveToInterceptPatch
            // will attempt a SamplePosition + Warp then, when we have the click
            // destination as reference (not the placement point).
        }

        private void Update()
        {
            // Retry-inject if the animations list appears to have been rebuilt
            // (e.g. UnityAnimator.animations getter re-lazy-inits). Cheap check.
            if (_injected || _iAnim == null) return;
            try { InjectDelegates(); _injected = true; enabled = false; }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"MovementBridge (Update) threw: {ex}"); _injected = true; enabled = false; }
        }

        private void InjectDelegates()
        {
            var animsProp = _iAnim.GetType().GetProperty("animations");
            var list = animsProp?.GetValue(_iAnim) as System.Collections.IList;
            if (list == null) { Plugin.Log.LogWarning("MovementBridge: animator.animations not available."); return; }

            // Check EACH delegate independently, the C_Humanoid controller
            // already exposes a "Movement Speed" Animator parameter (which
            // UnityAnimator auto-wraps into a FloatAny delegate). Early-
            // returning if that's present would silently skip the ⁂move⁂
            // insertion that actually shows the Move button.
            var nameField = _animDelegateType.GetField("name");
            bool hasMove = false, hasSpeed = false;
            foreach (var d in list)
            {
                var n = nameField.GetValue(d) as string;
                if (n == "⁂move⁂") hasMove = true;
                else if (n == "Movement Speed") hasSpeed = true;
            }

            if (!hasMove)
            {
                var move = System.Activator.CreateInstance(_animDelegateType);
                _animDelegateType.GetField("name").SetValue(move, "⁂move⁂");
                _animDelegateType.GetField("type").SetValue(move, System.Enum.ToObject(_delegateTypeEnum, 9)); // Trigger=9
                _animDelegateType.GetField("enabledModes").SetValue(move, _enabledModesStruct.GetProperty("PlayOnly").GetValue(null));
                System.Func<UnityEngine.YieldInstruction> setTrigger = () => { TogglePicker(); return null; };
                _animDelegateType.GetField("SetTrigger").SetValue(move, setTrigger);
                list.Insert(Mathf.Clamp(list.Count, 0, 1), move);
            }

            if (!hasSpeed)
            {
                var speed = System.Activator.CreateInstance(_animDelegateType);
                _animDelegateType.GetField("name").SetValue(speed, "Movement Speed");
                _animDelegateType.GetField("type").SetValue(speed, System.Enum.ToObject(_delegateTypeEnum, 2)); // FloatAny=2
                _animDelegateType.GetField("enabledModes").SetValue(speed, _enabledModesStruct.GetProperty("PlayOnly").GetValue(null));
                System.Func<float> getFloat = () => (float)(_agentSpeedProp.GetValue(_puppet) ?? 0f);
                System.Func<float, UnityEngine.YieldInstruction> setFloat = (v) => { _agentSpeedProp.SetValue(_puppet, v); return null; };
                _animDelegateType.GetField("GetFloat").SetValue(speed, getFloat);
                _animDelegateType.GetField("SetFloat").SetValue(speed, setFloat);
                list.Insert(Mathf.Clamp(list.Count, 0, 2), speed);
            }

            Plugin.Log.LogInfo($"MovementBridge: injected on '{gameObject.name}'. hadMove={hasMove}, hadSpeed={hasSpeed}, animations.Count={list.Count}");
        }

        private void TogglePicker()
        {
            if (_showMoveTo == null || _pickupable == null) return;
            // UniqueMono<T>.instance is a public static FIELD (not property)
            // inherited by MoveTo. Read it directly off the concrete subclass
            // with FlattenHierarchy, no generic-type reconstruction needed.
            var moveToType = HarmonyLib.AccessTools.TypeByName("RpgEngine.NavState.MoveTo");
            if (moveToType == null) { Plugin.Log.LogWarning("MovementBridge: MoveTo type not found."); return; }
            var instField = moveToType.GetField("instance",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.FlattenHierarchy);
            var instance = instField?.GetValue(null);
            if (instance == null) { Plugin.Log.LogWarning("MovementBridge: MoveTo.instance is null."); return; }

            var showing = (bool)(_isShowingProp?.GetValue(instance) ?? false);
            if (showing) _hideMoveTo.Invoke(instance, null);
            else _showMoveTo.Invoke(instance, new object[] { _pickupable });
            Plugin.Log.LogInfo($"MovementBridge: TogglePicker → {(showing ? "Hide" : "Show")}");
        }
    }

    /// <summary>
    /// Small component attached to every rigged custom mesh so downstream
    /// engine code (and follow-up Harmony patches) can discover which
    /// animations are available to play. Also exposes a PlayClip method
    /// callable from anywhere.
    /// </summary>
    public class PickupableAnimationExposer : MonoBehaviour
    {
        public List<string> ClipNames { get; private set; } = new List<string>();

        // The Animation component we drive. May live on a different GameObject
        // than this exposer (installed at rootBone.parent, not on the
        // pickupable root), so we hold an explicit reference rather than
        // calling GetComponent on ourselves.
        public Animation Anim;

        public void Configure(List<string> clipNames, Animation anim = null)
        {
            ClipNames = clipNames ?? new List<string>();
            if (anim != null) Anim = anim;
        }

        public bool PlayClip(string name, bool crossFade = true, float fadeSeconds = 0.15f)
        {
            // Prefer the explicit reference set at install time. Fall back to a
            // one-shot search in the parent tree in case something destroyed
            // and re-added the Animation (or on legacy plugins that pre-date
            // the Anim field).
            var anim = Anim;
            if (anim == null)
            {
                anim = GetComponentInParent<Animation>() ?? GetComponentInChildren<Animation>(true);
                if (anim == null) return false;
                Anim = anim;
            }
            var state = anim[name];
            if (state == null) return false;
            if (crossFade) anim.CrossFade(name, fadeSeconds);
            else anim.Play(name);
            return true;
        }

        public void StopAll()
        {
            (Anim ?? GetComponentInParent<Animation>())?.Stop();
        }
    }
}
