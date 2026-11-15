using System;
using System.Collections.Generic;
using Meta.XR;
using Unity.InferenceEngine;
using Unity.Mathematics;
using UnityEngine;

namespace CPR
{
    /// <summary>
    /// Finds and tracks a person with BlazePose on the passthrough camera, and only SHOWS them
    /// when they are lying down near the floor (the CPR patient). Standing people are tracked
    /// internally but never drawn.
    ///
    /// Provides: a stable compression point on the chest, the floor height, body length and
    /// Infant/Child/Adult class (estimated, or forced by <see cref="mode"/> for demos), and an
    /// aura around the patient. Can be frozen to stop all inference (battery).
    ///
    /// Editor testing: assign <see cref="editorTestImage"/> (a photo of someone lying down) and
    /// press Play. The image replaces the headset camera, and a virtual floor replaces depth.
    /// Log tag: "CPRPose".
    /// </summary>
    public class PoseOverlayTest : MonoBehaviour
    {
        public enum BodyClass { Unknown, Infant, Child, Adult }
        public enum PatientMode { Auto, Adult, Child, Infant }

        [Header("Scene references")]
        public PassthroughCameraAccess cameraAccess;
        public EnvironmentRaycastManager raycastManager;

        [Header("BlazePose")]
        public ModelAsset poseDetector;
        public ModelAsset poseLandmarker;
        public TextAsset anchorsCSV;
        [Range(0, 1)] public float detectorScoreThreshold = 0.75f;
        [Range(0, 1)] public float presenceThreshold = 0.5f;

        [Header("Patient mode")]
        [Tooltip("Auto = use the size estimate. Adult/Child/Infant = force it (demos, or rescuer correction).")]
        public PatientMode mode = PatientMode.Auto;

        [Header("Lying-person filter")]
        [Tooltip("Torso must be within this many degrees of horizontal.")]
        public float maxTorsoTiltDegrees = 35f;
        [Tooltip("Torso midpoint must be at most this high above the floor (m).")]
        public float maxTorsoHeight = 0.6f;
        [Tooltip("Minimum joints found (incl. both shoulders and hips) to trust a pose.")]
        public int minVisibleJoints = 12;
        [Tooltip("Pose must look like 'lying' for this long before anything is shown (s).")]
        public float confirmSeconds = 0.5f;
        [Tooltip("Pose must look NOT lying for this long before the patient is dropped (s). Stops flicker.")]
        public float loseSeconds = 0.5f;
        [Tooltip("Fallback floor height if depth can't find it. In Editor test mode this IS the floor.")]
        public float floorY = 0f;

        [Header("Body size (estimate - rescuer should confirm)")]
        [Tooltip("Crown-to-heel length below this (m) = Infant.")]
        public float infantMaxLength = 0.8f;
        [Tooltip("Crown-to-heel length below this (m) = Child; above = Adult.")]
        public float childMaxLength = 1.45f;
        [Tooltip("Calibration: estimate = raw * scale + offset. Fit from tests with people of known height.")]
        public float lengthScale = 1f;
        public float lengthOffset = 0f;

        [Header("Compression point: distance from the top of the head, as a fraction of body length")]
        [Tooltip("Adult: center of the chest ~25% of height down from the crown (~2 head-lengths).")]
        [Range(0.1f, 0.5f)] public float adultCrownFraction = 0.25f;
        [Tooltip("Child: center of the chest (children have relatively larger heads).")]
        [Range(0.1f, 0.5f)] public float childCrownFraction = 0.28f;
        [Tooltip("Infant: just below the nipple line (head is ~1/4 of an infant's length).")]
        [Range(0.1f, 0.5f)] public float infantCrownFraction = 0.33f;
        [Tooltip("Fallback before the full body has been measured: fraction of shoulder-mid -> hip-mid.")]
        [Range(0, 0.6f)] public float fallbackTorsoFraction = 0.25f;
        [Tooltip("How many frames are averaged for the compression point.")]
        public int chestAverageFrames = 15;

        [Header("Power")]
        [Tooltip("Wait between detector attempts while nobody is found (s). Higher = less battery.")]
        public float searchInterval = 0.33f;
        [Tooltip("Minimum time between pose updates while tracking (s).")]
        public float trackInterval = 0.05f;

        [Header("Editor test (Play mode on the Mac)")]
        public Texture editorTestImage;
        [Tooltip("Virtual camera standing in for the headset camera: put it where the photo was taken from.")]
        public Camera editorTestCamera;

        [Header("Visuals")]
        [Tooltip("Soft glow material (CPR/AuraGlow) used for the patient outline.")]
        public Material auraMaterial;
        [Tooltip("Soft glow around a real (camera-detected) patient. Off by default.")]
        public bool showAura = false;
        [Tooltip("Debug: draw the joints and bones as well.")]
        public bool showSkeleton = false;
        public Material jointMaterial;
        public Material boneMaterial;
        [Tooltip("Unused since the CompressionDisc took over; kept so old scenes still load.")]
        public Material chestMaterial;
        public float jointSize = 0.04f;

