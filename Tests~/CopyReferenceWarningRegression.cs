using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class CopyReferenceWarningRegression
{
    static readonly List<string> results = new List<string>();
    static readonly string[] cases = { "normal", "retained", "particle", "excluded", "failure", "cancel" };
    static int index, failures;
    static ToolPlan plan;
    static string folder;
    static string[] published;
    static Texture2D texture;
    static Mesh mesh;
    static Dictionary<string, string> sourceHashes;
    static double deadline;
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        if (parent != "Assets") Folder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
    public static void Run()
    {
        EditorSettings.serializationMode = SerializationMode.ForceText;
        Next();
    }
    static void Next()
    {
        if (index == cases.Length)
        {
            File.WriteAllLines("copy-reference-warning-results.txt", results.Concat(new[] { "Failures: " + failures }));
            EditorApplication.Exit(failures == 0 ? 0 : 1); return;
        }
        try
        {
            string kind = cases[index];
            folder = "Assets/WarningRegression/" + kind;
            Folder(folder + "/Source/Textures"); Folder(folder + "/Out");
            texture = new Texture2D(2, 2); texture.SetPixels(new[] { Color.red, Color.red, Color.red, Color.red }); texture.Apply();
            AssetDatabase.CreateAsset(texture, folder + "/Source/Textures/Texture.asset");
            mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            AssetDatabase.CreateAsset(mesh, folder + "/Source/Mesh.asset");
            var material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = texture };
            AssetDatabase.CreateAsset(material, folder + "/Source/Material.mat");
            AssetDatabase.SaveAssets();
            var roots = new List<GameObject>();
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("Root" + i);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                if (i == 0)
                {
                    var child = new GameObject("Particle"); child.transform.SetParent(go.transform);
                    child.AddComponent<ParticleSystem>();
                    var renderer = child.GetComponent<ParticleSystemRenderer>();
                    renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.SetMeshes(new[] { mesh }); renderer.sharedMaterial = material;
                }
                roots.Add(PrefabUtility.SaveAsPrefabAsset(go, folder + "/Source/Root" + i + ".prefab"));
                Object.DestroyImmediate(go);
            }
            sourceHashes = Directory.GetFiles(folder + "/Source", "*", SearchOption.AllDirectories).ToDictionary(p => p, ToolPaths.Fingerprint);
            published = null;
            var excluded = kind == "excluded" ? new[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder + "/Source/Textures") } : Array.Empty<DefaultAsset>();
            ToolWorkflow.Preview(() =>
            {
                plan = PrefabCopyPlan.Build(roots, AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder + "/Out"), excluded, paths => published = paths);
                if (kind == "retained" || kind == "failure" || kind == "cancel")
                {
                    int check = plan.steps.FindIndex(s => s.description == "复查整批副本的旧资源引用");
                    plan.steps.Insert(check, new ToolStep { description = "模拟导入器重新引用原贴图", execute = _ =>
                    {
                        string path = plan.overview.resources.Single(r => r.source == folder + "/Source/Material.mat").destination;
                        var copy = AssetDatabase.LoadAssetAtPath<Material>(path); copy.mainTexture = texture;
                        EditorUtility.SetDirty(copy); AssetDatabase.SaveAssetIfDirty(copy);
                    }});
                }
                if (kind == "particle")
                {
                    // Inject after the general dependency check to exercise the independent particle check.
                    int check = plan.steps.FindIndex(s => s.description.StartsWith("验证预制体副本"));
                    plan.steps.Insert(check, new ToolStep { description = "模拟粒子槽仍引用原 Mesh", execute = _ =>
                    {
                        string path = plan.overview.ResultPaths[0];
                        var contents = PrefabUtility.LoadPrefabContents(path);
                        try { contents.GetComponentInChildren<ParticleSystemRenderer>().SetMeshes(new[] { mesh }); PrefabUtility.SaveAsPrefabAsset(contents, path); }
                        finally { PrefabUtility.UnloadPrefabContents(contents); }
                    }});
                }
                if (kind == "failure") plan.Add("模拟真实文件失败", _ => { throw new IOException("Expected regression failure"); });
                if (kind == "cancel")
                {
                    plan.Add("模拟用户取消", _ => typeof(ToolWorkflow).GetField("cancel", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, true));
                    plan.Add("取消后不执行", _ => { throw new Exception("Cancellation was ignored"); });
                }
                return plan;
            }, "CopyWarningRegression");
            EditorApplication.delayCall += Start;
        }
        catch (Exception e) { FinishCase(e); }
    }
    static void Start()
    {
        try
        {
            Require(plan != null && plan.blockingErrors.Count == 0 && plan.errors.Count == 0, "Invalid fixture plan");
            ToolWorkflow.Start(); deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update += Check;
        }
        catch (Exception e) { FinishCase(e); }
    }
    static void Check()
    {
        if (ToolWorkflow.Running && EditorApplication.timeSinceStartup < deadline) return;
        EditorApplication.update -= Check;
        try
        {
            Require(!ToolWorkflow.Running, "Timed out");
            string kind = cases[index];
            if (kind == "failure" || kind == "cancel")
            {
                Require(plan.phase == (kind == "failure" ? ToolPlanPhase.Failed : ToolPlanPhase.Cancelled), "Real failure/cancel must roll back");
                Require(plan.overview.referenceWarnings.Count > 0, "Fixture must have a warning before failure/cancel");
                Require(published == null, "Rolled back batch published results");
                Require(!Directory.GetFiles(folder + "/Out", "*", SearchOption.AllDirectories).Any(), "Rollback left copied files");
            }
            else
            {
                Require(plan.phase == ToolPlanPhase.Succeeded, ToolWorkflow.Status);
                Require(published != null && published.Length == 2 && published.All(File.Exists), "Batch results lost");
                Require(plan.errors.Count == 0 && plan.blockingErrors.Count == 0, "Reference warnings must not become errors");
                int count = plan.overview.referenceWarnings.Count;
                if (kind == "retained" || kind == "particle")
                {
                    Require(count == 1, "Expected one precise owner/dependency warning, got " + count);
                    var warning = plan.overview.referenceWarnings[0];
                    Require(File.Exists(warning.copiedAsset) && File.Exists(warning.originalAsset) && File.Exists(warning.expectedAsset), "Warning paths must be actionable");
                    Require(ToolWorkflow.Status.Contains("副本已保留") && ToolWorkflow.Status.Contains("1 处"), "Completion must disclose retained references");
                    Require(warning.LogMessage.Contains(warning.copiedAsset) && warning.LogMessage.Contains(warning.originalAsset) && warning.LogMessage.Contains(warning.expectedAsset), "Log omitted diagnostics");
                    if (kind == "retained")
                    {
                        Require(AssetDatabase.LoadAssetAtPath<Material>(warning.copiedAsset).mainTexture == texture, "Fixture no longer retains original");
                        var copies = plan.overview.resources.Where(r => !r.Shared).ToDictionary(r => r.source, r => r.destination, StringComparer.OrdinalIgnoreCase);
                        PrefabCopyPlan.ReportRetainedReferences(plan.overview, copies);
                        Require(plan.overview.referenceWarnings.Count == count, "Repeated inspection duplicates warning");
                    }
                }
                else Require(count == 0, "Normal or intentionally shared resources must not warn");
                if (kind == "excluded") Require(plan.overview.resources.Any(r => r.Shared && r.source == AssetDatabase.GetAssetPath(texture)), "Excluded texture not reported as planned sharing");
                ToolFileTransaction.RestoreLast();
                Require(!Directory.GetFiles(folder + "/Out", "*", SearchOption.AllDirectories).Any(), "Successful warning batch is not recoverable");
            }
            Require(sourceHashes.All(pair => ToolPaths.Fingerprint(pair.Key) == pair.Value), "Source assets were changed");
            FinishCase(null);
        }
        catch (Exception e) { FinishCase(e); }
    }
    static void FinishCase(Exception error)
    {
        if (error != null) { failures++; results.Add("FAIL " + cases[index] + "\n" + error); }
        else results.Add("PASS " + cases[index]);
        index++; EditorUtility.ClearProgressBar(); EditorApplication.delayCall += Next;
    }
}
