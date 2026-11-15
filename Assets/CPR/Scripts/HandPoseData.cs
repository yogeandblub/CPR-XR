using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// A recorded CPR hand pose for one patient class: the finger joint rotations of each hand
    /// (exactly as Meta's hand tracking reports them) plus where each wrist sits relative to the
    /// compression disc. Replayed by <see cref="GhostHands"/> on every disc.
    ///
    /// Disc frame: origin = top center of the disc, +Y = up, +Z = from the rescuer toward the patient.
    /// </summary>
    [Serializable]
    public class HandPoseFile
    {
        public string patientClass;        // "Adult" / "Child" / "Infant"
        public string recordedAt;
        public List<HandPoseEntry> hands = new List<HandPoseEntry>();

        public HandPoseEntry Get(bool left) => hands.Find(h => h.isLeft == left);

        // ---------- storage ----------

        /// <summary>Recordings made on the headset: /sdcard/Android/data/&lt;app&gt;/files/hand-poses/</summary>
        public static string DeviceFolder => Path.Combine(Application.persistentDataPath, "hand-poses");
        /// <summary>Recordings baked into the project (copied from the headset): Resources/CPRHandPoses/</summary>
        public const string ResourcesFolder = "CPRHandPoses";

        public static string FileName(PoseOverlayTest.BodyClass c) => $"{c}.json";

        public void SaveToDevice()
        {
            Directory.CreateDirectory(DeviceFolder);
            File.WriteAllText(Path.Combine(DeviceFolder, FileName(Parse(patientClass))), JsonUtility.ToJson(this, true));
        }

        /// <summary>Delete the headset's recording for this class (the project's baked one, if any, is untouched).</summary>
        public static void DeleteFromDevice(PoseOverlayTest.BodyClass c)
        {
            try { var path = Path.Combine(DeviceFolder, FileName(c)); if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Debug.LogWarning($"CPRHands: could not delete pose for {c}: {e.Message}"); }
        }

        /// <summary>Newest recording on the headset first, otherwise the one baked into the project.</summary>
        public static HandPoseFile Load(PoseOverlayTest.BodyClass c)
        {
            try
            {
                var path = Path.Combine(DeviceFolder, FileName(c));
                if (File.Exists(path)) return JsonUtility.FromJson<HandPoseFile>(File.ReadAllText(path));
            }
            catch (Exception e) { Debug.LogWarning($"CPRHands: could not read device pose for {c}: {e.Message}"); }

            var asset = Resources.Load<TextAsset>($"{ResourcesFolder}/{c}");
            return asset != null ? JsonUtility.FromJson<HandPoseFile>(asset.text) : null;
        }

        static PoseOverlayTest.BodyClass Parse(string s) =>
            Enum.TryParse<PoseOverlayTest.BodyClass>(s, out var c) ? c : PoseOverlayTest.BodyClass.Adult;
    }

    [Serializable]
    public class HandPoseEntry
    {
        public bool isLeft;
        public int skeletonType;           // OVRSkeleton.SkeletonType (OVR or OpenXR hand)
        public int meshType;               // OVRMesh.MeshType
        public Vector3 wristPosition;      // in the disc frame
        public Quaternion wristRotation;   // in the disc frame
        public float rootScale = 1f;
        public float[] boneRotations;      // x,y,z,w per bone, raw tracking data
        public float[] boneTranslations;   // x,y,z per bone (OpenXR hands), may be empty

        public static HandPoseEntry Capture(OVRHand hand, Transform wrist, Matrix4x4 worldToDisc, Quaternion discRotation)
        {
            var provider = (OVRSkeleton.IOVRSkeletonDataProvider)hand;
            var meshProvider = (OVRMesh.IOVRMeshDataProvider)hand;
            var data = provider.GetSkeletonPoseData();
            if (!data.IsDataValid || data.BoneRotations == null) return null;

            var type = provider.GetSkeletonType();
            var e = new HandPoseEntry
            {
                isLeft = type == OVRSkeleton.SkeletonType.HandLeft || type == OVRSkeleton.SkeletonType.XRHandLeft,
                skeletonType = (int)type,
                meshType = (int)meshProvider.GetMeshType(),
                wristPosition = worldToDisc.MultiplyPoint3x4(wrist.position),
                wristRotation = Quaternion.Inverse(discRotation) * wrist.rotation,
                rootScale = data.RootScale,
                boneRotations = new float[data.BoneRotations.Length * 4],
                boneTranslations = data.BoneTranslations != null ? new float[data.BoneTranslations.Length * 3] : Array.Empty<float>(),
            };
            for (var i = 0; i < data.BoneRotations.Length; i++)
            {
                var q = data.BoneRotations[i];
                e.boneRotations[4 * i] = q.x; e.boneRotations[4 * i + 1] = q.y; e.boneRotations[4 * i + 2] = q.z; e.boneRotations[4 * i + 3] = q.w;
            }
            if (data.BoneTranslations != null)
                for (var i = 0; i < data.BoneTranslations.Length; i++)
                {
                    var t = data.BoneTranslations[i];
                    e.boneTranslations[3 * i] = t.x; e.boneTranslations[3 * i + 1] = t.y; e.boneTranslations[3 * i + 2] = t.z;
                }
            return e;
        }
    }
}
