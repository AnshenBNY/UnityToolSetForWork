using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class ParticleMeshRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly List<string> Results = new List<string>();
    private static int failures;
    private static Mesh[] bundle;

    public static void Run()
    {
        try
        {
            Folder("Assets/Inputs");
            Folder("Assets/Excluded");
            Folder("Assets/Outputs");
            Mesh rootMesh = MeshAsset("Bundle", 1);
            AssetDatabase.CreateAsset(rootMesh, "Assets/Inputs/Bundle.asset");
            AssetDatabase.AddObjectToAsset(MeshAsset("Shard", 2), rootMesh);
            AssetDatabase.AddObjectToAsset(MeshAsset("Shard", 3), rootMesh);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset("Assets/Inputs/Bundle.asset", ImportAssetOptions.ForceSynchronousImport);
            bundle = AssetDatabase.LoadAllAssetsAtPath("Assets/Inputs/Bundle.asset").OfType<Mesh>().OrderBy(m => m.name == "Bundle" ? 1 : 0).ThenBy(Id).ToArray();
            Require(bundle.Length == 3, "three sub-assets required");

            foreach (bool deferred in new[] { false, true })
            {
                bool mode = deferred;
                Case("All submeshes, duplicate names/slots, weights, repeat run and save; deferred=" + mode, () =>
                {
                    Mesh[] source = { bundle[1], bundle[0], bundle[1], bundle[2] };
                    float[] weights = { .1f, .2f, .3f, .4f };
                    GameObject go = Particle("MultipleMeshes", source);
                    try
                    {
                        var renderer = go.GetComponent<ParticleSystemRenderer>();
                        renderer.meshDistribution = ParticleSystemMeshDistribution.NonUniformRandom;
                        renderer.SetMeshWeightings(weights);
                        string output = "Multi" + mode;
                        Copy(typeof(PrefabCopyTool), go, output, mode);
                        VerifyMeshes(source, Read(go), "Assets/Outputs/" + output + "/");
                        var actualWeights = new float[renderer.meshCount];
                        Require(renderer.GetMeshWeightings(actualWeights) == weights.Length, "weight count");
                        Require(actualWeights.Zip(weights, (a, b) => Mathf.Approximately(a, b)).All(v => v), "weights preserved");
                        Require(renderer.meshDistribution == ParticleSystemMeshDistribution.NonUniformRandom, "distribution preserved");
                        Require(Read(go)[0] == Read(go)[2], "duplicate slot identity");
                        Require(Directory.GetFiles("Assets/Outputs/" + output, "*.asset").Length == 1, "one copy per file");
                        Mesh[] beforeRepeat = Read(go);
                        Copy(typeof(PrefabCopyTool), go, output, mode);
                        Require(Read(go).SequenceEqual(beforeRepeat), "repeat run preserves references");
                        Require(Directory.GetFiles("Assets/Outputs/" + output, "*.asset").Length == 1, "repeat run does not duplicate files");
                        VerifySaved(go, source, output, "Assets/Inputs/Bundle.asset");
                    }
                    finally { Object.DestroyImmediate(go); }
                });
            }

            Case("Unity-normalized null slot, mixed files, excluded file and inactive child", () =>
            {
                Mesh other = MeshAsset("Other", 4);
                Mesh excluded = MeshAsset("Excluded", 5);
                AssetDatabase.CreateAsset(other, "Assets/Inputs/Other.asset");
                AssetDatabase.CreateAsset(excluded, "Assets/Excluded/Excluded.asset");
                AssetDatabase.SaveAssets();
                Mesh[] source = { null, bundle[1], other, excluded };
                GameObject parent = new GameObject("Container");
                GameObject go = Particle("Inactive", source);
                Mesh[] normalizedSource = Read(go);
                go.transform.SetParent(parent.transform);
                go.SetActive(false);
                try
                {
                    Copy(typeof(PrefabCopyTool), parent, "Mixed", true, AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets/Excluded"));
                    Mesh[] actual = Read(go);
                    Require(actual.Length == normalizedSource.Length, "Unity-normalized mesh count preserved");
                    for (int i = 0; i < actual.Length; i++)
                    {
                        if (normalizedSource[i] == null || normalizedSource[i] == excluded)
                            Require(actual[i] == normalizedSource[i], "null/excluded slot unchanged");
                        else
                            Require(AssetDatabase.GetAssetPath(actual[i]).StartsWith("Assets/Outputs/Mixed/"), "included slot replaced");
                    }
                    Require(Directory.GetFiles("Assets/Outputs/Mixed", "*.asset").Length == 2, "only two source files copied");
                }
                finally { Object.DestroyImmediate(parent); }
            });

            Case("Actual project FBX with multiple child meshes leaves no old FBX dependency", () =>
            {
                string fbxPath = Directory.GetFiles("Assets/FbxFixtures").Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Replace('\\', '/'))
                    .FirstOrDefault(p => AssetDatabase.LoadAllAssetsAtPath(p).OfType<Mesh>().Count(m => m.subMeshCount == 1) >= 2);
                Require(fbxPath != null, "a real FBX with two or more submeshes is required");
                Mesh[] meshes = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>().Where(m => m.subMeshCount == 1).OrderBy(Id).ToArray();
                // Unity 2022.3 Renderer supports up to four entries; use two distinct FBX meshes and a repeat.
                Mesh[] source = { meshes[1], meshes[0], meshes[1] };
                GameObject go = Particle("RealFBX", source);
                try
                {
                    Copy(typeof(PrefabCopyTool), go, "RealFBX", true);
                    VerifyMeshes(source, Read(go), "Assets/Outputs/RealFBX/");
                    VerifySaved(go, source, "RealFBX", fbxPath);
                    Results.Add("FBX fixture: " + fbxPath + ", submeshes=" + meshes.Length);
                }
                finally { Object.DestroyImmediate(go); }
            });

            Case("Single mesh supports Undo", () =>
            {
                GameObject go = Particle("Undo", new[] { bundle[0] });
                try
                {
                    Undo.IncrementCurrentGroup();
                    Copy(typeof(PrefabCopyTool), go, "Undo", false);
                    Undo.FlushUndoRecordObjects();
                    Require(Read(go)[0] != bundle[0], "mesh replaced before Undo");
                    Undo.PerformUndo();
                    Require(Read(go)[0] == bundle[0], "Undo restores source mesh");
                }
                finally { Object.DestroyImmediate(go); }
            });

            Case("Unmatched/ambiguous submesh is not silently assigned", () =>
            {
                Mesh first = MeshAsset("Unmatched", 8);
                Mesh second = MeshAsset("Unmatched", 9);
                Mesh source = MeshAsset("Unmatched", 7);
                try
                {
                    MethodInfo match = typeof(PrefabCopyTool).GetMethod("FindMatchingParticleMesh", BindingFlags.NonPublic | BindingFlags.Static);
                    Require(match.Invoke(null, new object[] { source, new[] { first, second } }) == null, "ambiguous match rejected");
                    Require((Mesh)match.Invoke(null, new object[] { source, new[] { first } }) == first, "unique name fallback");
                    Require(match.Invoke(null, new object[] { source, bundle }) == null, "missing match rejected");
                }
                finally { Object.DestroyImmediate(first); Object.DestroyImmediate(second); Object.DestroyImmediate(source); }
            });

            Case("Copied prefab instance survives tool save/reload without modifying source prefab", () =>
            {
                Mesh[] source = { bundle[1], bundle[0] };
                GameObject template = Particle("PrefabInstance", source);
                PrefabUtility.SaveAsPrefabAsset(template, "Assets/Inputs/Particle.prefab");
                Object.DestroyImmediate(template);
                Folder("Assets/Outputs/Instance");
                Require(AssetDatabase.CopyAsset("Assets/Inputs/Particle.prefab", "Assets/Outputs/Instance/Particle_Copy.prefab"), "copy prefab before processing instance");
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Outputs/Instance/Particle_Copy.prefab");
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                try
                {
                    Copy(typeof(PrefabCopyTool), instance, "Instance", true);
                    var saveTool = new PrefabCopyTool();
                    try
                    {
                        typeof(PrefabCopyTool).GetField("rootPrefab", PrivateInstance).SetValue(saveTool, instance); typeof(PrefabCopyTool).GetField("rootPrefabCopyPath", PrivateInstance).SetValue(saveTool, "Assets/Outputs/Instance/Particle_Copy.prefab");
                        Require((bool)typeof(PrefabCopyTool).GetMethod("TrySaveCurrentPrefab", PrivateInstance).Invoke(saveTool, new object[] { false }), "tool saves copied prefab");
                    }
                    finally { }
                    VerifySaved(instance, source, "Instance", "Assets/Inputs/Bundle.asset");
                    Require(!AssetDatabase.GetDependencies("Assets/Outputs/Instance/Particle_Copy.prefab", true).Contains("Assets/Inputs/Bundle.asset"), "saved copied prefab has no old mesh dependency");
                    Require(AssetDatabase.GetDependencies("Assets/Inputs/Particle.prefab", true).Contains("Assets/Inputs/Bundle.asset"), "source prefab remains unchanged");
                }
                finally { Object.DestroyImmediate(instance); }
            });
        }
        catch (Exception ex) { failures++; Results.Add("SETUP FAILURE: " + ex); }
        Results.Add("Failures: " + failures);
        string report = string.Join("\n", Results);
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "regression-results.txt"), report);
        Debug.Log(report);
        return;
    }

    private static void Case(string name, Action action)
    {
        try { action(); Results.Add("PASS: " + name); }
        catch (Exception ex) { failures++; Results.Add("FAIL: " + name + "\n" + ex); }
    }

    private static void Copy(Type toolType, GameObject root, string outputName, bool deferred, DefaultAsset exclude = null)
    {
        string output = "Assets/Outputs/" + outputName;
        Folder(output);
        object tool = typeof(ScriptableObject).IsAssignableFrom(toolType) ? (object)ScriptableObject.CreateInstance(toolType) : Activator.CreateInstance(toolType);
        try
        {
            toolType.GetField("FxModelCopyPath", PrivateInstance).SetValue(tool, output);
            toolType.GetField("enableDeferredAssetOps", PrivateInstance).SetValue(tool, deferred);
            if (exclude != null) toolType.GetField("excludeFolders", PrivateInstance).SetValue(tool, new List<DefaultAsset> { exclude });
            Action copy = () => toolType.GetMethod("CopyMeshFromParticle", PrivateInstance).Invoke(tool, new object[] { root });
            toolType.GetMethod("ExecuteStepWithOptionalDeferredAssetOps", PrivateInstance).Invoke(tool, new object[] { copy });
        }
        finally { if (tool is Object unityObject) Object.DestroyImmediate(unityObject); }
    }

    private static void VerifyMeshes(Mesh[] source, Mesh[] actual, string expectedFolder)
    {
        Require(source.Length == actual.Length, "mesh count preserved");
        for (int i = 0; i < source.Length; i++)
        {
            Require(actual[i] != null && actual[i] != source[i], "slot " + i + " references a copy");
            Require(AssetDatabase.GetAssetPath(actual[i]).StartsWith(expectedFolder), "slot " + i + " output path");
            Require(Id(actual[i]) == Id(source[i]), "slot " + i + " correct child mesh/local ID");
        }
    }

    private static void VerifySaved(GameObject go, Mesh[] source, string output, string forbiddenDependency)
    {
        string path = "Assets/Outputs/" + output + "/Result.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, path);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        GameObject loaded = PrefabUtility.LoadPrefabContents(path);
        try { VerifyMeshes(source, Read(loaded), "Assets/Outputs/" + output + "/"); }
        finally { PrefabUtility.UnloadPrefabContents(loaded); }
        Require(!AssetDatabase.GetDependencies(path, true).Contains(forbiddenDependency), "saved prefab must not reference " + forbiddenDependency);
    }

    private static GameObject Particle(string name, Mesh[] meshes)
    {
        var go = new GameObject(name, typeof(ParticleSystem));
        var particles = go.GetComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.SetMeshes(meshes);
        return go;
    }

    private static Mesh[] Read(GameObject go)
    {
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        var result = new Mesh[renderer.meshCount];
        Require(renderer.GetMeshes(result) == result.Length, "mesh array read count");
        return result;
    }

    private static Mesh MeshAsset(string name, float scale)
    {
        return new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right * scale, Vector3.up * scale }, triangles = new[] { 0, 1, 2 } };
    }

    private static long Id(Mesh mesh)
    {
        Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out _, out long id), "valid mesh local ID");
        return id;
    }

    private static void Folder(string path)
    {
        if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
