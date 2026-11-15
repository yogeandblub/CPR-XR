using System.IO;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CPR.EditorTools
{
    /// <summary>Project setup and build helpers for CPR XR. Also available from the "CPR" menu.</summary>
    public static class CPRProjectTools
    {
        public const string PoseTestScene = "Assets/CPR/Scenes/PoseTest.unity";
        const string SourceScene = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";
        const string MaterialsDir = "Assets/CPR/Materials";

        [MenuItem("CPR/Apply App Identity")]
        public static void ApplyIdentity()
        {
            PlayerSettings.companyName = "CPR XR";
            PlayerSettings.productName = "CPR XR";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.cprxr.prototype");
            // Unity 6000.3's bundled SDK has API 34+; Quest 3 runs Android 14 (API 34).
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            AssetDatabase.SaveAssets();
            Debug.Log("CPR: identity applied (CPR XR / com.cprxr.prototype)");
        }

        [MenuItem("CPR/Create Pose Test Scene")]
        public static void CreatePoseTestScene()
        {
            if (!File.Exists(PoseTestScene))
                AssetDatabase.CopyAsset(SourceScene, PoseTestScene);

            var scene = EditorSceneManager.OpenScene(PoseTestScene, OpenSceneMode.Single);

            var existing = Object.FindFirstObjectByType<PoseOverlayTest>(FindObjectsInactive.Include);
            var overlay = existing != null ? existing : new GameObject("CPR Pose Overlay").AddComponent<PoseOverlayTest>();

            overlay.cameraAccess = Object.FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            overlay.raycastManager = Object.FindFirstObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
            overlay.poseDetector = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/BlazePose/Models/pose_detection.onnx");
            overlay.poseLandmarker = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/BlazePose/Models/pose_landmarks_detector_lite.onnx");
            overlay.anchorsCSV = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/BlazePose/Data/anchors.csv");
            overlay.jointMaterial = GetOrCreateMaterial("PoseJoint", new Color(0.1f, 0.9f, 1f));   // cyan
            overlay.boneMaterial = GetOrCreateMaterial("PoseBone", new Color(1f, 1f, 1f));          // white
            overlay.chestMaterial = GetOrCreateMaterial("CompressionPoint", new Color(1f, 0.45f, 0f)); // orange
            EditorUtility.SetDirty(overlay);

            EditorSceneManager.SaveScene(scene);

            Debug.Log($"CPR: PoseTest scene ready. camera={(overlay.cameraAccess != null)} raycast={(overlay.raycastManager != null)} " +
                      $"detector={(overlay.poseDetector != null)} landmarker={(overlay.poseLandmarker != null)} anchors={(overlay.anchorsCSV != null)}");
        }

        [MenuItem("CPR/Add Kneel Indicator To Open Scene")]
        public static string AddKneelIndicator()
        {
            var pose = Object.FindFirstObjectByType<PoseOverlayTest>(FindObjectsInactive.Include);
            if (pose == null) return "No PoseOverlayTest in the open scene - open Assets/CPR/Scenes/PoseTest.unity first.";

            var kneel = Object.FindFirstObjectByType<KneelIndicator>(FindObjectsInactive.Include);
            if (kneel == null)
            {
                var go = new GameObject("CPR Kneel Indicator");
                Undo.RegisterCreatedObjectUndo(go, "Add Kneel Indicator");
                kneel = Undo.AddComponent<KneelIndicator>(go);
            }
            Undo.RecordObject(kneel, "Configure Kneel Indicator");
            kneel.pose = pose;
            kneel.raycastManager = pose.raycastManager;
            kneel.head = Camera.main != null ? Camera.main.transform : null;
            // Sprites/Default: unlit, alpha-blended, honours vertex colour (for the outline) - always in builds.
            kneel.boxMaterial = GetOrCreateMaterial("KneelBox", Color.white, "Sprites/Default");
            EditorUtility.SetDirty(kneel);
            EditorSceneManager.MarkSceneDirty(kneel.gameObject.scene);
            return $"Kneel indicator ready on '{kneel.name}' (head={(kneel.head ? kneel.head.name : "none")}, raycast={(kneel.raycastManager != null)}). Scene marked unsaved - save when happy.";
        }

        [MenuItem("CPR/Configure Lying-Person Scene (remove YOLO, save battery, Editor test)")]
        public static string ConfigureLyingPersonScene()
        {
            var log = new System.Text.StringBuilder();
            var pose = Object.FindFirstObjectByType<PoseOverlayTest>(FindObjectsInactive.Include);
            if (pose == null) return "No PoseOverlayTest in the open scene - open Assets/CPR/Scenes/PoseTest.unity first.";
            Undo.SetCurrentGroupName("Configure Lying-Person Scene");
            var group = Undo.GetCurrentGroup();

            // 1. Remove YOLO object detection + its menu UI (BlazePose has its own person detector).
            foreach (var name in new[] { "DetectionManagerPrefab", "SentisInferenceManagerPrefab", "DetectionUiMenuPrefab", "ReturnToStartScene" })
            {
                foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (t == null || t.name != name) continue;
                    Undo.DestroyObjectImmediate(t.gameObject);
                    log.AppendLine($"removed {name}");
                    break;
                }
            }

            // 2. Battery: BlazePose only needs 224/256 px input, so ask the camera for less.
            if (pose.cameraAccess != null)
            {
                var so = new SerializedObject(pose.cameraAccess);
                so.FindProperty("RequestedResolution").vector2IntValue = new Vector2Int(640, 480);
                so.FindProperty("_maxFramerate").intValue = 30;
                so.ApplyModifiedProperties();
                log.AppendLine("camera: 640x480 @ 30 fps (was 1280x960 @ 60)");
            }

            // 3. Editor test rig: a virtual camera where the photo was taken from, and a draggable head.
            Undo.RecordObject(pose, "Configure pose");
            pose.detectorScoreThreshold = 0.75f;
            if (pose.editorTestCamera == null)
            {
                var camGo = new GameObject("Editor Test Camera (photo viewpoint)");
                Undo.RegisterCreatedObjectUndo(camGo, "Editor test camera");
                camGo.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, -1.2f), Quaternion.Euler(50f, 0f, 0f));
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;           // never renders; only used to turn image points into rays
                cam.fieldOfView = 60f;         // ~phone camera, portrait; adjust to the photo
                camGo.tag = "Untagged";
                pose.editorTestCamera = cam;
                log.AppendLine("added Editor Test Camera");
            }
            EditorUtility.SetDirty(pose);

            var kneel = Object.FindFirstObjectByType<KneelIndicator>(FindObjectsInactive.Include);
            if (kneel != null && kneel.editorHead == null)
            {
                var headGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.DestroyImmediate(headGo.GetComponent<Collider>());
                headGo.name = "Editor Test Head (drag me to test kneeling)";
                headGo.transform.position = new Vector3(0.6f, 1.6f, -0.6f);
                headGo.transform.localScale = Vector3.one * 0.2f;
                headGo.hideFlags = HideFlags.DontSaveInBuild;
                Undo.RegisterCreatedObjectUndo(headGo, "Editor test head");
                Undo.RecordObject(kneel, "Configure kneel");
                kneel.editorHead = headGo.transform;
                EditorUtility.SetDirty(kneel);
                log.AppendLine("added Editor Test Head");
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(pose.gameObject.scene);
            log.Append("Scene marked unsaved - Cmd+Z undoes all of this in one step.");
            return log.ToString();
        }

        [MenuItem("CPR/Configure Guidance (aura, compression disc, demo controls, full model)")]
        public static string ConfigureGuidance()
        {
            var log = new System.Text.StringBuilder();
            var pose = Object.FindFirstObjectByType<PoseOverlayTest>(FindObjectsInactive.Include);
            if (pose == null) return "No PoseOverlayTest in the open scene - open Assets/CPR/Scenes/PoseTest.unity first.";
            var kneel = Object.FindFirstObjectByType<KneelIndicator>(FindObjectsInactive.Include);
            Undo.SetCurrentGroupName("Configure CPR Guidance");
            var group = Undo.GetCurrentGroup();

            // Aura material from the glow shader.
            var auraPath = $"{MaterialsDir}/PatientAura.mat";
            var aura = AssetDatabase.LoadAssetAtPath<Material>(auraPath);
            if (aura == null)
            {
                aura = new Material(Shader.Find("CPR/AuraGlow"));
                AssetDatabase.CreateAsset(aura, auraPath);
                log.AppendLine("created PatientAura material");
            }

            Undo.RecordObject(pose, "Configure pose");
            pose.auraMaterial = aura;
            pose.showSkeleton = false;
            // Full landmark model: more accurate joints for height + placement (GPU budget freed by removing YOLO).
            var full = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/BlazePose/Models/pose_landmarks_detector_full.onnx");
            if (full != null) { pose.poseLandmarker = full; log.AppendLine("landmarker -> full"); }
            EditorUtility.SetDirty(pose);

            // Compression disc.
            var disc = Object.FindFirstObjectByType<CompressionDisc>(FindObjectsInactive.Include);
            if (disc == null)
            {
                var go = new GameObject("CPR Compression Disc");
                Undo.RegisterCreatedObjectUndo(go, "Add compression disc");
                disc = Undo.AddComponent<CompressionDisc>(go);
                log.AppendLine("added CPR Compression Disc");
            }
            Undo.RecordObject(disc, "Configure disc");
            disc.pose = pose;
            var rig = Object.FindFirstObjectByType<OVRCameraRig>(FindObjectsInactive.Include);
            if (rig != null) { disc.leftHand = rig.leftHandAnchor; disc.rightHand = rig.rightHandAnchor; }
            disc.discMaterial = GetOrCreateMaterial("CompressionDisc", Color.white, "Sprites/Default");
            EditorUtility.SetDirty(disc);

            // Demo controls.
            var controls = Object.FindFirstObjectByType<CPRDemoControls>(FindObjectsInactive.Include);
            if (controls == null)
            {
                var go = new GameObject("CPR Demo Controls");
                Undo.RegisterCreatedObjectUndo(go, "Add demo controls");
                controls = Undo.AddComponent<CPRDemoControls>(go);
                log.AppendLine("added CPR Demo Controls");
            }
            Undo.RecordObject(controls, "Configure controls");
            controls.pose = pose; controls.kneel = kneel; controls.disc = disc;
            EditorUtility.SetDirty(controls);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(pose.gameObject.scene);
            log.Append("Scene marked unsaved - Cmd+Z undoes all of this in one step.");
            return log.ToString();
        }

        [MenuItem("CPR/Configure Hand Recording + Ghost Hands")]
        public static string ConfigureHands()
        {
            var log = new System.Text.StringBuilder();
            var pose = Object.FindFirstObjectByType<PoseOverlayTest>(FindObjectsInactive.Include);
            var disc = Object.FindFirstObjectByType<CompressionDisc>(FindObjectsInactive.Include);
            var kneel = Object.FindFirstObjectByType<KneelIndicator>(FindObjectsInactive.Include);
            if (pose == null || disc == null) return "Run 'Configure Guidance' first (needs the pose overlay and compression disc).";
            Undo.SetCurrentGroupName("Configure Hand Recording");
            var group = Undo.GetCurrentGroup();

            Undo.RecordObject(disc, "Disc kneel ref");
            disc.kneel = kneel;
            EditorUtility.SetDirty(disc);

            // Ghost hand material: the aura glow, whiter and a little stronger.
            var ghostPath = $"{MaterialsDir}/GhostHands.mat";
            var ghostMat = AssetDatabase.LoadAssetAtPath<Material>(ghostPath);
            if (ghostMat == null)
            {
                ghostMat = new Material(Shader.Find("CPR/AuraGlow"));
                ghostMat.SetColor("_Color", new Color(0.9f, 0.95f, 1f, 1f));
                ghostMat.SetFloat("_Intensity", 1.2f);
                ghostMat.SetFloat("_Inner", 0.15f);
                ghostMat.SetFloat("_Power", 2f);
                AssetDatabase.CreateAsset(ghostMat, ghostPath);
                log.AppendLine("created GhostHands material");
            }

            var ghosts = Object.FindFirstObjectByType<GhostHands>(FindObjectsInactive.Include);
            if (ghosts == null)
            {
                var go = new GameObject("CPR Ghost Hands");
                Undo.RegisterCreatedObjectUndo(go, "Add ghost hands");
                ghosts = Undo.AddComponent<GhostHands>(go);
                log.AppendLine("added CPR Ghost Hands");
            }
            Undo.RecordObject(ghosts, "Configure ghosts");
            ghosts.pose = pose; ghosts.disc = disc; ghosts.ghostMaterial = ghostMat;
            EditorUtility.SetDirty(ghosts);

            var recorder = Object.FindFirstObjectByType<HandPoseRecorder>(FindObjectsInactive.Include);
            if (recorder == null)
            {
                var go = new GameObject("CPR Hand Pose Recorder");
                Undo.RegisterCreatedObjectUndo(go, "Add recorder");
                recorder = Undo.AddComponent<HandPoseRecorder>(go);
                log.AppendLine("added CPR Hand Pose Recorder");
            }
            Undo.RecordObject(recorder, "Configure recorder");
            recorder.disc = disc; recorder.ghostHands = ghosts;
            EditorUtility.SetDirty(recorder);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(pose.gameObject.scene);
            log.Append("Scene marked unsaved - Cmd+Z undoes all of this in one step.");
            return log.ToString();
        }


        [MenuItem("CPR/Configure Modes + Menu (Default / Practice / Pose)")]
        public static string ConfigureModes()
        {
            var log = new System.Text.StringBuilder();
            var pose = Object.FindFirstObjectByType<PoseOverlayTest>(FindObjectsInactive.Include);
            var kneel = Object.FindFirstObjectByType<KneelIndicator>(FindObjectsInactive.Include);
            var disc = Object.FindFirstObjectByType<CompressionDisc>(FindObjectsInactive.Include);
            var ghosts = Object.FindFirstObjectByType<GhostHands>(FindObjectsInactive.Include);
            var recorder = Object.FindFirstObjectByType<HandPoseRecorder>(FindObjectsInactive.Include);
            var controls = Object.FindFirstObjectByType<CPRDemoControls>(FindObjectsInactive.Include);
            var rig = Object.FindFirstObjectByType<OVRCameraRig>(FindObjectsInactive.Include);
            if (pose == null || kneel == null || disc == null || ghosts == null || recorder == null || rig == null)
                return "Missing pieces - run the earlier CPR/Configure steps first.";
            Undo.SetCurrentGroupName("Configure CPR Modes");
            var group = Undo.GetCurrentGroup();

            // 3. Practice mannequin spawner.
            var mannequin = Object.FindFirstObjectByType<PracticeMannequin>(FindObjectsInactive.Include);
            if (mannequin == null)
            {
                var go = new GameObject("CPR Practice Mannequin");
                Undo.RegisterCreatedObjectUndo(go, "Mannequin");
                mannequin = Undo.AddComponent<PracticeMannequin>(go);
            }
            Undo.RecordObject(mannequin, "Configure mannequin");
            mannequin.pose = pose;
            mannequin.auraMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsDir}/PatientAura.mat");
            EditorUtility.SetDirty(mannequin);

            Undo.RecordObject(ghosts, "Ghost kneel ref");
            ghosts.kneel = kneel;
            EditorUtility.SetDirty(ghosts);

            // 4. Mode manager.
            var mgr = Object.FindFirstObjectByType<CPRModeManager>(FindObjectsInactive.Include);
            if (mgr == null)
            {
                var go = new GameObject("CPR Mode Manager");
                Undo.RegisterCreatedObjectUndo(go, "Mode manager");
                mgr = Undo.AddComponent<CPRModeManager>(go);
            }

            // 5. Menu: a world-space Unity UI canvas (restyle it freely later), made pokeable with Meta's template.
            var menu = mgr.menu;
            if (menu == null)
            {
                menu = BuildMenuCanvas(mgr);
                Undo.RegisterCreatedObjectUndo(menu, "Menu");
                log.AppendLine("built CPR Menu canvas (Default / Practice Adult-Child-Infant / Pose / Reset / Close)");
            }
            Undo.RecordObject(mgr, "Configure mode manager");
            mgr.pose = pose; mgr.kneel = kneel; mgr.disc = disc; mgr.ghostHands = ghosts; mgr.recorder = recorder;
            mgr.mannequin = mannequin; mgr.legacyControls = controls; mgr.menu = menu;
            EditorUtility.SetDirty(mgr);

            // Fingertip / controller-tip pokes on the menu buttons (no event system needed).
            var poker = Object.FindFirstObjectByType<MenuPoker>(FindObjectsInactive.Include) ?? Undo.AddComponent<MenuPoker>(mgr.gameObject);
            Undo.RecordObject(poker, "Configure poker");
            poker.menu = menu.GetComponent<RectTransform>();
            EditorUtility.SetDirty(poker);

            // Point-and-pinch (or trigger) ray grab for the practice figure.
            var grabber = Object.FindFirstObjectByType<FigureRayGrabber>(FindObjectsInactive.Include) ?? Undo.AddComponent<FigureRayGrabber>(mannequin.gameObject);
            Undo.RecordObject(grabber, "Configure grabber");
            grabber.mannequin = mannequin;
            grabber.raycastManager = pose.raycastManager;
            grabber.rayMaterial = GetOrCreateMaterial("PointerRay", Color.white, "Sprites/Default");
            EditorUtility.SetDirty(grabber);
            log.AppendLine("menu poker + figure ray grabber ready");

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(pose.gameObject.scene);
            log.Append("Scene marked unsaved - Cmd+Z undoes all of this in one step.");
            return log.ToString();
        }

        static GameObject BuildMenuCanvas(CPRModeManager mgr)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject("CPR Menu", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(360, 330);   // px; scale below makes it 36 x 33 cm
            rt.localScale = Vector3.one * 0.001f;

            var bg = new GameObject("Background", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            bg.transform.SetParent(root.transform, false);
            Stretch(bg.GetComponent<RectTransform>());
            bg.GetComponent<UnityEngine.UI.Image>().color = new Color(0.05f, 0.07f, 0.1f, 0.85f);

            var y = 140f;
            Label(root.transform, font, "CPR XR", 30, FontStyle.Bold, new Vector2(0, y)); y -= 42;
            Button(root.transform, font, "Default  -  scan a real person", new Vector2(0, y), 320, mgr.SetDefault); y -= 52;
            Label(root.transform, font, "Practice on a figure:", 18, FontStyle.Normal, new Vector2(0, y + 6)); y -= 28;
            Button(root.transform, font, "Adult", new Vector2(-108, y), 100, mgr.SetPracticeAdult);
            Button(root.transform, font, "Child", new Vector2(0, y), 100, mgr.SetPracticeChild);
            Button(root.transform, font, "Infant", new Vector2(108, y), 100, mgr.SetPracticeInfant); y -= 56;
            Button(root.transform, font, "Pose  (temporary)", new Vector2(0, y), 320, mgr.SetPose); y -= 52;
            Button(root.transform, font, "Reset scan", new Vector2(-82, y), 156, mgr.ResetScan);
            Button(root.transform, font, "Close", new Vector2(82, y), 156, mgr.CloseMenu);

            root.SetActive(false);
            return root;
        }

        static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
            r.localPosition = Vector3.zero; r.localScale = Vector3.one; r.localRotation = Quaternion.identity;
        }

        static void Label(Transform parent, Font font, string text, int size, FontStyle style, Vector2 pos)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(UnityEngine.UI.Text));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(330, 36); r.anchoredPosition = pos;
            var t = go.GetComponent<UnityEngine.UI.Text>();
            t.font = font; t.text = text; t.fontSize = size; t.fontStyle = style;
            t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
        }

        static void Button(Transform parent, Font font, string text, Vector2 pos, float width, UnityEngine.Events.UnityAction action)
        {
            var go = new GameObject($"Button - {text}", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(width, 44); r.anchoredPosition = pos;
            go.GetComponent<UnityEngine.UI.Image>().color = new Color(0.16f, 0.45f, 0.85f, 1f);
            var b = go.GetComponent<UnityEngine.UI.Button>();
            var colors = b.colors; colors.highlightedColor = new Color(0.35f, 0.65f, 1f); colors.pressedColor = new Color(0.1f, 0.9f, 0.4f); b.colors = colors;
            UnityEditor.Events.UnityEventTools.AddPersistentListener(b.onClick, action);
            var label = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
            label.transform.SetParent(go.transform, false);
            Stretch(label.GetComponent<RectTransform>());
            var t = label.GetComponent<UnityEngine.UI.Text>();
            t.font = font; t.text = text; t.fontSize = 18; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
        }

        /// <summary>Copy hand poses recorded on the headset (adb pull) into the project so every build ships them.</summary>
        public static string ImportHandPoses(string pulledFolder)
        {
            var dst = "Assets/CPR/Resources/" + HandPoseFile.ResourcesFolder;
            Directory.CreateDirectory(dst);
            var n = 0;
            foreach (var f in Directory.GetFiles(pulledFolder, "*.json")) { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); n++; }
            AssetDatabase.Refresh();
            return $"imported {n} hand pose(s) into {dst}";
        }

        static Material GetOrCreateMaterial(string name, Color color, string shader = "Unlit/Color")
        {
            var path = $"{MaterialsDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        [MenuItem("CPR/Build Pose Test APK")]
        public static void BuildPoseTest()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { PoseTestScene },
                locationPathName = "Builds/CPR-PoseTest.apk",
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
            });
            Debug.Log($"CPR_BUILD_RESULT {report.summary.result} errors={report.summary.totalErrors}");
            if (Application.isBatchMode)
                EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>Batch entry point: identity + scene + build in one run.</summary>
        public static void SetupAndBuild()
        {
            ApplyIdentity();
            CreatePoseTestScene();
            BuildPoseTest();
        }
    }
}
