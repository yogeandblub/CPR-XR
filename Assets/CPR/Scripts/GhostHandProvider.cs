using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Feeds a recorded hand pose into Meta's OVRSkeleton / OVRMesh / OVRMeshRenderer in place of
    /// live hand tracking, so the ghost hand is Meta's own hand model held in the recorded pose.
    /// </summary>
    public class GhostHandProvider : MonoBehaviour,
        OVRSkeleton.IOVRSkeletonDataProvider, OVRMesh.IOVRMeshDataProvider, OVRMeshRenderer.IOVRMeshRendererDataProvider
    {
        OVRSkeleton.SkeletonType m_SkeletonType;
        OVRMesh.MeshType m_MeshType;
        OVRPlugin.Quatf[] m_Rotations;
        OVRPlugin.Vector3f[] m_Translations;
        float m_RootScale = 1f;
        int m_Changed;

        public void SetPose(HandPoseEntry e)
        {
            m_SkeletonType = (OVRSkeleton.SkeletonType)e.skeletonType;
            m_MeshType = (OVRMesh.MeshType)e.meshType;
            m_RootScale = e.rootScale <= 0f ? 1f : e.rootScale;

            m_Rotations = new OVRPlugin.Quatf[e.boneRotations.Length / 4];
            for (var i = 0; i < m_Rotations.Length; i++)
                m_Rotations[i] = new OVRPlugin.Quatf { x = e.boneRotations[4 * i], y = e.boneRotations[4 * i + 1], z = e.boneRotations[4 * i + 2], w = e.boneRotations[4 * i + 3] };

            if (e.boneTranslations != null && e.boneTranslations.Length > 0)
            {
                m_Translations = new OVRPlugin.Vector3f[e.boneTranslations.Length / 3];
                for (var i = 0; i < m_Translations.Length; i++)
                    m_Translations[i] = new OVRPlugin.Vector3f { x = e.boneTranslations[3 * i], y = e.boneTranslations[3 * i + 1], z = e.boneTranslations[3 * i + 2] };
            }
            else m_Translations = null;
            m_Changed++;
        }

        public OVRSkeleton.SkeletonType GetSkeletonType() => m_SkeletonType;
        public OVRMesh.MeshType GetMeshType() => m_MeshType;

        public OVRSkeleton.SkeletonPoseData GetSkeletonPoseData() => new OVRSkeleton.SkeletonPoseData
        {
            RootPose = new OVRPlugin.Posef { Orientation = new OVRPlugin.Quatf { w = 1f } }, // wrist placement is our transform
            RootScale = m_RootScale,
            BoneRotations = m_Rotations,
            BoneTranslations = m_Translations,
            IsDataValid = m_Rotations != null,
            IsDataHighConfidence = m_Rotations != null,
            SkeletonChangedCount = m_Changed,
        };

        public OVRMeshRenderer.MeshRendererData GetMeshRendererData() => new OVRMeshRenderer.MeshRendererData
        {
            IsDataValid = m_Rotations != null,
            IsDataHighConfidence = m_Rotations != null,
            ShouldUseSystemGestureMaterial = false,
        };
    }
}
