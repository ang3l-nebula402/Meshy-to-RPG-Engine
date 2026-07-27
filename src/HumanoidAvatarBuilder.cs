using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RpgEngineAnimatedMeshMod
{
    /// <summary>
    /// Builds a Unity Humanoid Avatar from a Meshy/Mixamo-rigged mesh at
    /// runtime. Once we have a Humanoid Avatar, Unity's retargeting system
    /// lets us play ANY humanoid animation on this rig, no bone renaming,
    /// no clip conversion. That means the engine's own `C_Humanoid`
    /// RuntimeAnimatorController (with all its states, dances, emotes, combat
    /// triggers) plays cleanly on our imported meshes.
    ///
    /// Bone-name mapping table below covers the two common naming styles we've
    /// seen: Meshy's Mixamo-style (Hips / LeftUpLeg / LeftShoulder / Spine) and
    /// the engine's own naming (Hips / UpperLeg_L / Clavicle_L / Spine_01) ,
    /// so this same builder works whether the mesh came from Meshy, Mixamo, or
    /// something rigged to match the engine's convention.
    /// </summary>
    internal static class HumanoidAvatarBuilder
    {
        // Exposed for other builders (e.g. hand-hold-point install) that need
        // the same name-alias table to locate specific bones.
        internal static (HumanBodyBones Bone, string[] Candidates)[] BoneMapForCapture => BONE_MAP;

        // Unity HumanBodyBones (only the required + commonly-supplied bones ,
        // Unity fills in fingers and jaw from the skeleton array if present).
        // Each entry lists candidate Transform names in preference order.
        private static readonly (HumanBodyBones Bone, string[] Candidates)[] BONE_MAP =
        {
            (HumanBodyBones.Hips,          new[] { "Hips", "hips", "Hip", "mixamorig:Hips" }),

            // Spine chain, MUST go bottom-to-top (Unity's Spine is a child of
            // Hips, Chest is a child of Spine, UpperChest is a child of Chest).
            //
            // Meshy's naming is COUNTER-INTUITIVE: their chain is
            //   Hips → Spine02 → Spine01 → Spine → neck
            // so "Spine02" is the LOWEST bone (just above Hips) and "Spine"
            // is the HIGHEST (just below neck). That means:
            //   Unity Spine       = Meshy Spine02
            //   Unity Chest       = Meshy Spine01
            //   Unity UpperChest  = Meshy Spine
            //
            // Engine's own naming (Spine_01/02/03) is the intuitive order.
            // Mixamo's (mixamorig:Spine, Spine1, Spine2) is also intuitive.
            (HumanBodyBones.Spine,         new[] { "Spine02", "Spine_01", "spine", "mixamorig:Spine", "Spine" }),
            (HumanBodyBones.Chest,         new[] { "Spine01", "Spine_02", "Spine1", "chest", "mixamorig:Spine1" }),
            (HumanBodyBones.UpperChest,    new[] { "Spine", "Spine_03", "Spine2", "Chest", "mixamorig:Spine2" }),

            (HumanBodyBones.Neck,          new[] { "Neck", "neck", "mixamorig:Neck" }),
            (HumanBodyBones.Head,          new[] { "Head", "head", "mixamorig:Head" }),
            (HumanBodyBones.LeftEye,       new[] { "LeftEye", "eye_L" }),
            (HumanBodyBones.RightEye,      new[] { "RightEye", "eye_R" }),
            (HumanBodyBones.Jaw,           new[] { "Jaw", "jaw" }),

            // Arms
            (HumanBodyBones.LeftShoulder,  new[] { "LeftShoulder", "Clavicle_L", "mixamorig:LeftShoulder" }),
            (HumanBodyBones.LeftUpperArm,  new[] { "LeftArm", "Shoulder_L", "mixamorig:LeftArm" }),
            (HumanBodyBones.LeftLowerArm,  new[] { "LeftForeArm", "Elbow_L", "mixamorig:LeftForeArm" }),
            (HumanBodyBones.LeftHand,      new[] { "LeftHand", "Hand_L", "mixamorig:LeftHand" }),

            (HumanBodyBones.RightShoulder, new[] { "RightShoulder", "Clavicle_R", "mixamorig:RightShoulder" }),
            (HumanBodyBones.RightUpperArm, new[] { "RightArm", "Shoulder_R", "mixamorig:RightArm" }),
            (HumanBodyBones.RightLowerArm, new[] { "RightForeArm", "Elbow_R", "mixamorig:RightForeArm" }),
            (HumanBodyBones.RightHand,     new[] { "RightHand", "Hand_R", "mixamorig:RightHand" }),

            // Legs
            (HumanBodyBones.LeftUpperLeg,  new[] { "LeftUpLeg", "UpperLeg_L", "mixamorig:LeftUpLeg" }),
            (HumanBodyBones.LeftLowerLeg,  new[] { "LeftLeg", "LowerLeg_L", "mixamorig:LeftLeg" }),
            (HumanBodyBones.LeftFoot,      new[] { "LeftFoot", "Ankle_L", "mixamorig:LeftFoot" }),
            // Toes intentionally UNMAPPED. Meshy's LeftToeBase/RightToeBase
            // T-pose orientations don't match what C_Humanoid's animations
            // expect, so every leg animation ends up twisting the toe bones
            // into the shins ("inward spike" distortion at the boots).
            // Leaving toes unmapped lets Unity ignore the toe muscle values
            // and the toe geometry just moves rigidly with the foot bone.

            (HumanBodyBones.RightUpperLeg, new[] { "RightUpLeg", "UpperLeg_R", "mixamorig:RightUpLeg" }),
            (HumanBodyBones.RightLowerLeg, new[] { "RightLeg", "LowerLeg_R", "mixamorig:RightLeg" }),
            (HumanBodyBones.RightFoot,     new[] { "RightFoot", "Ankle_R", "mixamorig:RightFoot" }),
        };

        // Bones Unity absolutely requires or the humanoid avatar is invalid.
        private static readonly HumanBodyBones[] REQUIRED =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
        };

        /// <summary>
        /// Try to build a Humanoid Avatar rooted at the given GameObject.
        /// Returns null on failure (missing required bones, invalid avatar) ,
        /// caller falls back to legacy Animation in that case.
        /// </summary>
        internal static Avatar TryBuild(GameObject root)
        {
            var log = Plugin.Log;
            if (root == null) return null;

            // Collect every Transform in the tree by name. Handle duplicate
            // names by preferring the one closest to the root.
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName.Add(t.name, t);

            var humanBones = new List<HumanBone>();
            var mapped = new Dictionary<HumanBodyBones, Transform>();

            foreach (var (bone, candidates) in BONE_MAP)
            {
                foreach (var name in candidates)
                {
                    if (byName.TryGetValue(name, out var t))
                    {
                        // v3.1.8: rolled back all muscle clamps. Custom
                        // HumanLimit ranges depend on the bind-pose axes
                        // matching Unity's convention; Meshy's rig has just
                        // enough axis mismatch that any clamp we set either
                        // does nothing (feet limit at ±20°) or crushes normal
                        // motion (shin limit made her walk on stilts).
                        // Retargeting distortion at some action poses is a
                        // known cross-rig quirk, Meshy's skeleton isn't the
                        // one C_Humanoid's animations were authored against.
                        var limit = new HumanLimit { useDefaultValues = true };

                        humanBones.Add(new HumanBone
                        {
                            humanName = bone.ToString(),
                            boneName = name,
                            limit = limit,
                        });
                        mapped[bone] = t;
                        break;
                    }
                }
            }

            // Verify required bones present.
            var missing = REQUIRED.Where(b => !mapped.ContainsKey(b)).ToList();
            if (missing.Count > 0)
            {
                log.LogWarning($"HumanoidAvatarBuilder: '{root.name}' missing required bones: [{string.Join(", ", missing)}]. Available Transform names include: [{string.Join(", ", byName.Keys.Take(30))}{(byName.Count > 30 ? "…" : "")}]");
                return null;
            }

            log.LogInfo($"HumanoidAvatarBuilder: mapped {mapped.Count} bones on '{root.name}'.");

            // Build the skeleton bone table, must include ALL bones under root.
            var skeleton = root.GetComponentsInChildren<Transform>(true)
                .Select(t => new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                })
                .ToArray();

            var desc = new HumanDescription
            {
                human = humanBones.ToArray(),
                skeleton = skeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                // Moderate stretch (Unity default is 0.05). Setting these to
                // 0 in v3.1.5 didn't help the shin distortion and may have
                // amplified it (rigid bone length forced compensation in
                // rotation). Keep Unity's default here.
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0.0f,
                hasTranslationDoF = false,
            };

            Avatar avatar;
            try
            {
                avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
            }
            catch (System.Exception ex)
            {
                log.LogWarning($"HumanoidAvatarBuilder: AvatarBuilder threw on '{root.name}': {ex.Message}");
                return null;
            }

            if (avatar == null || !avatar.isValid)
            {
                log.LogWarning($"HumanoidAvatarBuilder: BuildHumanAvatar produced invalid avatar for '{root.name}' " +
                               $"(avatar={(avatar == null ? "null" : "non-null")}, isValid={avatar?.isValid}). " +
                               $"Bone map: {string.Join(", ", humanBones.Select(h => $"{h.humanName}={h.boneName}"))}");
                return null;
            }

            avatar.name = root.name + "_HumanoidAvatar";
            log.LogInfo($"HumanoidAvatarBuilder: built valid Humanoid Avatar '{avatar.name}'.");
            return avatar;
        }
    }
}
