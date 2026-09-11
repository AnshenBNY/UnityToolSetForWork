using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.UI;
using Object = UnityEngine.Object;

internal static class SceneCopyRegression
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    static GameObject source;
    static Material material;
    static Mesh[] meshes;
    static Sprite sprite;
    static AnimatorController controller;
    static TimelineAsset timeline;
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Fields).SetValue(obj, value);
    static List<GameObject> Inputs(PrefabCopyTool tool) => (List<GameObject>)typeof(PrefabCopyTool).GetField("sceneInstances", Fields).GetValue(tool);
    static DefaultAsset Folder(string path)
    {
        if (!AssetDatabase.IsValidFolder(path))
        {
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (parent != "Assets") Folder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
        return AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
    }
    static GameObject[] Instances() => new[] { (GameObject)PrefabUtility.InstantiatePrefab(source), (GameObject)PrefabUtility.InstantiatePrefab(source) };
    static void Destroy(IEnumerable<GameObject> instances) { foreach (var instance in instances) if (instance != null) Object.DestroyImmediate(instance); }
    static PrefabCopyTool Tool(GameObject[] instances, string output)
    {
        var tool = new PrefabCopyTool(); tool.AddSceneInstances(instances); Set(tool, "outputFolder", Folder(output)); return tool;
    }
    static void Module(PrefabCopyTool tool, string name, GameObject instance) => typeof(PrefabCopyTool).GetMethod(name, Fields).Invoke(tool, new object[] { instance });
    static void RunPlan(ToolPlan plan)
    {
        Require(plan.errors.Count == 0, string.Join(";", plan.errors));
        Require(string.IsNullOrEmpty(plan.validate?.Invoke()), "prepared scene plan must be valid");
        var transaction = new ToolFileTransaction(plan.title);
        try { foreach (var step in plan.steps) step.Run(transaction); transaction.Complete(); plan.completed?.Invoke(); }
        catch { try { plan.rollback?.Invoke(); } finally { transaction.Rollback(); } throw; }
    }
    static void Start(ToolPlan plan)
    {
        typeof(ToolWorkflow).GetProperty("Plan", BindingFlags.Public | BindingFlags.Static).SetValue(null, plan); ToolWorkflow.Start();
    }
    static void Tick() => typeof(ToolWorkflow).GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
    static string Mods(GameObject go) => string.Join("|", (PrefabUtility.GetPropertyModifications(go) ?? Array.Empty<PropertyModification>())
        .Select(m => (m.target == null ? 0 : m.target.GetInstanceID()) + ":" + m.propertyPath + ":" + m.value + ":" + (m.objectReference == null ? 0 : m.objectReference.GetInstanceID())).OrderBy(s => s));

    static void Fixture()
    {
        Folder("Assets/SceneModules");
        var texture = new Texture2D(2, 2); texture.SetPixels(new[] { Color.red, Color.blue, Color.green, Color.white }); texture.Apply();
        AssetDatabase.CreateAsset(texture, "Assets/SceneModules/Texture.asset");
        sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * .5f); sprite.name = "Icon";
        AssetDatabase.CreateAsset(sprite, "Assets/SceneModules/Icon.asset");
        material = new Material(Shader.Find("Unlit/Texture")); material.mainTexture = texture;
        AssetDatabase.CreateAsset(material, "Assets/SceneModules/Material.mat");
        meshes = AssetDatabase.LoadAllAssetsAtPath("Assets/FbxFixtures/fish_200004005_skin.fbx").OfType<Mesh>().Where(m => m.subMeshCount == 1).Take(2).ToArray();
        var clip = new AnimationClip(); clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
        AssetDatabase.CreateAsset(clip, "Assets/SceneModules/Motion.anim");
        controller = AnimatorController.CreateAnimatorControllerAtPath("Assets/SceneModules/Controller.controller"); controller.AddMotion(clip);
        timeline = ScriptableObject.CreateInstance<TimelineAsset>(); AssetDatabase.CreateAsset(timeline, "Assets/SceneModules/Sequence.playable");
        var track = timeline.CreateTrack<AnimationTrack>(null, "Animation"); track.CreateClip(clip);
        AssetDatabase.SaveAssets();
        var root = new GameObject("SceneModuleRoot");
        root.AddComponent<MeshRenderer>().sharedMaterial = material; root.AddComponent<MeshFilter>().sharedMesh = meshes[0];
        var animator = root.AddComponent<Animator>(); animator.runtimeAnimatorController = controller;
        var director = root.AddComponent<PlayableDirector>(); director.playOnAwake = false; director.playableAsset = timeline; director.SetGenericBinding(track, animator);
        var particle = new GameObject("Particle"); particle.transform.SetParent(root.transform); particle.AddComponent<ParticleSystem>().Stop();
        var renderer = particle.GetComponent<ParticleSystemRenderer>(); renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.sharedMaterial = material; renderer.trailMaterial = material; renderer.SetMeshes(meshes);
        renderer.meshDistribution = ParticleSystemMeshDistribution.NonUniformRandom; renderer.SetMeshWeightings(new[] { .3f, .7f });
        var ui = new GameObject("Image", typeof(RectTransform)); ui.transform.SetParent(root.transform); ui.AddComponent<Image>().sprite = sprite;
        source = PrefabUtility.SaveAsPrefabAsset(root, "Assets/SceneModules/Root.prefab"); Object.DestroyImmediate(root);
    }

    public static void Run(Action<string, Action> test)
    {
        test("Scene batch selection filters assets/preview objects and collapses nested selections", () =>
        {
            Fixture(); var instances = Instances(); var plain = new GameObject("not a prefab");
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(source, instances[0].transform);
            var preview = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(source));
            try
            {
                var tool = new PrefabCopyTool(); tool.AddSceneInstances(new Object[] { nested, instances[0], instances[1], instances[0], source, plain, preview, instances[1].transform.GetChild(0).gameObject });
                Require(Inputs(tool).SequenceEqual(instances), "same-asset scene instances stay distinct; parent covers nested selection; invalid inputs rejected");
                ToolPreferences.Save(tool); var reloaded = new PrefabCopyTool(); ToolPreferences.Load(reloaded); Require(Inputs(reloaded).Count == 0, "scene references never persist as project inputs");
            }
            finally { PrefabUtility.UnloadPrefabContents(preview); Object.DestroyImmediate(plain); Destroy(instances); }
        });

        var modules = new[] { "CopyMaterialsFromMeshRender", "CopyMaterialsFromParticle", "CopyMeshFromParticle", "CopyImageAndTextResources", "CopyMeshFromMeshFilter", "CopyAnimationAndTimeline" };
        foreach (string method in modules)
            test("Scene batch module shares one resource set: " + method, () =>
            {
                var instances = Instances(); string output = "Assets/SceneOut/" + method;
                var tool = Tool(instances, output); var before = File.ReadAllText(AssetDatabase.GetAssetPath(source));
                try
                {
                    var plan = tool.BuildSceneCopyPlan(method, instance => Module(tool, method, instance));
                    Require(plan.overview.sceneTargets.Count == 2 && !Directory.EnumerateFileSystemEntries(output).Any(), "both targets visible and preparation makes no files");
                    RunPlan(plan);
                    Func<GameObject, Object> reference;
                    switch (method)
                    {
                        case "CopyMaterialsFromMeshRender": reference = o => o.GetComponent<MeshRenderer>().sharedMaterial; break;
                        case "CopyMaterialsFromParticle": reference = o => o.GetComponentInChildren<ParticleSystemRenderer>(true).sharedMaterial; break;
                        case "CopyMeshFromParticle": reference = o => o.GetComponentInChildren<ParticleSystemRenderer>(true).mesh; break;
                        case "CopyImageAndTextResources": reference = o => o.GetComponentInChildren<Image>(true).sprite; break;
                        case "CopyMeshFromMeshFilter": reference = o => o.GetComponent<MeshFilter>().sharedMesh; break;
                        default: reference = o => o.GetComponent<Animator>().runtimeAnimatorController; break;
                    }
                    var a = reference(instances[0]); var b = reference(instances[1]);
                    Require(a != null && a == b && ToolPaths.Contains(output, AssetDatabase.GetAssetPath(a)), "both instances refer to the same copied resource");
                    if (method == "CopyMeshFromParticle")
                        foreach (var instance in instances)
                        {
                            var r = instance.GetComponentInChildren<ParticleSystemRenderer>(true); var slots = new Mesh[r.meshCount]; r.GetMeshes(slots); var weights = new float[slots.Length]; r.GetMeshWeightings(weights);
                            Require(slots.Length == 2 && slots.All(m => m != null && ToolPaths.Contains(output, AssetDatabase.GetAssetPath(m))) && slots[1].name == meshes[1].name, "every particle mesh slot is remapped");
                            Require(Mathf.Approximately(weights[0], .3f) && Mathf.Approximately(weights[1], .7f), "weights preserved in both instances");
                        }
                    if (method == "CopyAnimationAndTimeline")
                    {
                        Require(instances[0].GetComponent<PlayableDirector>().playableAsset == instances[1].GetComponent<PlayableDirector>().playableAsset, "timeline copied once");
                        foreach (var instance in instances)
                        {
                            var director = instance.GetComponent<PlayableDirector>(); var copiedTrack = ((TimelineAsset)director.playableAsset).GetOutputTracks().First();
                            Require(director.GetGenericBinding(copiedTrack) == instance.GetComponent<Animator>(), "each timeline remains bound to its own scene animator");
                        }
                    }
                    Require(File.ReadAllText(AssetDatabase.GetAssetPath(source)) == before, "source prefab unchanged by scene operations");
                    var outputFiles = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".meta")).ToArray();
                    Require(!outputFiles.Any(p => Path.GetFileNameWithoutExtension(p).EndsWith("_Copy_1")), "shared batch does not make numbered duplicate files");
                }
                finally { Destroy(instances); }
            });

        test("Scene batch preparation detects changed lists, hierarchy and second-instance values", () =>
        {
            var instances = Instances(); var tool = Tool(instances, "Assets/SceneStale");
            try
            {
                var plan = tool.BuildSceneCopyPlan("material", o => Module(tool, "CopyMaterialsFromMeshRender", o));
                instances[1].GetComponent<MeshRenderer>().enabled = false; Require(!string.IsNullOrEmpty(plan.validate()), "second instance change invalidates plan");
                plan = tool.BuildSceneCopyPlan("material", o => Module(tool, "CopyMaterialsFromMeshRender", o));
                new GameObject("new child").transform.SetParent(instances[1].transform); Require(!string.IsNullOrEmpty(plan.validate()), "added hierarchy invalidates plan");
                plan = tool.BuildSceneCopyPlan("material", o => Module(tool, "CopyMaterialsFromMeshRender", o));
                Inputs(tool).RemoveAt(1); Start(plan); Require(!ToolWorkflow.Running && plan.phase == ToolPlanPhase.Failed, "changed list prevents execution");
                Require(!Directory.EnumerateFileSystemEntries("Assets/SceneStale").Any(), "stale plan produces no files");
            }
            finally { Destroy(instances); }
        });

        foreach (bool cancel in new[] { false, true })
            test(cancel ? "Scene batch cancellation restores references and prefab overrides for the entire batch" : "Scene batch later failure restores references, overrides and all generated files", () =>
            {
                var instances = Instances(); string output = cancel ? "Assets/SceneCancel" : "Assets/SceneFailure"; var tool = Tool(instances, output);
                instances[0].GetComponent<MeshRenderer>().enabled = false; PrefabUtility.RecordPrefabInstancePropertyModifications(instances[0].GetComponent<MeshRenderer>());
                var beforeMods = instances.Select(Mods).ToArray(); int calls = 0;
                try
                {
                    var plan = tool.BuildSceneCopyPlan("material", o => { Module(tool, "CopyMaterialsFromMeshRender", o); if (!cancel && ++calls == 2) throw new IOException("intentional failure in second scene instance"); });
                    Start(plan); Tick(); Tick();
                    Require(instances[0].GetComponent<MeshRenderer>().sharedMaterial != material, "first instance changed before abort");
                    if (cancel) typeof(ToolWorkflow).GetField("cancel", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
                    Tick();
                    Require(!ToolWorkflow.Running && plan.phase == (cancel ? ToolPlanPhase.Cancelled : ToolPlanPhase.Failed), "batch ended in expected state");
                    Require(instances.All(o => o.GetComponent<MeshRenderer>().sharedMaterial == material), "all original references restored");
                    Require(instances.Select(Mods).SequenceEqual(beforeMods), "pre-existing prefab overrides preserved without stale copy references");
                    Require(!Directory.EnumerateFileSystemEntries(output).Any(), "generated resources, folders and metas all removed");
                }
                finally { Destroy(instances); }
            });

        test("Successful scene batch reference edits support one Undo", () =>
        {
            var instances = Instances(); var tool = Tool(instances, "Assets/SceneUndo");
            try
            {
                RunPlan(tool.BuildSceneCopyPlan("material", o => Module(tool, "CopyMaterialsFromMeshRender", o)));
                Undo.PerformUndo(); Require(instances.All(o => o.GetComponent<MeshRenderer>().sharedMaterial == material), "one undo restores both instances");
                Require(Directory.GetFiles("Assets/SceneUndo", "*.mat", SearchOption.AllDirectories).Length == 1, "scene undo keeps copied file assets");
            }
            finally { Destroy(instances); }
        });

        test("Scene batch saves multiple generated roots and rejects duplicate destination instances", () =>
        {
            var tool = new PrefabCopyTool(); tool.AddSourcePrefabs(new[] { source, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/B/Twin.prefab") }); Set(tool, "outputFolder", Folder("Assets/SceneSave"));
            var copy = tool.PrepareCompleteCopy(); RunPlan(copy); tool.PlaceCopiesInScene(copy.overview.ResultPaths); var instances = Inputs(tool).ToArray();
            GameObject duplicate = null;
            try
            {
                Require(instances.Length == 2, "putting batch into scene fills the entire scene input list");
                foreach (var instance in instances) { instance.GetComponent<Renderer>().enabled = false; PrefabUtility.RecordPrefabInstancePropertyModifications(instance.GetComponent<Renderer>()); }
                var save = tool.BuildSceneSavePlan(); Require(save.overview.sceneTargets.Count == 2, "save summary includes both scene targets"); RunPlan(save);
                Require(copy.overview.ResultPaths.All(p => !AssetDatabase.LoadAssetAtPath<GameObject>(p).GetComponent<Renderer>().enabled), "both generated prefab files saved");
                duplicate = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(copy.overview.ResultPaths[0])); tool.AddSceneInstances(new[] { duplicate });
                bool blocked = false; try { tool.BuildSceneSavePlan(); } catch (InvalidOperationException e) { blocked = e.Message.Contains("同一个副本文件"); }
                Require(blocked, "two instances targeting one file are rejected before writes");
            }
            finally { if (duplicate != null) Object.DestroyImmediate(duplicate); Destroy(instances); }
        });

        test("Scene batch save failure restores both files while retaining unsaved scene changes", () =>
        {
            var tool = new PrefabCopyTool(); tool.AddSourcePrefabs(new[] { source, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/B/Twin.prefab") }); Set(tool, "outputFolder", Folder("Assets/SceneSaveRollback"));
            var copy = tool.PrepareCompleteCopy(); RunPlan(copy); tool.PlaceCopiesInScene(copy.overview.ResultPaths); var instances = Inputs(tool).ToArray();
            var originals = copy.overview.ResultPaths.SelectMany(p => new[] { p, p + ".meta" }).ToDictionary(p => p, ToolPaths.Fingerprint);
            try
            {
                foreach (var instance in instances) { instance.GetComponent<Renderer>().enabled = false; PrefabUtility.RecordPrefabInstancePropertyModifications(instance.GetComponent<Renderer>()); }
                var save = tool.BuildSceneSavePlan(); save.Add("intentional save failure", tx => { throw new IOException("intentional batch save failure"); });
                bool failed = false; try { RunPlan(save); } catch (IOException) { failed = true; }
                Require(failed && originals.All(p => ToolPaths.Fingerprint(p.Key) == p.Value), "all prefab files and metas restored");
                Require(instances.All(o => !o.GetComponent<Renderer>().enabled), "artist's unsaved scene changes retained after rollback");
                var originalInstance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                try { Inputs(tool).Clear(); tool.AddSceneInstances(new[] { originalInstance }); bool blocked = false; try { tool.BuildSceneSavePlan(); } catch (InvalidOperationException) { blocked = true; } Require(blocked, "original source prefab cannot be overwritten by batch save"); }
                finally { Object.DestroyImmediate(originalInstance); }
            }
            finally { Destroy(instances); }
        });
    }
}