        // ---- Public state for other CPR components ----
        public bool IsLyingConfirmed { get; private set; }
        /// <summary>True while a virtual (practice mannequin) patient replaces the camera.</summary>
        public bool IsVirtual { get; private set; }
        public bool IsFrozen { get; private set; }
        public BodyClass EstimatedClass { get; private set; }
        public float EstimatedLength { get; private set; }
        /// <summary>Spread of the length samples (m): half the interquartile range. Smaller = more consistent.</summary>
        public float EstimatedLengthSpread { get; private set; }
        public BodyClass EffectiveClass => mode switch
        {
            PatientMode.Adult => BodyClass.Adult,
            PatientMode.Child => BodyClass.Child,
            PatientMode.Infant => BodyClass.Infant,
            _ => EstimatedClass,
        };
        /// <summary>Floor height under the patient (depth-measured when possible).</summary>
        public float FloorY => IsVirtual || !UseEditorSource ? (m_HasFloor ? m_FloorY : floorY) : floorY;
        /// <summary>Direction along the body, shoulders -> hips, flat on the floor.</summary>
        public Vector3 BodyAxis { get; private set; } = Vector3.forward;

        const int k_NumAnchors = 2254;
        const int k_NumKeypoints = 33;
        const int k_DetectorSize = 224;
        const int k_LandmarkerSize = 256;

        // BlazePose landmark indices
        const int NOSE = 0, L_EAR = 7, R_EAR = 8, L_SHOULDER = 11, R_SHOULDER = 12, L_ELBOW = 13, R_ELBOW = 14,
                  L_WRIST = 15, R_WRIST = 16, L_HIP = 23, R_HIP = 24, L_KNEE = 25, R_KNEE = 26,
                  L_ANKLE = 27, R_ANKLE = 28, L_HEEL = 29, R_HEEL = 30, L_FOOT = 31, R_FOOT = 32;
        const int AUX_CENTER = 33, AUX_SCALE = 34; // ROI helper points output by the landmark model

        static readonly int[,] k_Bones =
        {
            {11,12},{11,13},{13,15},{12,14},{14,16},{11,23},{12,24},{23,24},
            {23,25},{25,27},{24,26},{26,28},{27,29},{29,31},{27,31},{28,30},{30,32},{28,32},
            {15,17},{15,19},{15,21},{17,19},{16,18},{16,20},{16,22},{18,20},
            {0,1},{1,2},{2,3},{3,7},{0,4},{4,5},{5,6},{6,8},{9,10},
        };

        // Aura limbs: joint a -> joint b, radius for an adult (scaled by body size).
        static readonly (int a, int b, float r)[] k_AuraLimbs =
        {
            (L_SHOULDER, L_ELBOW, 0.055f), (L_ELBOW, L_WRIST, 0.045f),
            (R_SHOULDER, R_ELBOW, 0.055f), (R_ELBOW, R_WRIST, 0.045f),
            (L_HIP, L_KNEE, 0.085f), (L_KNEE, L_ANKLE, 0.06f), (L_ANKLE, L_FOOT, 0.04f),
            (R_HIP, R_KNEE, 0.085f), (R_KNEE, R_ANKLE, 0.06f), (R_ANKLE, R_FOOT, 0.04f),
        };
        const float k_AuraTorsoRadius = 0.17f, k_AuraHeadRadius = 0.11f;

        float[,] m_Anchors;
        Worker m_Detector, m_Landmarker;
        Tensor<float> m_DetectorInput, m_LandmarkerInput;

        // Tracking state: when true we reuse last frame's crop and skip the detector.
        bool m_HasCrop;
        float2 m_CropCenter, m_CropScalePoint;
        float m_LyingSince = -1f;
        readonly List<float> m_LengthSamples = new List<float>();
        float m_FloorY; bool m_HasFloor;
        Vector3 m_Crown; bool m_HasCrown;
        bool m_FreezeRequested; float m_FreezeRequestedAt;
        float m_VirtualSurfaceOffset;
        float m_NotLyingSince = -1f;
        Vector3 m_CameraPos;

        // Compression point: averaged over recent frames so it doesn't jump.
        readonly Queue<Vector3> m_ChestSamples = new Queue<Vector3>();
        Vector3 m_ChestSum, m_ChestPoint;
        float m_LastChestFraction = -1f;

        Transform m_VisualRoot, m_SkeletonRoot, m_AuraRoot;
        Transform[] m_Joints;
        Vector3[] m_JointPos;
        bool[] m_JointValid;
        LineRenderer[] m_BoneLines;
        Transform[] m_AuraLimbs;
        Transform m_AuraTorso, m_AuraHead;
        TextMesh m_Label;

        // stats
        int m_Frames, m_DetectorRuns, m_VisibleSum;
        float m_StatsStart, m_LatencySum;

        bool UseEditorSource => Application.isEditor && editorTestImage != null && editorTestCamera != null;

