using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class DependencyRecordRegression
{
    static readonly List<string> results = new List<string>();
    static int failures;
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Case(string name, Action action)
    {
        try { action(); results.Add("PASS " + name); }
        catch (Exception e) { failures++; results.Add("FAIL " + name + "\n" + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }
    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        if (parent != "Assets") Folder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
    static Material Material(string path)
    {
        Folder(Path.GetDirectoryName(path).Replace('\\', '/'));
        var material = new Material(Shader.Find("Unlit/Texture"));
        AssetDatabase.CreateAsset(material, path);
        AssetDatabase.SaveAssetIfDirty(material);
        return material;
    }
    static void Prefab(string path, Material material = null)
    {
        Folder(Path.GetDirectoryName(path).Replace('\\', '/'));
        var go = new GameObject("Owner");
        if (material != null) go.AddComponent<MeshRenderer>().sharedMaterial = material;
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
    }
    static DependencyCheckReport Audit(DependencyRecordFile data) => (DependencyCheckReport)
        typeof(DependencyRecordTool).GetMethod("BuildCheckReport", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { data, "fixture.json" });
    static DependencyRecordFile Record(string root, string missing, string guid, params string[] owners)
    {
        var record = new DependencyRecordFile();
        record.roots.Add(new AssetRecordItem { path = root, guid = AssetDatabase.AssetPathToGUID(root) });
        record.dependencies.Add(new DependencyRecordItem { path = missing, guid = guid, fileName = Path.GetFileName(missing), referencedByRoots = new List<string> { root } });
        foreach (var owner in owners)
            record.dependencies.Add(new DependencyRecordItem { path = owner, guid = AssetDatabase.AssetPathToGUID(owner), referencedByRoots = new List<string> { root } });
        return record;
    }
    static DependencyRecordTool Panel(DependencyCheckReport report)
    {
        var tool = new DependencyRecordTool();
        typeof(DependencyRecordTool).GetField("lastReport", Private).SetValue(tool, report);
        return tool;
    }
    static void Execute(ToolPlan plan)
    {
        Require(plan.steps.Count > 0 && plan.errors.Count == 0 && plan.blockingErrors.Count == 0, "Invalid plan: " + string.Join(";", plan.errors));
        Require(string.IsNullOrEmpty(plan.validate?.Invoke()), "Plan invalidated");
        var transaction = new ToolFileTransaction(plan.title);
        try
        {
            foreach (var step in plan.steps) step.Run(transaction);
            transaction.Complete();
            plan.completed?.Invoke();
        }
        catch { transaction.Rollback(); throw; }
    }
    public static void Run()
    {
        EditorSettings.serializationMode = SerializationMode.ForceText;
        Case("Wrapped/reordered/optional-type references are replaced, plain GUID text preserved", () =>
        {
            var target = Material("Assets/Formatting/Candidate.mat");
            string old = Guid.NewGuid().ToString("N"), next = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(target));
            string path = "Assets/Formatting/References.asset";
            string input = "%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n  note: " + old +
                "\n  first: {guid: " + old.ToUpperInvariant() + ",\n    type: 2, fileID: 2100000}" +
                "\n  second: {fileID: 2100000, guid: " + old + "}\n";
            File.WriteAllText(path, input);
            var plan = ReferenceRepair.Build("formats", old, "Assets/Old.mat", AssetDatabase.GetAssetPath(target), new[] { path });
            Require(plan.errors.Count == 0 && plan.steps.Count == 1 && plan.steps[0].description.Contains("2 处"), "Both legal reference forms must be matched");
            // Do not import the synthetic MonoBehaviour. Test the generated write through its transaction,
            // then inspect text (a missing script is expected for this fixture).
            var transaction = new ToolFileTransaction("formats");
            plan.steps.Single().execute(transaction);
            transaction.Complete();
            string output = File.ReadAllText(path);
            Require(output.Contains("note: " + old), "Plain GUID string was rewritten");
            Require(!ReferenceRepair.ReferencesGuid(path, old) && ReferenceRepair.ReferencesGuid(path, next), "Reference replacement failed");
            Require(output.Contains("first: {guid: " + next + ",\n    type: 2, fileID: 2100000}"), "Formatting lost");
            Require(output.Contains("second: {fileID: 2100000, guid: " + next + "}"), "Optional type was added");
        });

        Case("JSON retains disconnected owners; repair updates UI/counts and survives fresh audit", () =>
        {
            string root = "Assets/Disconnected/Root.prefab", owner = "Assets/Disconnected/Hidden.prefab";
            string candidate = "Assets/Disconnected/Old.mat", old = Guid.NewGuid().ToString("N");
            var target = Material(candidate);
            Prefab(root); Prefab(owner, target);
            string original = File.ReadAllText(owner).Replace(AssetDatabase.AssetPathToGUID(candidate), old);
            File.WriteAllText(owner, original);
            AssetDatabase.ImportAsset(owner, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var data = Record(root, "Assets/Original/Old.mat", old, owner);
            string json = JsonUtility.ToJson(data);
            Require(!AssetDatabase.GetDependencies(root, true).Contains(owner), "Fixture must have a broken graph edge");
            var report = Audit(data); var item = report.items.Single(i => i.guid == old); var panel = Panel(report);
            Execute(panel.BuildMissingReferenceRepair(report, item, candidate));
            Require(item.state == DependencyState.Present && item.repaired && report.missingCount == 0, "Live result not refreshed");
            Require(AssetDatabase.LoadAssetAtPath<GameObject>(owner).GetComponent<MeshRenderer>().sharedMaterial == target, "Actual serialized material not fixed");
            var fresh = Audit(JsonUtility.FromJson<DependencyRecordFile>(json));
            Require(fresh.missingCount == 0 && fresh.items.Single(i => i.guid == old).repaired, "Reparse/reopen resurrects repaired result");
            Require(JsonUtility.ToJson(data) == json && item.guid == old, "Original baseline was modified");

            Require(AssetDatabase.MoveAsset(owner, "Assets/Disconnected/Moved.prefab") == "", "Move owner failed");
            owner = "Assets/Disconnected/Moved.prefab";
            Require(AssetDatabase.MoveAsset(candidate, "Assets/Disconnected/Renamed.mat") == "", "Move candidate failed");
            candidate = "Assets/Disconnected/Renamed.mat";
            Require(Audit(data).missingCount == 0, "GUID-based repair evidence did not follow moved owners/candidate");

        });

        Case("Repair evidence does not hide restored old references or deleted candidates", () =>
        {
            string root = "Assets/Recheck/Root.prefab", candidate = "Assets/Recheck/Candidate.mat", old = Guid.NewGuid().ToString("N");
            var material = Material(candidate); Prefab(root, material);
            string original = File.ReadAllText(root).Replace(AssetDatabase.AssetPathToGUID(candidate), old);
            File.WriteAllText(root, original); AssetDatabase.ImportAsset(root, ImportAssetOptions.ForceSynchronousImport);
            var data = Record(root, "Assets/Old/Candidate.mat", old);
            var report = Audit(data); var item = report.items.Single();
            Execute(Panel(report).BuildMissingReferenceRepair(report, item, candidate));
            Require(Audit(data).missingCount == 0, "Not resolved after repair");
            ToolFileTransaction.RestoreLast();
            Require(Audit(data).missingCount == 1 && ReferenceRepair.ReferencesGuid(root, old), "Backup restore hidden by ledger");
            report = Audit(data); item = report.items.Single();
            Execute(Panel(report).BuildMissingReferenceRepair(report, item, candidate));
            AssetDatabase.DeleteAsset(candidate);
            Require(Audit(data).missingCount == 1, "Deleted replacement incorrectly reported as repaired");
        });

        Case("Moved root and legacy empty referencedByRoots resolve to the current scope", () =>
        {
            string root = "Assets/MovedScope/Before.prefab", candidate = "Assets/MovedScope/Candidate.mat", old = Guid.NewGuid().ToString("N");
            var material = Material(candidate); Prefab(root, material);
            File.WriteAllText(root, File.ReadAllText(root).Replace(AssetDatabase.AssetPathToGUID(candidate), old));
            AssetDatabase.ImportAsset(root, ImportAssetOptions.ForceSynchronousImport);
            var data = Record(root, "Assets/Old/Candidate.mat", old);
            data.dependencies.Single().referencedByRoots.Clear();
            Require(AssetDatabase.MoveAsset(root, "Assets/MovedScope/After.prefab") == "", "Move failed");
            var report = Audit(data); var item = report.items.Single();
            Execute(Panel(report).BuildMissingReferenceRepair(report, item, candidate));
            Require(report.missingCount == 0 && Audit(data).missingCount == 0, "Legacy root fallback failed");
        });

        Case("Importer meta references participate in repair, verification and backup restore", () =>
        {
            string candidate = "Assets/Importer/Candidate.mat", root = "Assets/Importer/Texture.png", old = Guid.NewGuid().ToString("N");
            var material = Material(candidate);
            var texture = new Texture2D(2, 2); File.WriteAllBytes(root, texture.EncodeToPNG()); Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(root, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(root);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "Candidate"), material);
            importer.SaveAndReimport();
            string original = File.ReadAllText(root + ".meta").Replace(AssetDatabase.AssetPathToGUID(candidate), old);
            File.WriteAllText(root + ".meta", original);
            Require(ReferenceRepair.ReferencesGuid(root + ".meta", old), "Importer fixture has no object reference");
            var data = Record(root, "Assets/Old/Candidate.mat", old);
            var report = Audit(data); var item = report.items.Single();
            Execute(Panel(report).BuildMissingReferenceRepair(report, item, candidate));
            Require(report.missingCount == 0 && Audit(data).missingCount == 0, "Meta repair was not verified");
            Require(AssetImporter.GetAtPath(root).GetExternalObjectMap().Values.Contains(material), "Importer map did not update");
            ToolFileTransaction.RestoreLast();
            Require(Audit(data).missingCount == 1, "Meta restore was hidden");
        });

        Case("Wrong extensions are excluded; new report invalidates pending repair", () =>
        {
            string root = "Assets/Candidates/Root.prefab", candidate = "Assets/Candidates/UniqueCandidate.mat", old = Guid.NewGuid().ToString("N");
            var material = Material(candidate); Prefab(root, material);
            var texture = new Texture2D(2, 2); File.WriteAllBytes("Assets/Candidates/UniqueCandidate.png", texture.EncodeToPNG()); Object.DestroyImmediate(texture);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            File.WriteAllText(root, File.ReadAllText(root).Replace(AssetDatabase.AssetPathToGUID(candidate), old));
            AssetDatabase.ImportAsset(root, ImportAssetOptions.ForceSynchronousImport);
            var data = Record(root, "Assets/Old/UniqueCandidate.mat", old);
            var report = Audit(data); var item = report.items.Single();
            typeof(DependencyRecordTool).GetMethod("SearchCandidatesForMissing", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { report, 20 });
            Require(item.candidatePaths.SequenceEqual(new[] { candidate }), "Different extension offered as a candidate");
            var panel = Panel(report); var plan = panel.BuildMissingReferenceRepair(report, item, candidate);
            typeof(DependencyRecordTool).GetField("lastReport", Private).SetValue(panel, Audit(data));
            Require(!string.IsNullOrEmpty(plan.validate()), "Pending plan survives new audit");
        });

        Case("Ambiguous missing subasset IDs remain blocked", () =>
        {
            string candidate = "Assets/Ambiguous/Mesh.asset", old = Guid.NewGuid().ToString("N");
            Folder("Assets/Ambiguous");
            AssetDatabase.CreateAsset(new Mesh { name = "Mesh" }, candidate);
            string owner = "Assets/Ambiguous/Owner.asset";
            File.WriteAllText(owner, "%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n  mesh: {fileID: 123456789, guid: " + old + ", type: 3}\n");
            var plan = ReferenceRepair.Build("ambiguous", old, "Assets/Missing.fbx", candidate, new[] { owner });
            Require(plan.steps.Count == 0 && plan.errors.Count > 0 && ReferenceRepair.ReferencesGuid(owner, old), "Ambiguous identity guessed");
        });
        Case("Shared repair preserves copy GUID/fileID behavior and writes true null references", () =>
        {
            string source = "Assets/SharedRepair/Source.mat", owner = "Assets/SharedRepair/Owner.prefab", copy = "Assets/SharedRepair/Copy.mat";
            var material = Material(source); Prefab(owner, material);
            string sourceGuid = AssetDatabase.AssetPathToGUID(source);
            Execute(ReferenceRepair.Build("copy", sourceGuid, source, source, new[] { owner }, identicalCopy: true, copyDestination: copy));
            Require(!ReferenceRepair.ReferencesGuid(owner, sourceGuid) && ReferenceRepair.ReferencesGuid(owner, AssetDatabase.AssetPathToGUID(copy)), "Copy retained original GUID");
            Require(AssetDatabase.LoadAssetAtPath<GameObject>(owner).GetComponent<MeshRenderer>().sharedMaterial == AssetDatabase.LoadAssetAtPath<Material>(copy), "Copy GUID/fileID cannot load");
            Execute(ReferenceRepair.Build("clear", AssetDatabase.AssetPathToGUID(copy), copy, "", new[] { owner }, clear: true));
            Require(AssetDatabase.LoadAssetAtPath<GameObject>(owner).GetComponent<MeshRenderer>().sharedMaterial == null && File.ReadAllText(owner).Contains("- {fileID: 0}"), "Null replacement is not a true null reference");
        });
        Case("Legacy repairs use current dependency evidence; candidate names alone never resolve missing items", () =>
        {
            string root = "Assets/Legacy/Root.prefab", candidate = "Assets/Legacy/Old.mat", old = Guid.NewGuid().ToString("N");
            var material = Material(candidate); Prefab(root, material);
            var data = Record(root, "Assets/Original/Old.mat", old);
            Require(Audit(data).items.Single().repaired && Audit(data).missingCount == 0, "Already repaired legacy dependency was resurrected");
            Prefab(root);
            Require(Audit(data).missingCount == 1, "Unused same-name candidate falsely resolved missing item");
            Prefab(root, material);
            File.WriteAllText(root, File.ReadAllText(root).Replace(AssetDatabase.AssetPathToGUID(candidate), old));
            AssetDatabase.ImportAsset(root, ImportAssetOptions.ForceSynchronousImport);
            Require(Audit(data).missingCount == 1, "Remaining old GUID reference hidden by existing candidate");
            File.WriteAllText(root, File.ReadAllText(root).Replace(old, AssetDatabase.AssetPathToGUID(candidate)).Replace("fileID: 2100000", "fileID: 123456"));
            AssetDatabase.ImportAsset(root, ImportAssetOptions.ForceSynchronousImport);
            Require(Audit(data).missingCount == 1, "Invalid replacement fileID falsely inferred as a legacy repair");
        });
        Case("JSON-only repair keeps unrelated roots intact and reports remaining old references", () =>
        {
            string root = "Assets/Scope/Root.prefab", unrelated = "Assets/Scope/Unrelated.prefab", blocked = "Assets/Scope/Blocked.prefab";
            string candidate = "Assets/Scope/Candidate.mat", old = Guid.NewGuid().ToString("N");
            var material = Material(candidate); Prefab(root, material); Prefab(unrelated, material); Prefab(blocked, material);
            string candidateGuid = AssetDatabase.AssetPathToGUID(candidate);
            foreach (var path in new[] { root, unrelated, blocked })
            {
                File.WriteAllText(path, File.ReadAllText(path).Replace(candidateGuid, old));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            var data = Record(root, "Assets/Old/Candidate.mat", old, blocked);
            data.roots.Add(new AssetRecordItem { path = unrelated, guid = AssetDatabase.AssetPathToGUID(unrelated) });
            // The blocked owner remains in the JSON scope but has an unresolvable local object ID.
            File.WriteAllText(blocked, File.ReadAllText(blocked).Replace("fileID: 2100000", "fileID: 123456"));
            var report = Audit(data); var item = report.items.Single(i => i.guid == old);
            var plan = Panel(report).BuildMissingReferenceRepair(report, item, candidate);
            Require(plan.steps.Count == 1 && plan.errors.Count == 1, "Scoped valid/invalid owners were not separated");
            var transaction = new ToolFileTransaction("partial");
            foreach (var step in plan.steps) step.Run(transaction);
            transaction.Complete(); plan.completed();
            Require(report.missingCount == 1 && !item.repaired && Audit(data).missingCount == 1, "Partial repair incorrectly marked complete");
            Require(ReferenceRepair.ReferencesGuid(unrelated, old), "Unrelated root changed in JSON-only mode");
        });
        results.Add("Failures: " + failures);
        File.WriteAllText("dependency-record-regression-results.txt", string.Join("\n", results));
        Debug.Log(string.Join("\n", results));
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }
}
