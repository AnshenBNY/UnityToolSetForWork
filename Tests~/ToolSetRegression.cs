using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public static class ToolSetRegression
{
    static readonly List<string> results = new List<string>();
    static int failures;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static void Set(object o, string field, object value) => o.GetType().GetField(field, Instance).SetValue(o, value);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Case(string name, Action test)
    {
        try { test(); results.Add("PASS " + name); }
        catch (Exception e) { failures++; results.Add("FAIL " + name + ": " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }
    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        if (parent != "Assets") Folder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
    static DefaultAsset Dir(string path) { Folder(path); return AssetDatabase.LoadAssetAtPath<DefaultAsset>(path); }
    static Material Mat(string path, Texture texture = null)
    {
        Folder(Path.GetDirectoryName(path).Replace('\\', '/'));
        var m = new Material(Shader.Find("Unlit/Texture")); if (texture != null) m.mainTexture = texture;
        AssetDatabase.CreateAsset(m, path); AssetDatabase.SaveAssetIfDirty(m); return m;
    }
    static Texture2D Tex(string path, Color color)
    {
        Folder(Path.GetDirectoryName(path).Replace('\\', '/'));
        var tex = new Texture2D(2, 2); tex.SetPixels(new[] {color,color,color,color}); tex.Apply();
        AssetDatabase.CreateAsset(tex,path); AssetDatabase.SaveAssetIfDirty(tex); return tex;
    }
    static void RunPlan(ToolPlan plan)
    {
        Require(plan.errors.Count == 0, string.Join(";", plan.errors));
        string error = plan.validate?.Invoke(); Require(string.IsNullOrEmpty(error), error);
        foreach (var p in plan.fingerprints) Require(ToolPaths.Fingerprint(p.Key) == p.Value, "plan stale: " + p.Key);
        var transaction = new ToolFileTransaction(plan.title);
        try { foreach (var step in plan.steps) step.Run(transaction); transaction.Complete(); plan.completed?.Invoke(); }
        catch { plan.rollback?.Invoke(); transaction.Rollback(); throw; }
    }
    public static void Run()
    {
        ParticleMeshRegression.Run();
        string previous = File.ReadAllText("regression-results.txt");
        if (!previous.Contains("Failures: 0")) { failures++; results.Add(previous); }
        else results.Add("PASS previous 7 particle mesh regressions");
        Case("Unique planned copy paths and folder boundaries", () =>
        {
            Folder("Assets/Work/Out"); File.WriteAllText("Assets/Work/Out/M_Copy.asset", "occupied");
            var paths = new CopyPathAllocator();
            string a = paths.Reserve("Assets/A/M.asset", "Assets/Work/Out");
            string b = paths.Reserve("Assets/B/M.asset", "Assets/Work/Out");
            Require(a != b && a != "Assets/Work/Out/M_Copy.asset", "same filenames must not collide");
            Require(paths.Reserve("Assets/A/M.asset", "Assets/Work/Out") == a, "same source reuses reservation");
            Require(!ToolPaths.Contains("Assets/FX", "Assets/FX_Backup/a.asset"), "folder boundary");
        });
        Case("Two same-name textures migrate independently and preserve material scale", () =>
        {
            var a = Tex("Assets/TexturesA/Duplicate.asset", Color.red);
            var b = Tex("Assets/TexturesB/Duplicate.asset", Color.green);
            var ma = Mat("Assets/Migration/A.mat", a); var mb = Mat("Assets/Migration/B.mat", b);
            Mat("Assets/Migration/Unchanged.mat");
            ma.mainTextureScale = new Vector2(3,4); AssetDatabase.SaveAssetIfDirty(ma);
            var tool = new MaterialTextureMigrator();
            Set(tool,"sourceMaterialsFolder",Dir("Assets/Migration")); Set(tool,"targetTexturesFolder",Dir("Assets/MigratedTextures"));
            var plan = tool.BuildPlan(); RunPlan(plan);
            Require(ma.mainTexture != a && mb.mainTexture != b && ma.mainTexture != mb.mainTexture, "distinct copied texture references");
            Require(ma.mainTextureScale == new Vector2(3,4), "texture scale preserved");
            Require(plan.steps.Count == 4, "two copies and two edits; unchanged is skipped");
            ToolFileTransaction.RestoreLast();
            ma = AssetDatabase.LoadAssetAtPath<Material>("Assets/Migration/A.mat");
            Require(ma.mainTexture == a, "restore original material reference");
            Require(AssetDatabase.FindAssets("t:Texture",new[]{"Assets/MigratedTextures"}).Length == 0,"remove created textures and metas");
        });
        Case("GUID text is untouched and object references can be truly cleared", () =>
        {
            var a = Mat("Assets/Repair/Old.mat"); var b = Mat("Assets/Repair/New.mat");
            var go = new GameObject(AssetDatabase.AssetPathToGUID("Assets/Repair/Old.mat"));
            go.AddComponent<MeshRenderer>().sharedMaterial = a; new GameObject(go.name).transform.SetParent(go.transform);
            PrefabUtility.SaveAsPrefabAsset(go,"Assets/Repair/Owner.prefab"); Object.DestroyImmediate(go);
            string old = AssetDatabase.AssetPathToGUID("Assets/Repair/Old.mat");
            RunPlan(ReferenceRepair.Build("replace",old,"Assets/Repair/Old.mat","Assets/Repair/New.mat",new[]{"Assets/Repair/Owner.prefab"}));
            var owner = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Repair/Owner.prefab");
            Require(owner.transform.GetChild(0).name == old, "plain GUID string must not change");
            Require(owner.GetComponent<MeshRenderer>().sharedMaterial == b, "material replaced");
            RunPlan(ReferenceRepair.Build("clear",AssetDatabase.AssetPathToGUID("Assets/Repair/New.mat"),"Assets/Repair/New.mat","",new[]{"Assets/Repair/Owner.prefab"},true));
            owner = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Repair/Owner.prefab");
            Require(owner.GetComponent<MeshRenderer>().sharedMaterial == null,"reference cleared");
            Require(File.ReadAllText("Assets/Repair/Owner.prefab").Contains("- {fileID: 0}"),"serialized null fileID");
        });
        Case("Missing FBX subasset repair is blocked without old identity", () =>
        {
            string fbx = "Assets/FbxFixtures/fish_200004005_skin.fbx";
            string guid = Guid.NewGuid().ToString("N");
            File.WriteAllText("Assets/Repair/Unsafe.asset","%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n  mesh: {fileID: 4300000, guid: "+guid+", type: 3}\n");
            var plan = ReferenceRepair.Build("unsafe",guid,"Assets/Missing.fbx",fbx,new[]{"Assets/Repair/Unsafe.asset"});
            Require(plan.errors.Count > 0,"missing subasset must be blocked");
        });
        Case("Full dependency copy: real FBX multi-mesh, shape, skinned, raw image, nested exclusion and weights", () =>
        {
            Folder("Assets/Graph"); Dir("Assets/GraphOut");
            string fbx = "Assets/FbxFixtures/fish_200004005_skin.fbx";
            var meshes = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().Where(m=>m.subMeshCount==1).Take(2).ToArray();
            Require(meshes.Length==2,"two real FBX meshes");
            var texture = Tex("Assets/Graph/Image.asset",Color.blue);
            var material = Mat("Assets/Graph/Particle.mat",texture);
            var sharedGo = new GameObject("Shared"); sharedGo.AddComponent<MeshRenderer>().sharedMaterial = material;
            Folder("Assets/Shared");
            var shared = PrefabUtility.SaveAsPrefabAsset(sharedGo,"Assets/Shared/Child.prefab"); Object.DestroyImmediate(sharedGo);
            string originalShared = File.ReadAllText("Assets/Shared/Child.prefab");
            var root = new GameObject("Graph");
            var ps = root.AddComponent<ParticleSystem>(); ps.Stop();
            var renderer = root.GetComponent<ParticleSystemRenderer>(); renderer.renderMode=ParticleSystemRenderMode.Mesh;
            renderer.SetMeshes(meshes); renderer.meshDistribution=ParticleSystemMeshDistribution.NonUniformRandom; renderer.SetMeshWeightings(new[]{.25f,.75f});
            renderer.sharedMaterial=material;
            var shape=ps.shape; shape.enabled=true; shape.shapeType=ParticleSystemShapeType.Mesh; shape.mesh=meshes[1];
            var skin = new GameObject("Skin"); skin.transform.SetParent(root.transform); skin.AddComponent<SkinnedMeshRenderer>().sharedMesh=meshes[0];
            var ui = new GameObject("UI",typeof(RectTransform));ui.transform.SetParent(root.transform); ui.AddComponent<RawImage>().texture=texture;
            PrefabUtility.InstantiatePrefab(shared,root.transform);
            var source=PrefabUtility.SaveAsPrefabAsset(root,"Assets/Graph/Root.prefab"); Object.DestroyImmediate(root);
            string sourceText=File.ReadAllText("Assets/Graph/Root.prefab"), output=null;
            var plan=PrefabCopyPlan.Build(source,Dir("Assets/GraphOut"),new[]{Dir("Assets/Shared")}, p=>output=p);
            RunPlan(plan);
            Require(File.ReadAllText("Assets/Graph/Root.prefab")==sourceText && File.ReadAllText("Assets/Shared/Child.prefab")==originalShared,"source and shared unchanged");
            var actualCopies=AssetDatabase.FindAssets("",new[]{"Assets/GraphOut"}).Select(AssetDatabase.GUIDToAssetPath).Where(p=>!AssetDatabase.IsValidFolder(p)).ToArray();
            Require(plan.overview.CopyCount==actualCopies.Length,"overview counts resources rather than internal operations");
            Require(plan.overview.resources.Where(r=>!r.Shared).Select(r=>r.destination).OrderBy(p=>p).SequenceEqual(actualCopies.OrderBy(p=>p)),"overview destinations match saved assets");
            Require(plan.overview.resources.Select(r=>r.source).Distinct().Count()==plan.overview.resources.Count,"one overview row per source asset");
            Require(plan.overview.resources.Any(r=>r.Shared&&r.source=="Assets/Shared/Child.prefab"),"shared prefab shown separately");
            var copied=PrefabUtility.LoadPrefabContents(output);
            try
            {
                var cr=copied.GetComponent<ParticleSystemRenderer>(); var actual=new Mesh[cr.meshCount];cr.GetMeshes(actual);
                Require(actual.Length==2 && actual.All(m=>m!=null && AssetDatabase.GetAssetPath(m)!=fbx),"all meshes replaced");
                float[] weights=new float[2];cr.GetMeshWeightings(weights);Require(Mathf.Approximately(weights[0],.25f)&&Mathf.Approximately(weights[1],.75f),"weights preserved");
                Require(copied.GetComponent<ParticleSystem>().shape.mesh!=meshes[1],"shape mesh replaced");
                Require(copied.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh!=meshes[0],"skinned mesh replaced");
                Require(copied.GetComponentInChildren<RawImage>().texture!=texture,"raw image copied");
                Require(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(copied.transform.Find(shared.name).gameObject)=="Assets/Shared/Child.prefab","shared child remains connected");
            }
            finally {PrefabUtility.UnloadPrefabContents(copied);}
            // Same source repeated creates a separate copy, never reuses an unrelated existing asset.
            string second=null;RunPlan(PrefabCopyPlan.Build(source,Dir("Assets/GraphOut"),new[]{Dir("Assets/Shared")},p=>second=p));
            Require(output!=second,"repeat unique output");
        });
        Case("Save root copy preserves shared nested prefab and instance connection", () =>
        {
            var shared=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Shared/Child.prefab");
            string before=File.ReadAllText("Assets/Shared/Child.prefab");
            var root=new GameObject("SafeRoot"); PrefabUtility.InstantiatePrefab(shared,root.transform);
            var asset=PrefabUtility.SaveAsPrefabAsset(root,"Assets/GraphOut/SaveRoot.prefab");Object.DestroyImmediate(root);
            var instance=(GameObject)PrefabUtility.InstantiatePrefab(asset);
            try
            {
                instance.transform.GetChild(0).gameObject.SetActive(false);
                var tool=new PrefabCopyTool();Set(tool,"rootPrefab",instance);Set(tool,"rootPrefabCopyPath","Assets/GraphOut/SaveRoot.prefab");
                Require((bool)typeof(PrefabCopyTool).GetMethod("TrySaveCurrentPrefab",Instance).Invoke(tool,new object[]{false}),"save succeeded");
                Require(File.ReadAllText("Assets/Shared/Child.prefab")==before,"shared prefab unchanged");
                Require(PrefabUtility.IsAnyPrefabInstanceRoot(instance),"root still connected");
                var loaded=PrefabUtility.LoadPrefabContents("Assets/GraphOut/SaveRoot.prefab");
                Require(!loaded.transform.GetChild(0).gameObject.activeSelf,"override persisted in root");PrefabUtility.UnloadPrefabContents(loaded);
            }
            finally { Object.DestroyImmediate(instance); }
        });
        Case("Rename collision preflight and file GUID preservation", () =>
        {
            var a=Mat("Assets/Rename/A.mat");var b=Mat("Assets/Rename/B.mat");Mat("Assets/Rename/Result_1.mat");
            var tool=new BatchRenameTool();Set(tool,"newBaseName","Result");Selection.objects=new Object[]{a,b};
            var blocked=tool.BuildPlan();Require(blocked.errors.Count>0,"occupied name blocked before writes");
            Set(tool,"newBaseName","Changed");var plan=tool.BuildPlan();
            string guid=AssetDatabase.AssetPathToGUID("Assets/Rename/A.mat");RunPlan(plan);
            Require(AssetDatabase.GUIDToAssetPath(guid)=="Assets/Rename/Changed_1.mat","GUID preserved");
            ToolFileTransaction.RestoreLast();Require(AssetDatabase.GUIDToAssetPath(guid)=="Assets/Rename/A.mat","rename restore preserves GUID");
        });
        Case("Hierarchy rename filters persistent assets and supports Undo", () =>
        {
            var asset=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Graph/Root.prefab");
            var node=new GameObject("Before");
            try
            {
                Selection.objects=new Object[]{asset,node};
                var tool=new BatchRenameTool();Set(tool,"renameTarget",RenameTarget.Hierarchy节点);Set(tool,"newBaseName","Scene");
                var plan=tool.BuildPlan();Require(plan.steps.Count==1,"only scene node");
                RunPlan(plan);Undo.FlushUndoRecordObjects();Require(node.name=="Scene_1","scene renamed");Undo.PerformUndo();Require(node.name=="Before","scene undo");
            }
            finally {Object.DestroyImmediate(node);}
        });
        Case("Model importer rejects invalid errors and missing Avatar", () =>
        {
            Selection.activeObject=AssetDatabase.LoadMainAssetAtPath("Assets/FbxFixtures/fish_200004005_skin.fbx");
            var tool=new BatchChangeModelImport();Set(tool,"applyAnimationError",true);Set(tool,"animationScaleError",float.NaN);
            Require(tool.BuildPlan().errors.Count>0,"invalid error blocked");
            Set(tool,"animationScaleError",.5f);Set(tool,"applyAvatarDefinition",true);Set(tool,"avatarDefinition",AvatarDefinition.CopyFromOtherAvatar);
            Require(tool.BuildPlan().errors.Count>0,"missing avatar blocked");
            Set(tool,"applyAvatarDefinition",false);Set(tool,"applyAnimationError",false);Set(tool,"importMode",ImportMode.None);
            string path="Assets/FbxFixtures/fish_200004005_skin.fbx";string meta=File.ReadAllText(path+".meta");
            RunPlan(tool.BuildPlan());Require(((ModelImporter)AssetImporter.GetAtPath(path)).materialImportMode==ModelImporterMaterialImportMode.None,"public importer API applied");
            ToolFileTransaction.RestoreLast();Require(File.ReadAllText(path+".meta")==meta,"meta restored");
        });
        Case("Renderer mapping supports roots and refuses ambiguous paths", () =>
        {
            var a=new GameObject("Source");var b=new GameObject("Target");
            try
            {
                var m=AssetDatabase.LoadAssetAtPath<Material>("Assets/Graph/Particle.mat");a.AddComponent<MeshRenderer>().sharedMaterial=m;b.AddComponent<MeshRenderer>();
                var tool=new PrefabMatProjectTool();Set(tool,"source",a);Set(tool,"target",b);Set(tool,"fxToggle",false);Set(tool,"matToggle",true);
                RunPlan(tool.BuildPlan());Require(b.GetComponent<MeshRenderer>().sharedMaterial==m,"root MeshRenderer mapped");
                new GameObject("Same").transform.SetParent(b.transform);new GameObject("Same").transform.SetParent(b.transform);
                bool rejected=false;try{PrefabMatProjectTool.FindUnique(b.transform,"Same");}catch(InvalidOperationException){rejected=true;}Require(rejected,"ambiguous child refused");
            }
            finally {Object.DestroyImmediate(a);Object.DestroyImmediate(b);}
        });
        Case("Backup refuses to overwrite later edits", () =>
        {
            var material=Mat("Assets/Recover/Test.mat");string path="Assets/Recover/Test.mat";
            var tx=new ToolFileTransaction("test");tx.Capture(path);File.AppendAllText(path,"\n# operation\n");tx.Complete();File.AppendAllText(path,"# later edit\n");
            string expected=File.ReadAllText(path);bool blocked=false;
            try{ToolFileTransaction.RestoreLast();}catch(IOException){blocked=true;}
            Require(blocked&&File.ReadAllText(path)==expected,"later edits protected");
        });
        Case("Workflow rolls back failed writes and releases update/progress state", () =>
        {
            string path="Assets/Recover/Test.mat";string before=File.ReadAllText(path);
            var plan=new ToolPlan{title="failure injection"};plan.Watch(path);
            plan.Add("write",tx=>{tx.Capture(path);File.AppendAllText(path,"# temporary\n");});
            plan.Add("fail",tx=>{throw new IOException("intentional failure");});
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);
            ToolWorkflow.Start();
            var tick=typeof(ToolWorkflow).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Static);
            tick.Invoke(null,null);tick.Invoke(null,null);
            Require(!ToolWorkflow.Running && File.ReadAllText(path)==before,"rollback restored file and stopped");
            Require(ToolWorkflow.Status.Contains("已恢复"),"honest result");
        });

        Case("Workflow keeps completed output when a review step reports a problem", () =>
        {
            string path="Assets/Recover/Test.mat";string before=File.ReadAllText(path);
            var plan=new ToolPlan{title="non-fatal review"};plan.Watch(path);
            plan.Add("write",tx=>{tx.Capture(path);File.AppendAllText(path,"# retained\n");});
            plan.Add("review",tx=>{throw new IOException("old dependency");},keepChangesOnFailure:true);
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);
            ToolWorkflow.Start();
            var tick=typeof(ToolWorkflow).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Static);
            tick.Invoke(null,null);tick.Invoke(null,null);
            Require(!ToolWorkflow.Running&&plan.phase==ToolPlanPhase.Succeeded,"review problem should not fail the plan");
            Require(File.ReadAllText(path).Contains("# retained"),"completed output was rolled back");
            Require(plan.errors.Any(e=>e.Contains("old dependency"))&&plan.steps[1].state.StartsWith("需处理"),"review problem was not reported");
            Require(ToolWorkflow.Status.Contains("结果已保留"),"result status does not explain retained output");
            File.WriteAllText(path,before);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
        });

        Case("Model importer external material remap points to copied material", () =>
        {
            string model="Assets/FbxFixtures/fish_200004005_skin.fbx";
            var importer=(ModelImporter)AssetImporter.GetAtPath(model);
            var external=Mat("Assets/Graph/External.mat");
            var embedded=AssetDatabase.LoadAllAssetsAtPath(model).OfType<Material>().First();
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material),embedded.name),external);importer.SaveAndReimport();
            var instance=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(model));
            Folder("Assets/RemapRoot");
            var root=new GameObject("Wrapper");instance.transform.SetParent(root.transform);
            var source=PrefabUtility.SaveAsPrefabAsset(root,"Assets/RemapRoot/Root.prefab");Object.DestroyImmediate(root);
            string output=null;
            RunPlan(PrefabCopyPlan.Build(source,Dir("Assets/RemapOut"),new DefaultAsset[0],p=>output=p));
            string copiedModel=AssetDatabase.GetDependencies(output,true).First(p=>p.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase));
            var mappings=((ModelImporter)AssetImporter.GetAtPath(copiedModel)).GetExternalObjectMap();
            Require(mappings.Values.OfType<Material>().Any(m=>m!=external&&AssetDatabase.GetAssetPath(m).StartsWith("Assets/RemapOut/")),"external importer material replaced");
            Require(importer.GetExternalObjectMap().Values.Contains(external),"source importer unchanged");
        });
        Case("Distinct same-name subassets map by identity and candidate ambiguity is rejected", () =>
        {
            Folder("Assets/SubRepair");
            Mesh Mesh(string name,float x) { var m=new Mesh{name=name};m.vertices=new[]{Vector3.zero,Vector3.right*x,Vector3.up};m.triangles=new[]{0,1,2};return m; }
            var oldRoot=Mesh("OldRoot",1);AssetDatabase.CreateAsset(oldRoot,"Assets/SubRepair/Old.asset");
            var oldSub=Mesh("Shard",2);AssetDatabase.AddObjectToAsset(oldSub,oldRoot);
            var newRoot=Mesh("NewRoot",3);AssetDatabase.CreateAsset(newRoot,"Assets/SubRepair/New.asset");
            var newSub=Mesh("Shard",4);AssetDatabase.AddObjectToAsset(newSub,newRoot);AssetDatabase.SaveAssets();
            var go=new GameObject("SubOwner");go.AddComponent<MeshFilter>().sharedMesh=oldSub;
            PrefabUtility.SaveAsPrefabAsset(go,"Assets/SubRepair/Owner.prefab");Object.DestroyImmediate(go);
            var plan=ReferenceRepair.Build("subasset",AssetDatabase.AssetPathToGUID("Assets/SubRepair/Old.asset"),"Assets/SubRepair/Old.asset","Assets/SubRepair/New.asset",new[]{"Assets/SubRepair/Owner.prefab"});
            RunPlan(plan);
            Require(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SubRepair/Owner.prefab").GetComponent<MeshFilter>().sharedMesh==newSub,"correct subasset assigned");
            AssetDatabase.AddObjectToAsset(Mesh("Shard",5),newRoot);AssetDatabase.SaveAssets();
            var copied=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SubRepair/Owner.prefab");
            go=(GameObject)PrefabUtility.InstantiatePrefab(copied);go.GetComponent<MeshFilter>().sharedMesh=oldSub;PrefabUtility.SaveAsPrefabAsset(go,"Assets/SubRepair/Owner.prefab");Object.DestroyImmediate(go);
            plan=ReferenceRepair.Build("ambiguous",AssetDatabase.AssetPathToGUID("Assets/SubRepair/Old.asset"),"Assets/SubRepair/Old.asset","Assets/SubRepair/New.asset",new[]{"Assets/SubRepair/Owner.prefab"});
            Require(plan.errors.Count>0,"ambiguous subassets blocked");
        });
        Case("Copied references restore GUID plus fileID and original files remain untouched", () =>
        {
            var go=new GameObject("CopyOwner");go.AddComponent<MeshRenderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>("Assets/Graph/Particle.mat");
            PrefabUtility.SaveAsPrefabAsset(go,"Assets/Repair/CopyOwner.prefab");Object.DestroyImmediate(go);
            string path="Assets/Graph/Particle.mat",before=File.ReadAllText(path);string output="Assets/Repair/ParticleCopied.mat";
            RunPlan(ReferenceRepair.Build("copy repair",AssetDatabase.AssetPathToGUID(path),path,path,new[]{"Assets/Repair/CopyOwner.prefab"},identicalCopy:true,copyDestination:output));
            Require(AssetDatabase.GetAssetPath(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Repair/CopyOwner.prefab").GetComponent<MeshRenderer>().sharedMaterial)==output,"reference targets copied material");
            Require(File.ReadAllText(path)==before,"source material unchanged");
        });
        Case("Nested prefab mapping keeps transforms and source connection; repeat skips duplicates", () =>
        {
            var a=new GameObject("MapSource");var b=new GameObject("MapTarget");
            try
            {
                var child=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Shared/Child.prefab"),a.transform);
                child.transform.localPosition=new Vector3(2,3,4);PrefabUtility.RecordPrefabInstancePropertyModifications(child.transform);
                var tool=new PrefabMatProjectTool();Set(tool,"source",a);Set(tool,"target",b);
                var plan=tool.BuildPlan();RunPlan(plan);
                Require(b.transform.childCount==1&&b.transform.GetChild(0).localPosition==new Vector3(2,3,4),"copy preserves local transform");
                Require(PrefabUtility.IsPartOfPrefabInstance(child),"source still connected");
                Require(PrefabUtility.IsPartOfPrefabInstance(b.transform.GetChild(0)),"target prefab connected");
                Require(tool.BuildPlan().steps.Count==0,"repeat does not duplicate");
            }
            finally {Object.DestroyImmediate(a);Object.DestroyImmediate(b);}
        });
        Case("Workflow cancellation restores created assets and folders, preview detects stale files", () =>
        {
            var plan=new ToolPlan{title="cancel test"};
            plan.Add("create",tx=>{tx.CaptureNewFolder("Assets/CancelFolder");AssetDatabase.CreateFolder("Assets","CancelFolder");tx.Capture("Assets/CancelFolder/A.mat");AssetDatabase.CopyAsset("Assets/Graph/Particle.mat","Assets/CancelFolder/A.mat");});
            plan.Add("pending",tx=>{throw new Exception("should not execute");});
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);ToolWorkflow.Start();
            var tick=typeof(ToolWorkflow).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Static);tick.Invoke(null,null);
            typeof(ToolWorkflow).GetField("cancel",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,true);tick.Invoke(null,null);
            Require(!ToolWorkflow.Running&&!Directory.Exists("Assets/CancelFolder")&&!File.Exists("Assets/CancelFolder.meta"),"cancel restores assets and folder metas");
            var stale=new ToolPlan{title="stale"};stale.Watch("Assets/Recover/Test.mat");stale.Add("do not execute",tx=>{throw new Exception("should not execute");});
            File.AppendAllText("Assets/Recover/Test.mat","\n# changed after preview\n");
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,stale);ToolWorkflow.Start();
            Require(!ToolWorkflow.Running&&stale.errors.Count>0,"stale preview blocked");
        });


        Case("Saving copied prefab does not save unrelated dirty materials", () =>
        {
            var unrelated=Mat("Assets/Recover/Unrelated.mat");string path="Assets/Recover/Unrelated.mat";string before=File.ReadAllText(path);
            unrelated.renderQueue=3101;EditorUtility.SetDirty(unrelated);
            var asset=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GraphOut/SaveRoot.prefab");var instance=(GameObject)PrefabUtility.InstantiatePrefab(asset);
            try
            {
                var tool=new PrefabCopyTool();Set(tool,"rootPrefab",instance);Set(tool,"rootPrefabCopyPath","Assets/GraphOut/SaveRoot.prefab");
                Require((bool)typeof(PrefabCopyTool).GetMethod("TrySaveCurrentPrefab",Instance).Invoke(tool,new object[]{false}),"save succeeds");
                Require(File.ReadAllText(path)==before && EditorUtility.IsDirty(unrelated),"unrelated material stays unsaved");
            }
            finally {Object.DestroyImmediate(instance);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);}
        });
        Case("Dependency report resolves moved root scopes by GUID", () =>
        {
            var material=Mat("Assets/Moved/Before.mat");string guid=AssetDatabase.AssetPathToGUID("Assets/Moved/Before.mat");
            var record=new DependencyRecordFile();
            record.roots.Add(new AssetRecordItem{path="Assets/Moved/Before.mat",guid=guid});
            record.dependencies.Add(new DependencyRecordItem{path="Assets/Moved/Before.mat",guid=guid,referencedByRoots=new List<string>{"Assets/Moved/Before.mat"}});
            Require(string.IsNullOrEmpty(AssetDatabase.MoveAsset("Assets/Moved/Before.mat","Assets/Moved/After.mat")),"move fixture");
            var report=(DependencyCheckReport)typeof(DependencyRecordTool).GetMethod("BuildCheckReport",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{record,"fixture"});
            Require(report.rootPaths.Single()=="Assets/Moved/After.mat" && report.items.Single().referencedByRoots.Single()=="Assets/Moved/After.mat","repair scope follows GUID after move");
        });


        Case("Scene recovery failure does not prevent file rollback", () =>
        {
            string path="Assets/Recover/Test.mat",before=File.ReadAllText(path);
            var plan=new ToolPlan{title="partial recovery fault"};plan.Watch(path);
            plan.rollback=()=>{throw new InvalidOperationException("intentional scene recovery failure");};
            plan.Add("modify",tx=>{tx.Capture(path);File.AppendAllText(path,"\n# transient\n");});
            plan.Add("fail",tx=>{throw new IOException("intentional step failure");});
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);ToolWorkflow.Start();
            var tick=typeof(ToolWorkflow).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Static);tick.Invoke(null,null);tick.Invoke(null,null);
            Require(File.ReadAllText(path)==before&&!ToolWorkflow.Running,"files recovered independently of scene failure");
            Require(ToolWorkflow.Status.Contains("恢复失败"),"partial recovery reported accurately");
        });
        Case("Manual recovery protects unsaved material edits", () =>
        {
            string path="Assets/Recover/Dirty.mat";var material=Mat(path);var tx=new ToolFileTransaction("dirty test");tx.Capture(path);tx.Complete();
            material.renderQueue=3200;EditorUtility.SetDirty(material);
            bool blocked=false;try{ToolFileTransaction.RestoreLast();}catch(InvalidOperationException){blocked=true;}
            Require(blocked&&material.renderQueue==3200,"unsaved material change protected");
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
        });

        Case("Changing copy settings invalidates the prepared operation before writing", () =>
        {
            var tool=new PrefabCopyTool(); tool.AddSourcePrefabs(new[]{AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Graph/Root.prefab")});
            Set(tool,"outputFolder",Dir("Assets/PreparedCopyA"));
            var plan=tool.PrepareCompleteCopy();
            Require(!Directory.EnumerateFileSystemEntries("Assets/PreparedCopyA").Any(),"preparing creates no asset or classification folders");
            Set(tool,"outputFolder",Dir("Assets/PreparedCopyB"));
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);
            ToolWorkflow.Start();
            Require(!ToolWorkflow.Running&&plan.phase==ToolPlanPhase.Failed,"stale settings block execution");
            Require(!Directory.EnumerateFileSystemEntries("Assets/PreparedCopyA").Any()&&!Directory.EnumerateFileSystemEntries("Assets/PreparedCopyB").Any(),"neither output receives unconfirmed copies");
            ToolWorkflow.DiscardPreparedPlan(PrefabCopyTool.CompleteWorkflowOwner);
            Require(ToolWorkflow.Plan==null,"stale copy summary can be discarded");
        });
        Case("Completed operations cannot be accidentally started twice", () =>
        {
            int executions=0;
            var plan=new ToolPlan{title="single execution",owner=nameof(PrefabCopyTool)};
            plan.Add("execute once",tx=>executions++);
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);
            ToolWorkflow.Start();
            typeof(ToolWorkflow).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);
            Require(!ToolWorkflow.Running&&plan.phase==ToolPlanPhase.Succeeded&&executions==1,"completion changes action state");
            ToolWorkflow.Start();
            Require(!ToolWorkflow.Running&&executions==1,"second start is ignored");
        });

        Case("Batch copy deduplicates shared FBX/materials, preserves nested and variant roots, and restores the whole batch", () =>
        {
            var material=AssetDatabase.LoadAssetAtPath<Material>("Assets/Graph/Particle.mat");
            string fbx="Assets/FbxFixtures/fish_200004005_skin.fbx";
            var meshes=AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().Where(m=>m.subMeshCount==1).Take(2).ToArray();
            Folder("Assets/BatchSources/A");Folder("Assets/BatchSources/B");
            var child=new GameObject("Twin");child.AddComponent<ParticleSystem>().Stop();
            var renderer=child.GetComponent<ParticleSystemRenderer>();renderer.renderMode=ParticleSystemRenderMode.Mesh;
            renderer.SetMeshes(new[]{meshes[1],meshes[0]});renderer.sharedMaterial=material;
            renderer.meshDistribution=ParticleSystemMeshDistribution.NonUniformRandom;renderer.SetMeshWeightings(new[]{.2f,.8f});
            var b=PrefabUtility.SaveAsPrefabAsset(child,"Assets/BatchSources/B/Twin.prefab");Object.DestroyImmediate(child);
            var parent=new GameObject("Twin");parent.AddComponent<MeshRenderer>().sharedMaterial=material;
            PrefabUtility.InstantiatePrefab(b,parent.transform);
            PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Shared/Child.prefab"),parent.transform);
            var a=PrefabUtility.SaveAsPrefabAsset(parent,"Assets/BatchSources/A/Twin.prefab");Object.DestroyImmediate(parent);
            var variantInstance=(GameObject)PrefabUtility.InstantiatePrefab(b);variantInstance.SetActive(false);
            var variant=PrefabUtility.SaveAsPrefabAsset(variantInstance,"Assets/BatchSources/Variant.prefab");Object.DestroyImmediate(variantInstance);
            Require(PrefabUtility.GetPrefabAssetType(variant)==PrefabAssetType.Variant,"fixture is a variant");
            var output=Dir("Assets/BatchOut");Folder("Assets/BatchOut/Prefabs");
            Require(AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(b),"Assets/BatchOut/Prefabs/Twin_Copy.prefab"),"occupied output fixture");
            var existing=Directory.GetFiles("Assets/BatchOut","*",SearchOption.AllDirectories).ToDictionary(p=>p,ToolPaths.Fingerprint);
            var originals=new[]{a,b,variant}.SelectMany(o=>AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(o),true))
                .Where(File.Exists).SelectMany(p=>new[]{p,p+".meta"}).Distinct().Where(File.Exists).ToDictionary(p=>p,ToolPaths.Fingerprint);
            string[] outputs=null;
            var plan=PrefabCopyPlan.Build(new[]{a,b,variant,a},output,new[]{Dir("Assets/Shared")},p=>outputs=p);
            Require(plan.overview.roots.Count==3,"duplicate root removed, nested selected root remains a result");
            Require(plan.overview.resources.Select(r=>r.source).Distinct().Count()==plan.overview.resources.Count,"batch resource union deduplicated");
            Require(plan.overview.resources.Count(r=>r.source==fbx)==1&&plan.overview.resources.Count(r=>r.source==AssetDatabase.GetAssetPath(material))==1,"one mesh and material copy for the batch");
            RunPlan(plan);
            Require(outputs.Length==3&&outputs.Distinct().Count()==3&&outputs.SequenceEqual(plan.overview.ResultPaths),"all requested root results are exposed in input order");
            Require(outputs[0]!=outputs[1]&&outputs.All(p=>p!="Assets/BatchOut/Prefabs/Twin_Copy.prefab"),"same-name roots avoid each other and existing files");
            var copiedA=PrefabUtility.LoadPrefabContents(outputs[0]);
            try
            {
                var nested=copiedA.transform.Find("Twin").gameObject;
                Require(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(nested)==outputs[1],"selected nested root references the single copied root");
                Require(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(copiedA.transform.Find("Child").gameObject)=="Assets/Shared/Child.prefab","excluded prefab stays shared");
                var copiedMaterial=copiedA.GetComponent<MeshRenderer>().sharedMaterial;
                Require(copiedMaterial!=material&&nested.GetComponent<ParticleSystemRenderer>().sharedMaterial==copiedMaterial,"shared material remains shared between copied roots");
            }
            finally{PrefabUtility.UnloadPrefabContents(copiedA);}
            var copiedB=AssetDatabase.LoadAssetAtPath<GameObject>(outputs[1]);var copiedRenderer=copiedB.GetComponent<ParticleSystemRenderer>();
            var actual=new Mesh[copiedRenderer.meshCount];copiedRenderer.GetMeshes(actual);float[] weights=new float[actual.Length];copiedRenderer.GetMeshWeightings(weights);
            Require(actual.Length==2&&actual.All(m=>m!=null&&AssetDatabase.GetAssetPath(m)!=fbx)&&actual[0].name==meshes[1].name&&actual[1].name==meshes[0].name,"all batch mesh slots remapped in order");
            Require(Mathf.Approximately(weights[0],.2f)&&Mathf.Approximately(weights[1],.8f),"batch mesh weights preserved");
            Require(PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(outputs[2]))==PrefabAssetType.Variant&&AssetDatabase.GetDependencies(outputs[2],false).Contains(outputs[1]),"variant inherits the copied base");
            Require(originals.All(p=>ToolPaths.Fingerprint(p.Key)==p.Value),"all original files and metas unchanged");
            ToolFileTransaction.RestoreLast();
            Require(Directory.GetFiles("Assets/BatchOut","*",SearchOption.AllDirectories).OrderBy(p=>p).SequenceEqual(existing.Keys.OrderBy(p=>p))&&existing.All(p=>ToolPaths.Fingerprint(p.Key)==p.Value),"one restore removes whole batch including metas and preserves existing output");
            Require(Directory.GetDirectories("Assets/BatchOut").Length==1,"only pre-existing classification folder remains");
        });

        Case("Batch source validation blocks empty, missing, scene, model and excluded roots before writing", () =>
        {
            var valid=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/A/Twin.prefab");var output=Dir("Assets/BatchInvalidOut");
            var scene=new GameObject("Scene input");var model=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FbxFixtures/fish_200004005_skin.fbx");
            try
            {
                foreach(var inputs in new[]{Array.Empty<GameObject>(),new[]{valid,(GameObject)null},new[]{valid,scene},new[]{valid,model}})
                {
                    bool blocked=false;try{PrefabCopyPlan.Build(inputs,output,Array.Empty<DefaultAsset>(),null);}catch(InvalidOperationException){blocked=true;}
                    Require(blocked,"invalid batch blocked before producing a plan");
                }
                bool excluded=false;try{PrefabCopyPlan.Build(new[]{valid},output,new[]{Dir("Assets/BatchSources/A")},null);}catch(InvalidOperationException){excluded=true;}
                Require(excluded&&!Directory.EnumerateFileSystemEntries("Assets/BatchInvalidOut").Any(),"excluded requested root blocks whole batch, output stays empty");
            }
            finally{Object.DestroyImmediate(scene);}
        });

        Case("Batch list changes and edits to the second source invalidate prepared copies", () =>
        {
            var a=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/A/Twin.prefab");var b=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/B/Twin.prefab");
            var tool=new PrefabCopyTool();tool.AddSourcePrefabs(new[]{a,b});Set(tool,"outputFolder",Dir("Assets/BatchStaleOut"));
            var plan=tool.PrepareCompleteCopy();Set(tool,"sourcePrefabs",new List<GameObject>{a});
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);ToolWorkflow.Start();
            Require(!ToolWorkflow.Running&&plan.phase==ToolPlanPhase.Failed,"source list change blocks old batch");
            Set(tool,"sourcePrefabs",new List<GameObject>{a,b});plan=tool.PrepareCompleteCopy();
            string path=AssetDatabase.GetAssetPath(b),before=File.ReadAllText(path);
            try
            {
                File.AppendAllText(path,"\n# changed after batch preparation\n");
                typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);ToolWorkflow.Start();
                Require(!ToolWorkflow.Running&&plan.phase==ToolPlanPhase.Failed,"later source fingerprint is checked too");
                Require(!Directory.EnumerateFileSystemEntries("Assets/BatchStaleOut").Any(),"stale batch does not write files");
            }
            finally{File.WriteAllText(path,before);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);}
        });

        Case("Batch failure rolls back all roots and shared resources without publishing partial results", () =>
        {
            var sources=new[]{AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/A/Twin.prefab"),AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/B/Twin.prefab")};
            bool published=false;var plan=PrefabCopyPlan.Build(sources,Dir("Assets/BatchFailedOut"),Array.Empty<DefaultAsset>(),p=>published=true);
            string previousBackup=EditorPrefs.GetString(ToolPaths.PreferenceKey("LastBackup"));
            plan.Add("intentional batch failure",tx=>{throw new IOException("intentional batch failure after all copies");});
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);ToolWorkflow.Start();
            var tick=typeof(ToolWorkflow).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Static);
            for(int i=0;ToolWorkflow.Running&&i<=plan.steps.Count;i++)tick.Invoke(null,null);
            Require(!ToolWorkflow.Running&&plan.phase==ToolPlanPhase.Failed&&!published,"failed batch never publishes successful roots");
            Require(!Directory.EnumerateFileSystemEntries("Assets/BatchFailedOut").Any(),"all copied resources, files, folders and metas rolled back");
            Require(EditorPrefs.GetString(ToolPaths.PreferenceKey("LastBackup"))==previousBackup,"failed batch does not replace last successful backup");
        });

        Case("Batch cancellation after copying both roots restores the entire transaction", () =>
        {
            var sources=new[]{AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/A/Twin.prefab"),AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/B/Twin.prefab")};
            bool published=false;var plan=PrefabCopyPlan.Build(sources,Dir("Assets/BatchCancelOut"),Array.Empty<DefaultAsset>(),p=>published=true);
            typeof(ToolWorkflow).GetProperty("Plan",BindingFlags.Public|BindingFlags.Static).SetValue(null,plan);ToolWorkflow.Start();
            var tick=typeof(ToolWorkflow).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Static);
            for(int i=0;ToolWorkflow.Running&&i<plan.steps.Count;i++)
            {
                tick.Invoke(null,null);
                if(plan.overview.ResultPaths.All(File.Exists))break;
            }
            Require(ToolWorkflow.Running&&plan.overview.ResultPaths.All(File.Exists),"both root files exist before cancellation");
            typeof(ToolWorkflow).GetField("cancel",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,true);tick.Invoke(null,null);
            Require(plan.phase==ToolPlanPhase.Cancelled&&!published&&!Directory.EnumerateFileSystemEntries("Assets/BatchCancelOut").Any(),"cancel removes all batch files and metas");
        });

        Case("Batch results instantiate independently with one Undo and any generated root can be saved", () =>
        {
            var tool=new PrefabCopyTool();tool.AddSourcePrefabs(new[]{AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/A/Twin.prefab"),AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/B/Twin.prefab")});
            Set(tool,"outputFolder",Dir("Assets/BatchSceneOut"));var plan=tool.PrepareCompleteCopy();RunPlan(plan);
            tool.PlaceCopiesInScene(plan.overview.ResultPaths);
            var instances=Selection.gameObjects;
            try
            {
                Require(instances.Length==2&&instances.All(o=>o.transform.parent==null&&PrefabUtility.IsAnyPrefabInstanceRoot(o)),"two independent connected roots, no wrapper object");
                Require(instances.Select(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot).OrderBy(p=>p).SequenceEqual(plan.overview.ResultPaths.OrderBy(p=>p)),"scene roots reference their own copies");
                typeof(PrefabCopyTool).GetMethod("SelectCurrentInstance",Instance).Invoke(tool,new object[]{instances[1]});instances[1].SetActive(false);
                Require((bool)typeof(PrefabCopyTool).GetMethod("TrySaveCurrentPrefab",Instance).Invoke(tool,new object[]{false}),"save supports another generated batch root");
                Undo.PerformUndo();Require(instances.All(o=>o==null),"one undo removes every inserted root");
                Require(plan.overview.ResultPaths.All(File.Exists),"scene undo preserves prefab files");
                tool.PlaceCopiesInScene(new[]{plan.overview.ResultPaths[1]});
                Require(Selection.gameObjects.Length==1,"single result can also be placed alone");Undo.PerformUndo();
            }
            finally{foreach(var instance in instances)if(instance!=null)Object.DestroyImmediate(instance);}
        });

        Case("Batch input deduplication, typed preference round-trip, legacy migration and deliberate empty lists", () =>
        {
            var a=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/A/Twin.prefab");var b=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/BatchSources/B/Twin.prefab");
            var scene=new GameObject("Rejected scene");var tool=new PrefabCopyTool();
            try{tool.AddSourcePrefabs(new Object[]{a,b,a,scene,AssetDatabase.LoadAssetAtPath<Material>("Assets/Graph/Particle.mat")});}
            finally{Object.DestroyImmediate(scene);}
            Set(tool,"outputFolder",Dir("Assets/BatchPrefsOut"));Set(tool,"excludeFolders",new List<DefaultAsset>{Dir("Assets/Shared")});
            Require(tool.PrepareCompleteCopy().overview.roots.Count==2,"selection input filters invalid and duplicate objects");
            ToolPreferences.Save(tool);var loaded=new PrefabCopyTool();ToolPreferences.Load(loaded);
            var plan=loaded.PrepareCompleteCopy();Require(plan.overview.roots.Select(r=>r.source).SequenceEqual(new[]{AssetDatabase.GetAssetPath(a),AssetDatabase.GetAssetPath(b)}),"GameObject input list persists with its type and order");
            Require(plan.overview.resources.Any(r=>r.Shared&&r.source=="Assets/Shared/Child.prefab"),"DefaultAsset exclusions still persist with correct type");
            string key=ToolPaths.PreferenceKey(nameof(PrefabCopyTool));
            EditorPrefs.SetString(key,"{\"entries\":[{\"field\":\"rootPrefab\",\"text\":\""+AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(a))+"\"},{\"field\":\"outputFolder\",\"text\":\""+AssetDatabase.AssetPathToGUID("Assets/BatchPrefsOut")+"\"}]}");
            var legacy=new PrefabCopyTool();ToolPreferences.Load(legacy);Require(legacy.PrepareCompleteCopy().overview.roots.Count==1,"previous single-source preference migrates");
            Set(legacy,"sourcePrefabs",new List<GameObject>());Set(legacy,"rootPrefab",a);ToolPreferences.Save(legacy);
            var empty=new PrefabCopyTool();ToolPreferences.Load(empty);bool blocked=false;try{empty.PrepareCompleteCopy();}catch(InvalidOperationException){blocked=true;}
            Require(blocked,"explicitly cleared input stays empty on reload");
        });

        SceneCopyRegression.Run(Case);
        results.Add("Failures: "+failures);
        File.WriteAllText("toolset-regression-results.txt",string.Join("\n",results));Debug.Log(string.Join("\n",results));
        EditorApplication.Exit(failures==0?0:1);
    }
}