        // Placement follows the body that is actually lying there (the estimate), not the demo mode:
        // forcing "Infant" on an adult teammate must not move the target onto their stomach.
        float CrownFraction => EstimatedClass switch
        {
            BodyClass.Infant => infantCrownFraction,
            BodyClass.Child => childCrownFraction,
            _ => adultCrownFraction,
        };

        async void Start()
        {
            BuildVisuals();
            m_Anchors = BlazeUtils.LoadAnchors(anchorsCSV.text, k_NumAnchors);

            // Detector + argmax post-processing (from Unity's sample) so only the best pose is read back.
            var detectorModel = ModelLoader.Load(poseDetector);
            var graph = new FunctionalGraph();
            var input = graph.AddInput(detectorModel, 0);
            var outputs = Functional.Forward(detectorModel, input);
            var best = BlazeUtils.ArgMaxFiltering(outputs[0], outputs[1]);
            detectorModel = graph.Compile(best.Item1, best.Item2, best.Item3);

            m_Detector = new Worker(detectorModel, BackendType.GPUCompute);
            m_Landmarker = new Worker(ModelLoader.Load(poseLandmarker), BackendType.GPUCompute);
            m_DetectorInput = new Tensor<float>(new TensorShape(1, k_DetectorSize, k_DetectorSize, 3));
            m_LandmarkerInput = new Tensor<float>(new TensorShape(1, k_LandmarkerSize, k_LandmarkerSize, 3));

            Debug.Log($"CPRPose: started, landmarker={poseLandmarker.name}, mode={mode}" + (UseEditorSource ? ", Editor test mode (photo + virtual floor)" : ""));

            try
            {
                var token = destroyCancellationToken;
                while (!token.IsCancellationRequested)
                {
                    if (IsFrozen || IsVirtual || !IsSourceReady())
                    {
                        await Awaitable.NextFrameAsync(token);
                        continue;
                    }

                    var found = await RunPose();
                    var wait = found ? trackInterval : searchInterval;
                    if (wait > 0f) await Awaitable.WaitForSecondsAsync(wait, token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                m_Detector?.Dispose();
                m_Landmarker?.Dispose();
                m_DetectorInput?.Dispose();
                m_LandmarkerInput?.Dispose();
            }
        }

        // ---------- Frame source (headset camera, or Editor test image) ----------

        bool IsSourceReady() => UseEditorSource || (cameraAccess != null && cameraAccess.IsPlaying);

        Texture GetFrame() => UseEditorSource ? editorTestImage : cameraAccess.GetTexture();

        Pose GetCameraPose() => UseEditorSource
            ? new Pose(editorTestCamera.transform.position, editorTestCamera.transform.rotation)
            : cameraAccess.GetCameraPose();

        bool TryRaycastViewport(float2 viewport, Pose cameraPose, out Vector3 world)
        {
            world = default;
            if (viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) return false;

            Ray ray;
            if (UseEditorSource)
            {
                editorTestCamera.aspect = (float)editorTestImage.width / editorTestImage.height;
                ray = editorTestCamera.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
            }
            else
            {
                ray = cameraAccess.ViewportPointToRay(new Vector2(viewport.x, viewport.y), cameraPose);
            }
            return TryRaycastWorld(ray, out world);
        }

        bool TryRaycastWorld(Ray ray, out Vector3 world, float maxDistance = 100f)
        {
            world = default;
            if (UseEditorSource)
            {
                if (!new Plane(Vector3.up, new Vector3(0f, floorY, 0f)).Raycast(ray, out var d) || d > maxDistance) return false;
                world = ray.GetPoint(d);
                return true;
            }
            if (raycastManager == null || !raycastManager.Raycast(ray, out var hit, maxDistance)) return false;
            world = hit.point;
            return true;
        }

        // ---------- Inference ----------

        /// <returns>true if a person (lying or not) is currently being tracked.</returns>
        async Awaitable<bool> RunPose()
        {
            var t0 = Time.realtimeSinceStartup;
            var cameraPose = GetCameraPose(); // pose at capture, used for every ray this frame
            m_CameraPos = cameraPose.position;
            var texture = GetFrame();
            var texSize = new float2(texture.width, texture.height);

            // Stage 1: detector, only when we have no crop to reuse.
            if (!m_HasCrop)
            {
                m_DetectorRuns++;
                var size = math.max(texture.width, texture.height);
                var scale = size / (float)k_DetectorSize;
                var M = BlazeUtils.mul(
                    BlazeUtils.TranslationMatrix(0.5f * (texSize + new float2(-size, size))),
                    BlazeUtils.ScaleMatrix(new float2(scale, -scale)));
                BlazeUtils.SampleImageAffine(texture, m_DetectorInput, M);
                m_Detector.Schedule(m_DetectorInput);

                // Request all readbacks before awaiting any, so they complete together.
                var idxReq = (m_Detector.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
                var scoreReq = (m_Detector.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync();
                var boxReq = (m_Detector.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync();
                using var idxT = await idxReq;
                using var scoreT = await scoreReq;
                using var boxT = await boxReq;

                if (scoreT[0] < detectorScoreThreshold)
                {
                    OnNoPerson();
                    Tick(t0, 0);
                    return false;
                }

                var idx = idxT[0];
                var anchor = k_DetectorSize * new float2(m_Anchors[idx, 0], m_Anchors[idx, 1]);
                m_CropCenter = BlazeUtils.mul(M, anchor + new float2(boxT[0, 0, 4], boxT[0, 0, 5]));
                m_CropScalePoint = BlazeUtils.mul(M, anchor + new float2(boxT[0, 0, 6], boxT[0, 0, 7]));
            }

            // Stage 2: landmarks inside a rotated square crop around the body.
            var M2 = CropMatrix(m_CropCenter, m_CropScalePoint);
            BlazeUtils.SampleImageAffine(texture, m_LandmarkerInput, M2);
            m_Landmarker.Schedule(m_LandmarkerInput);

            var lmReq = (m_Landmarker.PeekOutput("Identity") as Tensor<float>).ReadbackAndCloneAsync();       // (1,195) = 39 x [x,y,z,vis,presence]
            var flagReq = (m_Landmarker.PeekOutput("Identity_1") as Tensor<float>).ReadbackAndCloneAsync();   // pose presence
            using var lm = await lmReq;
            using var flagT = await flagReq;

            if (ToProbability(flagT[0]) < presenceThreshold)
            {
                OnNoPerson();
                Tick(t0, 0);
                return false;
            }

            // Next frame's crop comes from this frame's auxiliary ROI landmarks: no detector needed.
            m_CropCenter = BlazeUtils.mul(M2, new float2(lm[5 * AUX_CENTER], lm[5 * AUX_CENTER + 1]));
            m_CropScalePoint = BlazeUtils.mul(M2, new float2(lm[5 * AUX_SCALE], lm[5 * AUX_SCALE + 1]));
            m_HasCrop = true;

            // Place each joint in 3D: image point -> camera ray -> depth hit.
            var visible = 0;
            for (var i = 0; i < k_NumKeypoints; i++)
            {
                var imagePt = BlazeUtils.mul(M2, new float2(lm[5 * i], lm[5 * i + 1]));
                var world = Vector3.zero;
                var ok = lm[5 * i + 3] > 0.5f && lm[5 * i + 4] > 0.5f;
                if (ok) ok = TryRaycastViewport(imagePt / texSize, cameraPose, out world);
                m_JointValid[i] = ok;
                if (ok) { m_JointPos[i] = world; visible++; }
            }

            UpdateLyingState(visible);
            if (IsLyingConfirmed && m_NotLyingSince < 0f) // a good frame (not just inside the grace period)
            {
                UpdateBodySize();
                UpdateCompressionPoint();
            }
            UpdateVisuals();
            CheckFreezeRequest();
            Tick(t0, visible);
            return true;
        }

        float2x3 CropMatrix(float2 center, float2 scalePoint)
        {
            var delta = scalePoint - center;
            var radius = 1.25f * math.length(delta);
            var theta = math.atan2(delta.y, delta.x);
            var origin = new float2(0.5f * k_LandmarkerSize);
            var s = radius / (0.5f * k_LandmarkerSize);
            return BlazeUtils.mul(BlazeUtils.mul(BlazeUtils.mul(
                BlazeUtils.TranslationMatrix(center),
                BlazeUtils.ScaleMatrix(new float2(s, -s))),
                BlazeUtils.RotationMatrix(0.5f * Mathf.PI - theta)),
                BlazeUtils.TranslationMatrix(-origin));
        }

        // ---------- Lying filter + floor ----------

        bool TorsoVisible => m_JointValid[L_SHOULDER] && m_JointValid[R_SHOULDER] && m_JointValid[L_HIP] && m_JointValid[R_HIP];
        Vector3 ShoulderMid => 0.5f * (m_JointPos[L_SHOULDER] + m_JointPos[R_SHOULDER]);
        Vector3 HipMid => 0.5f * (m_JointPos[L_HIP] + m_JointPos[R_HIP]);

        void UpdateLyingState(int visible)
        {
            var lyingNow = false;
            if (TorsoVisible && visible >= minVisibleJoints)
            {
                var torso = HipMid - ShoulderMid;
                var flat = Vector3.ProjectOnPlane(torso, Vector3.up);
                var tilt = Vector3.Angle(torso, flat);
                if (flat.sqrMagnitude > 1e-4f) BodyAxis = flat.normalized;
                UpdateFloor();
                var height = 0.5f * (ShoulderMid.y + HipMid.y) - FloorY;
                lyingNow = torso.magnitude > 0.1f && tilt <= maxTorsoTiltDegrees && height <= maxTorsoHeight;
            }

            if (!lyingNow)
            {
                m_LyingSince = -1f;
                LoseAfterGrace();
                return;
            }

            m_NotLyingSince = -1f;
            if (m_LyingSince < 0f) m_LyingSince = Time.realtimeSinceStartup;
            if (!IsLyingConfirmed && Time.realtimeSinceStartup - m_LyingSince >= confirmSeconds)
            {
                IsLyingConfirmed = true;
                m_LengthSamples.Clear();
                ClearChest();
                Debug.Log($"CPRPose: lying person confirmed (floor y={FloorY:F2})");
            }
        }

        /// <summary>Measure the floor with a depth ray straight down beside the torso (not onto the body).</summary>
        void UpdateFloor()
        {
            if (UseEditorSource) return;
            var across = Vector3.Cross(Vector3.up, BodyAxis).normalized;
            var side = Vector3.Dot(m_CameraPos - ShoulderMid, across) >= 0 ? across : -across; // the side we can see
            var probe = 0.5f * (ShoulderMid + HipMid) + side * 0.5f;
            if (!TryRaycastWorld(new Ray(probe + Vector3.up * 0.6f, Vector3.down), out var hit, 2.5f)) return;
            if (hit.y > Mathf.Min(ShoulderMid.y, HipMid.y)) return; // hit something above the body: not the floor
            m_FloorY = m_HasFloor ? Mathf.Lerp(m_FloorY, hit.y, 0.2f) : hit.y;
            m_HasFloor = true;
        }

        /// <summary>Drop a confirmed patient only after they've looked "not lying" for loseSeconds.</summary>
        void LoseAfterGrace(bool immediate = false)
        {
            if (!IsLyingConfirmed) return;
            if (m_NotLyingSince < 0f) m_NotLyingSince = Time.realtimeSinceStartup;
            if (!immediate && Time.realtimeSinceStartup - m_NotLyingSince < loseSeconds) return; // keep last good state
            Debug.Log("CPRPose: lying person lost");
            IsLyingConfirmed = false;
            m_NotLyingSince = -1f;
            m_FreezeRequested = false;
            ClearChest();
        }

        void OnNoPerson(bool immediate = false)
        {
            m_HasCrop = false; // run the detector next time
            m_LyingSince = -1f;
            LoseAfterGrace(immediate);
            if (!IsLyingConfirmed) for (var i = 0; i < k_NumKeypoints; i++) m_JointValid[i] = false;
            UpdateVisuals();
        }

        // ---------- Body size ----------

        void UpdateBodySize()
        {
            var shoulderMid = ShoulderMid;
            var torso = Vector3.Distance(shoulderMid, HipMid);

            // Head: ear midpoint (seen well from above), then extend past it to estimate the crown.
            var hasHead = Mid(L_EAR, R_EAR, out var head) || TryJoint(NOSE, out head);
            var hasKnee = Mid(L_KNEE, R_KNEE, out var knee);
            var hasFoot = Mid(L_HEEL, R_HEEL, out var foot) || Mid(L_ANKLE, R_ANKLE, out foot);

            if (!hasHead || !hasKnee || !hasFoot) return; // only measure when the whole body is visible

            var neck = Vector3.Distance(head, shoulderMid);
            var crownExtra = 0.65f * neck; // ear -> crown is ~0.65x ear -> shoulder line (adult proportions)
            var raw = crownExtra + neck + torso + Vector3.Distance(HipMid, knee) + Vector3.Distance(knee, foot);
            var awayFromBody = (head - shoulderMid).normalized;
            m_Crown = head + awayFromBody * crownExtra;
            m_HasCrown = true;

            m_LengthSamples.Add(raw * lengthScale + lengthOffset);
            if (m_LengthSamples.Count > 150) m_LengthSamples.RemoveAt(0);

            // Median + half-IQR: robust to the odd bad frame.
            var sorted = new List<float>(m_LengthSamples);
            sorted.Sort();
            EstimatedLength = sorted[sorted.Count / 2];
            EstimatedLengthSpread = 0.5f * (sorted[(sorted.Count * 3) / 4] - sorted[sorted.Count / 4]);
            EstimatedClass = EstimatedLength < infantMaxLength ? BodyClass.Infant
                           : EstimatedLength < childMaxLength ? BodyClass.Child
                           : BodyClass.Adult;
        }

        bool TryJoint(int i, out Vector3 p)
        {
            p = m_JointPos[i];
            return m_JointValid[i];
        }

        bool Mid(int a, int b, out Vector3 mid)
        {
            mid = default;
            if (m_JointValid[a] && m_JointValid[b]) { mid = 0.5f * (m_JointPos[a] + m_JointPos[b]); return true; }
            if (m_JointValid[a]) { mid = m_JointPos[a]; return true; }
            if (m_JointValid[b]) { mid = m_JointPos[b]; return true; }
            return false;
        }

        // ---------- Compression point ----------

        void UpdateCompressionPoint()
        {
            Vector3 target;
            var useCrown = m_HasCrown && m_LengthSamples.Count >= 5;
            var fraction = useCrown ? CrownFraction : -fallbackTorsoFraction; // sign keeps the two methods apart
            if (!Mathf.Approximately(fraction, m_LastChestFraction)) { ClearChest(); m_LastChestFraction = fraction; }

            if (useCrown)
            {
                // Center of the chest: a fixed fraction of body length down from the crown, along the
                // head -> hips line (shoulder points shift with arm position; the crown doesn't).
                var down = (HipMid - m_Crown).normalized;
                target = m_Crown + down * (CrownFraction * EstimatedLength);
            }
            else
            {
                target = Vector3.Lerp(ShoulderMid, HipMid, fallbackTorsoFraction);
            }
            // ...then onto the chest surface along the camera's line of sight.
            var dir = target - m_CameraPos;
            Vector3 surface;
            if (IsVirtual) surface = target + Vector3.up * m_VirtualSurfaceOffset; // mannequin: chest top, no depth ray
            else if (!TryRaycastWorld(new Ray(m_CameraPos, dir.normalized), out surface, dir.magnitude + 0.3f)) surface = target;

            m_ChestSamples.Enqueue(surface);
            m_ChestSum += surface;
            while (m_ChestSamples.Count > Mathf.Max(1, chestAverageFrames)) m_ChestSum -= m_ChestSamples.Dequeue();
            m_ChestPoint = m_ChestSum / m_ChestSamples.Count;
        }

        void ClearChest()
        {
            m_ChestSamples.Clear();
            m_ChestSum = Vector3.zero;
        }

        // ---------- Read access for other CPR components ----------

        /// <summary>World position of a joint, only while a lying person is confirmed.</summary>
        public bool TryGetJoint(int index, out Vector3 position)
        {
            position = default;
            if (!IsLyingConfirmed || m_JointValid == null || index < 0 || index >= k_NumKeypoints || !m_JointValid[index]) return false;
            position = m_JointPos[index];
            return true;
        }

        /// <summary>Averaged compression point on the chest surface (kept while frozen).</summary>
        public bool TryGetCompressionPoint(out Vector3 position)
        {
            position = m_ChestPoint;
            return (IsLyingConfirmed || IsFrozen) && m_ChestSamples.Count > 0;
        }

        [Header("Freeze (battery)")]
        [Tooltip("When a freeze is requested, keep measuring until this many full-body samples...")]
        public int freezeMinSamples = 40;
        [Tooltip("...or this many seconds have passed, whichever comes first.")]
        public float freezeTimeout = 8f;

        /// <summary>Freeze once the body has been measured well enough (or the timeout passes).</summary>
        public void RequestFreeze()
        {
            if (IsFrozen || m_FreezeRequested || IsVirtual) return;
            m_FreezeRequested = true;
            m_FreezeRequestedAt = Time.realtimeSinceStartup;
            Debug.Log($"CPRPose: freeze requested - measuring until {freezeMinSamples} samples or {freezeTimeout:F0}s");
            CheckFreezeRequest();
        }

        void CheckFreezeRequest()
        {
            if (!m_FreezeRequested || IsFrozen) return;
            if (m_LengthSamples.Count >= freezeMinSamples || Time.realtimeSinceStartup - m_FreezeRequestedAt >= freezeTimeout)
            {
                m_FreezeRequested = false;
                SetFrozen(true);
            }
        }

        /// <summary>
        /// Practice mode: drive everything (kneel spot, button disc, ghost hands) from a virtual body
        /// instead of the camera. Call every frame the mannequin moves. Joints are BlazePose indices
        /// at body mid-depth; chestSurfaceOffset lifts the compression point onto the chest top.
        /// </summary>
        public void SetVirtualPatient(Vector3[] joints, BodyClass bodyClass, float length, Vector3 crown, float floor, float chestSurfaceOffset)
        {
            if (!IsVirtual)
            {
                IsVirtual = true;
                ClearChest();
                m_LengthSamples.Clear();
                Debug.Log($"CPRPose: virtual patient ({bodyClass}, {length:F2} m) - camera inference paused");
            }
            for (var i = 0; i < k_NumKeypoints && i < joints.Length; i++) { m_JointPos[i] = joints[i]; m_JointValid[i] = true; }
            EstimatedClass = bodyClass;
            EstimatedLength = length;
            EstimatedLengthSpread = 0f;
            m_Crown = crown; m_HasCrown = true;
            m_FloorY = floor; m_HasFloor = true;
            m_VirtualSurfaceOffset = chestSurfaceOffset;
            var flat = Vector3.ProjectOnPlane(HipMid - ShoulderMid, Vector3.up);
            if (flat.sqrMagnitude > 1e-4f) BodyAxis = flat.normalized;
            IsLyingConfirmed = true;
            m_NotLyingSince = -1f;
            // The mannequin can be dragged around, so recompute the target from scratch each update.
            ClearChest();
            while (m_LengthSamples.Count < 5) m_LengthSamples.Add(length); // enables the crown-based placement
            UpdateCompressionPoint();
            m_VisualRoot.gameObject.SetActive(false); // the mannequin draws its own aura
        }

        /// <summary>Leave practice: forget the virtual body and go back to scanning with the camera.</summary>
        public void ClearVirtualPatient()
        {
            if (!IsVirtual) return;
            IsVirtual = false;
            Debug.Log("CPRPose: virtual patient removed - camera scanning resumes");
            ResetTracking();
        }

        /// <summary>Stop all inference (battery) and keep everything where it is.</summary>
        public void SetFrozen(bool frozen)
        {
            if (IsFrozen == frozen) return;
            IsFrozen = frozen;
            Debug.Log(frozen
                ? $"CPRPose: frozen - inference stopped. Patient {EffectiveClass} (mode {mode}), est. {EstimatedLength:F2} m ±{EstimatedLengthSpread:F2} from {m_LengthSamples.Count} samples"
                : "CPRPose: resumed");
        }

        /// <summary>Forget the current person and search again.</summary>
        public void ResetTracking()
        {
            SetFrozen(false);
            m_LengthSamples.Clear();
            EstimatedClass = BodyClass.Unknown;
            EstimatedLength = EstimatedLengthSpread = 0f;
            m_HasFloor = false;
            m_HasCrown = false;
            m_FreezeRequested = false;
            OnNoPerson(immediate: true);
            Debug.Log("CPRPose: reset");
        }

        /// <summary>Switch Auto -> Adult -> Child -> Infant -> Auto.</summary>
        public void CycleMode()
        {
            mode = (PatientMode)(((int)mode + 1) % 4);
            // Placement depends on the class: recompute from the last known joints (works while frozen too).
            ClearChest();
            if (TorsoVisible) UpdateCompressionPoint();
            Debug.Log($"CPRPose: mode {mode} -> treating patient as {EffectiveClass}");
            UpdateVisuals();
        }

        // The pose flag may come out as a raw logit or already as a probability depending on the export.
        static float ToProbability(float v) => v < 0f || v > 1f ? 1f / (1f + Mathf.Exp(-v)) : v;

        // ---------- Visuals (only for a confirmed lying person) ----------

        void UpdateVisuals()
        {
            var show = (IsLyingConfirmed || IsFrozen) && !IsVirtual;
            m_VisualRoot.gameObject.SetActive(show);
            if (!show) return;

            UpdateAura();

            m_SkeletonRoot.gameObject.SetActive(showSkeleton);
            if (showSkeleton)
            {
                for (var i = 0; i < k_NumKeypoints; i++)
                {
                    m_Joints[i].gameObject.SetActive(m_JointValid[i]);
                    if (m_JointValid[i]) m_Joints[i].position = m_JointPos[i];
                }
                for (var b = 0; b < m_BoneLines.Length; b++)
                {
                    int a = k_Bones[b, 0], c = k_Bones[b, 1];
                    var on = m_JointValid[a] && m_JointValid[c];
                    m_BoneLines[b].enabled = on;
                    if (on)
                    {
                        m_BoneLines[b].SetPosition(0, m_JointPos[a]);
                        m_BoneLines[b].SetPosition(1, m_JointPos[c]);
                    }
                }
            }

            var hasChest = m_ChestSamples.Count > 0;
            m_Label.gameObject.SetActive(hasChest && EffectiveClass != BodyClass.Unknown);
            if (hasChest)
            {
                m_Label.text = mode == PatientMode.Auto
                    ? $"{EffectiveClass} (est. {EstimatedLength:F2} m)"
                    : $"{EffectiveClass} (demo mode)";
                m_Label.transform.position = m_ChestPoint + Vector3.up * 0.4f;
            }
        }

        void UpdateAura()
        {
            m_AuraRoot.gameObject.SetActive(showAura);
            if (!showAura) return;
            if (!TorsoVisible) return; // keep the last aura if the torso blinks out for a frame
            var size = Mathf.Clamp(Vector3.Distance(ShoulderMid, HipMid) / 0.5f, 0.3f, 1.3f); // 1 = adult torso

            PlaceCapsule(m_AuraTorso, ShoulderMid, HipMid, k_AuraTorsoRadius * size, true);
            m_AuraTorso.gameObject.SetActive(true);

            var hasHead = Mid(L_EAR, R_EAR, out var head) || TryJoint(NOSE, out head);
            m_AuraHead.gameObject.SetActive(hasHead);
            if (hasHead)
            {
                var r = k_AuraHeadRadius * size;
                m_AuraHead.position = head + (head - ShoulderMid).normalized * (0.3f * r) - Vector3.up * (0.5f * r);
                m_AuraHead.localScale = Vector3.one * (2f * r);
            }

            for (var i = 0; i < k_AuraLimbs.Length; i++)
            {
                var (a, b, r) = k_AuraLimbs[i];
                var on = m_JointValid[a] && m_JointValid[b];
                m_AuraLimbs[i].gameObject.SetActive(on);
                if (on) PlaceCapsule(m_AuraLimbs[i], m_JointPos[a], m_JointPos[b], r * size, false);
            }
        }

        /// <summary>Stretch a Unity capsule (height 2, radius 0.5, along Y) between two points.</summary>
        static void PlaceCapsule(Transform t, Vector3 a, Vector3 b, float radius, bool flatten)
        {
            // Depth hits land on the top of the body, so sink the glow to wrap the limb.
            var sink = Vector3.up * (0.5f * radius);
            var dir = b - a;
            var len = dir.magnitude;
            if (len < 1e-3f) return;
            var axis = dir / len;
            t.position = 0.5f * (a + b) - sink;
            var thick = 2f * radius;

            // Capsule's local Y runs along the limb.
            var widthAxis = Vector3.Cross(Vector3.up, axis);
            if (flatten && widthAxis.sqrMagnitude > 1e-4f)
            {
                // Torso is wide but shallow: local X = across the body, local Z = up/down (squashed).
                t.rotation = Quaternion.LookRotation(Vector3.Cross(widthAxis.normalized, axis), axis);
                t.localScale = new Vector3(thick, 0.5f * len + radius, thick * 0.6f);
            }
            else
            {
                t.rotation = Quaternion.FromToRotation(Vector3.up, axis);
                t.localScale = new Vector3(thick, 0.5f * len + radius, thick);
            }
        }

        void LateUpdate()
        {
            if (m_Label != null && m_Label.gameObject.activeInHierarchy && Camera.main != null)
                m_Label.transform.rotation = Quaternion.LookRotation(m_Label.transform.position - Camera.main.transform.position);
        }

        void BuildVisuals()
        {
            m_VisualRoot = new GameObject("PoseVisuals").transform;

            // Aura (the default look).
            m_AuraRoot = new GameObject("Aura").transform;
            m_AuraRoot.SetParent(m_VisualRoot);
            m_AuraTorso = MakeAuraPiece(PrimitiveType.Capsule, "Torso");
            m_AuraHead = MakeAuraPiece(PrimitiveType.Sphere, "Head");
            m_AuraLimbs = new Transform[k_AuraLimbs.Length];
            for (var i = 0; i < k_AuraLimbs.Length; i++) m_AuraLimbs[i] = MakeAuraPiece(PrimitiveType.Capsule, $"Limb_{k_AuraLimbs[i].a}_{k_AuraLimbs[i].b}");

            // Debug skeleton.
            m_SkeletonRoot = new GameObject("Skeleton").transform;
            m_SkeletonRoot.SetParent(m_VisualRoot);
            m_Joints = new Transform[k_NumKeypoints];
            m_JointPos = new Vector3[k_NumKeypoints];
            m_JointValid = new bool[k_NumKeypoints];
            for (var i = 0; i < k_NumKeypoints; i++)
            {
                var j = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(j.GetComponent<Collider>());
                j.name = $"Joint_{i}";
                j.transform.SetParent(m_SkeletonRoot);
                j.transform.localScale = Vector3.one * (i < 11 ? jointSize * 0.6f : jointSize);
                j.GetComponent<Renderer>().sharedMaterial = jointMaterial;
                m_Joints[i] = j.transform;
            }
            m_BoneLines = new LineRenderer[k_Bones.GetLength(0)];
            for (var b = 0; b < m_BoneLines.Length; b++)
            {
                var lr = new GameObject($"Bone_{k_Bones[b, 0]}_{k_Bones[b, 1]}").AddComponent<LineRenderer>();
                lr.transform.SetParent(m_SkeletonRoot);
                lr.positionCount = 2;
                lr.startWidth = lr.endWidth = 0.012f;
                lr.sharedMaterial = boneMaterial;
                m_BoneLines[b] = lr;
            }

            // Patient label (class / size).
            var labelGo = new GameObject("BodyClassLabel");
            labelGo.transform.SetParent(m_VisualRoot);
            m_Label = labelGo.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_Label.font = font;
            labelGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            m_Label.fontSize = 48;
            m_Label.characterSize = 0.01f;
            m_Label.anchor = TextAnchor.MiddleCenter;
            m_Label.color = Color.white;
            labelGo.SetActive(false);

            m_VisualRoot.gameObject.SetActive(false);
        }

        Transform MakeAuraPiece(PrimitiveType type, string name)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(m_AuraRoot);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = auraMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        void Tick(float t0, int visible)
        {
            m_Frames++;
            m_VisibleSum += visible;
            m_LatencySum += Time.realtimeSinceStartup - t0;
            var elapsed = Time.realtimeSinceStartup - m_StatsStart;
            if (elapsed < 2f) return;
            Debug.Log($"CPRPose: {m_Frames / elapsed:F1} pose/s, {1000f * m_LatencySum / m_Frames:F0} ms/pose, " +
                      $"detector ran {m_DetectorRuns}/{m_Frames}, avg joints {(float)m_VisibleSum / m_Frames:F1}/33, " +
                      $"lying={IsLyingConfirmed}, class={EffectiveClass} ({EstimatedLength:F2} m ±{EstimatedLengthSpread:F2}, n={m_LengthSamples.Count})");
            m_Frames = m_DetectorRuns = m_VisibleSum = 0;
            m_LatencySum = 0;
            m_StatsStart = Time.realtimeSinceStartup;
        }
    }
}
