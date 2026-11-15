using System.Collections.Generic;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// The realistic mannequin (Resources/Mannequin/MannequinModel.fbx, a humanoid rig), posed with Unity's
    /// muscle system so the poses don't depend on how the rig's bones are oriented.
    ///   Supine   - lying on the back, arms along the sides (practice dummy).
    ///   Recovery - lying on the left side, top knee bent forward, top hand under the cheek,
    ///              bottom arm out in front (recovery-position ghost).
    /// Local layout of the returned object (matches PracticeMannequin / RecoveryGhost):
    ///   +Z toward the head, +Y up, back/side resting on y = 0.
    ///   Supine: the crown at z = +length/2, heels at z = -length/2.
    ///   Recovery: hips at the origin, the person faces +X.
    /// </summary>
    public static class MannequinModel
    {
        public enum Pose { Supine, Recovery }

        static GameObject s_Prefab;
        static Material s_Skin;
        static bool s_Loaded;

        public static bool Available { get { Load(); return s_Prefab != null; } }

        static void Load()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            s_Prefab = Resources.Load<GameObject>("Mannequin/MannequinModel");
            s_Skin = Resources.Load<Material>("Mannequin/MannequinSkin");
            if (s_Prefab == null) Debug.LogWarning("CPRModel: Resources/Mannequin/MannequinModel not found - using the capsule figure");
        }

        /// <summary>Create the posed model under 'parent'. 'material' overrides the skin (e.g. a ghost glow).</summary>
        public static GameObject Create(Transform parent, Pose pose, float length, Material material = null)
        {
            Load();
            if (s_Prefab == null) return null;

            // Holder keeps the layout above; the model inside is posed standing, then laid down.
            var holder = new GameObject($"Mannequin ({pose})").transform;
            holder.SetParent(parent, false);
            var model = Object.Instantiate(s_Prefab, holder, false);
            model.name = "Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            var animator = model.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                animator.enabled = false; // posed once by hand; no animation plays
                ApplyMuscles(animator, model.transform, pose);
                if (pose == Pose.Recovery) AimRecoveryLimbs(animator); else StraightenLegs(animator);
            }
            var mat = material != null ? material : s_Skin;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                if (mat != null) { var mats = r.sharedMaterials; for (var i = 0; i < mats.Length; i++) mats[i] = mat; r.sharedMaterials = mats; }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }

            // Scale to the body length (measured standing, head to heel, before lying down).
            var standing = Bounds(model, holder);
            var height = Mathf.Max(0.1f, standing.size.y);
            model.transform.localScale = Vector3.one * (length / height);

            // Lay down: the model's up (+Y) -> head direction (+Z); its front (+Z) -> up (supine) or +X (on its side).
            model.transform.localRotation = pose == Pose.Supine
                ? Quaternion.LookRotation(Vector3.up, Vector3.forward)
                : Quaternion.LookRotation(Vector3.right, Vector3.forward);

            // Rest it on y = 0; supine spans z = -L/2..+L/2, recovery puts the hips at the origin.
            var b = Bounds(model, holder);
            var shift = new Vector3(-b.center.x, -b.min.y, -b.center.z);
            if (pose == Pose.Recovery && animator != null && animator.isHuman)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) { var h = holder.InverseTransformPoint(hips.position); shift = new Vector3(-h.x, -b.min.y, -h.z); }
            }
            else if (pose == Pose.Supine) shift.z = -b.max.z + 0.5f * length; // crown at +L/2
            model.transform.localPosition += shift;
            return holder.gameObject;
        }

        static Bounds Bounds(GameObject model, Transform space)
        {
            var first = true;
            var b = new Bounds();
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                if (r is SkinnedMeshRenderer smr)
                {
                    // Bake the current pose so the bounds follow the posed (not bind-pose) mesh.
                    var mesh = new Mesh();
                    smr.BakeMesh(mesh, true);
                    var m = space.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                    foreach (var v in mesh.vertices) { var p = m.MultiplyPoint3x4(v); if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p); }
                    if (Application.isPlaying) Object.Destroy(mesh); else Object.DestroyImmediate(mesh);
                }
            }
            return b;
        }

        // ---------- poses (muscle values, -1..1) ----------

        static void ApplyMuscles(Animator animator, Transform root, Pose pose)
        {
            var handler = new HumanPoseHandler(animator.avatar, root);
            var hp = new HumanPose();
            handler.GetHumanPose(ref hp);
            for (var i = 0; i < hp.muscles.Length; i++) hp.muscles[i] = 0f;
            foreach (var kv in pose == Pose.Supine ? Supine : Recovery)
            {
                var idx = MuscleIndex(kv.Key);
                if (idx >= 0) hp.muscles[idx] = kv.Value;
            }
            hp.bodyPosition = new Vector3(0f, hp.bodyPosition.y, 0f);
            hp.bodyRotation = Quaternion.identity;
            handler.SetHumanPose(ref hp);
            handler.Dispose();
        }

        // Body frame of the standing model: R = its right, U = up (head), F = front.
        static bool Frame(Animator a, out Vector3 R, out Vector3 U, out Vector3 F)
        {
            R = U = F = default;
            var hips = a.GetBoneTransform(HumanBodyBones.Hips);
            var lSh = a.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var rSh = a.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var head = a.GetBoneTransform(HumanBodyBones.Head);
            if (hips == null || lSh == null || rSh == null || head == null) return false;
            R = (rSh.position - lSh.position).normalized;
            U = Vector3.ProjectOnPlane(head.position - hips.position, R).normalized;
            F = Vector3.Cross(R, U).normalized;
            return true;
        }

        /// <summary>Supine: legs straight and in line with the body so the heels rest on the floor.</summary>
        static void StraightenLegs(Animator a)
        {
            if (!Frame(a, out var R, out var U, out var F)) return;
            foreach (var (up, low, foot, side) in new[] {
                (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, -1f),
                (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, 1f) })
            {
                var dir = -U + 0.04f * side * R; // feet very slightly apart
                Aim(a, up, low, dir);
                Aim(a, low, foot, dir);
            }
        }

        /// <summary>
        /// Recovery position (lying on the LEFT side). Each joint is placed at a point measured from the floor:
        /// the floor is the plane under the left shoulder, at "height" = distance along +R from it.
        /// </summary>
        static void AimRecoveryLimbs(Animator a)
        {
            if (!Frame(a, out var R, out var U, out var F)) return;
            Vector3 B(HumanBodyBones b) => a.GetBoneTransform(b).position;
            var hips = B(HumanBodyBones.Hips);
            var floorOffset = Vector3.Dot(B(HumanBodyBones.LeftUpperArm) - hips, R) - 0.07f; // under the bottom shoulder
            // A point 'f' in front, 'u' toward the head (from 'from'), at height 'h' above the floor.
            Vector3 At(Vector3 from, float f, float u, float h)
            {
                var p = from + F * f + U * u;
                return p + R * (floorOffset + h - Vector3.Dot(p - hips, R));
            }
            var lSh = B(HumanBodyBones.LeftUpperArm); var rSh = B(HumanBodyBones.RightUpperArm);
            var lHip = B(HumanBodyBones.LeftUpperLeg); var rHip = B(HumanBodyBones.RightUpperLeg);
            var head = B(HumanBodyBones.Head);
            float Len(HumanBodyBones x, HumanBodyBones y) => Vector3.Distance(B(x), B(y));
            var upperArm = Len(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
            var thigh = Len(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
            var shin = Len(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);

            // Bottom (left) leg: straight, slightly behind the body line.
            AimAt(a, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, At(lHip, -0.08f, -thigh, 0.07f));
            AimAt(a, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, At(B(HumanBodyBones.LeftLowerLeg), -0.06f, -shin, 0.06f));
            // Top (right) leg: hip bent ~90 degrees, knee on the floor in front; shin back toward the feet.
            AimAt(a, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, At(rHip, thigh * 0.92f, -thigh * 0.3f, 0.06f));
            AimAt(a, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, At(B(HumanBodyBones.RightLowerLeg), -0.05f, -shin, 0.07f));
            // Bottom (left) arm: straight out in front at a right angle on the floor, forearm toward the head (palm up).
            AimAt(a, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, At(lSh, upperArm, 0f, 0.04f));
            AimAt(a, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, At(B(HumanBodyBones.LeftLowerArm), 0.04f, 0.3f, 0.04f));
            // Top (right) arm: hand under the cheek (on the floor side of the face), elbow out in front and down.
            Reach(a, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                  At(head, 0.09f, 0.02f, 0.07f), F - 0.6f * U - 0.3f * R);
        }

        /// <summary>Rotate 'bone' so it points from itself to 'child' along 'dir' (world space).</summary>
        static void Aim(Animator a, HumanBodyBones bone, HumanBodyBones child, Vector3 dir)
        {
            var b = a.GetBoneTransform(bone);
            var c = a.GetBoneTransform(child);
            if (b == null || c == null) return;
            var current = c.position - b.position;
            if (current.sqrMagnitude < 1e-8f || dir.sqrMagnitude < 1e-8f) return;
            b.rotation = Quaternion.FromToRotation(current, dir.normalized) * b.rotation;
        }

        /// <summary>Two-bone reach: put the end of upper->lower->end at 'target', bending toward 'pole'.</summary>
        static void Reach(Animator a, HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones end, Vector3 target, Vector3 pole)
        {
            var s = a.GetBoneTransform(upper); var m = a.GetBoneTransform(lower); var e = a.GetBoneTransform(end);
            if (s == null || m == null || e == null) return;
            float la = Vector3.Distance(s.position, m.position), lb = Vector3.Distance(m.position, e.position);
            var toT = target - s.position;
            var d = Mathf.Clamp(toT.magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
            var dir = toT.normalized;
            var x = (la * la + d * d - lb * lb) / (2f * d);
            var h = Mathf.Sqrt(Mathf.Max(0f, la * la - x * x));
            var bend = Vector3.ProjectOnPlane(pole, dir).normalized;
            var elbow = s.position + dir * x + bend * h;
            AimAt(a, upper, lower, elbow);
            AimAt(a, lower, end, s.position + dir * d);
        }

        static void AimAt(Animator a, HumanBodyBones bone, HumanBodyBones child, Vector3 target)
        {
            var b = a.GetBoneTransform(bone);
            if (b != null) Aim(a, bone, child, target - b.position);
        }

        /// <summary>Highest point of the model's surface (holder space) within 'radius' of (x, z) - e.g. the chest top.</summary>
        public static float SurfaceHeight(GameObject holder, float x, float z, float radius)
        {
            var best = float.NegativeInfinity;
            foreach (var smr in holder.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var mesh = new Mesh();
                smr.BakeMesh(mesh, true);
                var m = holder.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                foreach (var v in mesh.vertices)
                {
                    var p = m.MultiplyPoint3x4(v);
                    if ((p.x - x) * (p.x - x) + (p.z - z) * (p.z - z) <= radius * radius && p.y > best) best = p.y;
                }
                if (Application.isPlaying) Object.Destroy(mesh); else Object.DestroyImmediate(mesh);
            }
            return best;
        }

        static Dictionary<string, int> s_MuscleIndex;
        static int MuscleIndex(string name)
        {
            if (s_MuscleIndex == null)
            {
                s_MuscleIndex = new Dictionary<string, int>();
                for (var i = 0; i < HumanTrait.MuscleCount; i++) s_MuscleIndex[HumanTrait.MuscleName[i]] = i;
            }
            return s_MuscleIndex.TryGetValue(name, out var idx) ? idx : -1;
        }

        static readonly Dictionary<string, float> Supine = new Dictionary<string, float>
        {
            // Arms down along the sides, palms in, legs straight.
            { "Left Arm Down-Up", -0.75f }, { "Right Arm Down-Up", -0.75f },
            { "Left Forearm Stretch", 0.9f }, { "Right Forearm Stretch", 0.9f },
            { "Left Upper Leg In-Out", 0.05f }, { "Right Upper Leg In-Out", 0.05f },
            { "Left Lower Leg Stretch", 1f }, { "Right Lower Leg Stretch", 1f },
        };

        static readonly Dictionary<string, float> Recovery = new Dictionary<string, float>
        {
            // Head tilted back a little to keep the airway open (limbs are aimed in AimRecoveryLimbs).
            { "Head Nod Down-Up", 0.3f }, { "Neck Nod Down-Up", 0.2f },
        };
    }
}
