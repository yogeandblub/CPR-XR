using System.Text;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

public static class AgentChecks
{
    // Prints every BlazePose model's inputs/outputs so we can wire them up correctly.
    public static void DumpBlazeModels()
    {
        var sb = new StringBuilder("AGENT_MODELS\n");
        foreach (var name in new[] { "pose_detection", "pose_landmarks_detector_lite", "pose_landmarks_detector_full" })
        {
            var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>($"Assets/BlazePose/Models/{name}.onnx");
            if (asset == null) { sb.AppendLine($"{name}: NOT IMPORTED as ModelAsset"); continue; }
            var model = ModelLoader.Load(asset);
            sb.AppendLine($"{name}:");
            foreach (var i in model.inputs) sb.AppendLine($"  in  {i.name} {i.shape} {i.dataType}");
            foreach (var o in model.outputs) sb.AppendLine($"  out {o.name}");
        }
        var cs = Resources.Load<ComputeShader>("ComputeShaders/ImageTransform");
        sb.AppendLine($"ImageTransform compute shader found: {cs != null}");
        Debug.Log(sb.ToString() + "AGENT_MODELS_END");
        EditorApplication.Exit(0);
    }
}
