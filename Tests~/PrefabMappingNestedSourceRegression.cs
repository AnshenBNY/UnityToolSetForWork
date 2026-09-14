using System;
using System.IO;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEngine;

public static class PrefabMappingNestedSourceRegression
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        try
        {
            Folder("Assets/MappingRegression");
            var childObject = new GameObject("NestedChild");
            var childAsset = PrefabUtility.SaveAsPrefabAsset(childObject, "Assets/MappingRegression/NestedChild.prefab");
            UnityEngine.Object.DestroyImmediate(childObject);
            var otherObject = new GameObject("NestedChild");
            var otherAsset = PrefabUtility.SaveAsPrefabAsset(otherObject, "Assets/MappingRegression/OtherChild.prefab");
            UnityEngine.Object.DestroyImmediate(otherObject);

            var parentObject = new GameObject("Parent");
            PrefabUtility.InstantiatePrefab(childAsset, parentObject.transform);
            var parentAsset = PrefabUtility.SaveAsPrefabAsset(parentObject, "Assets/MappingRegression/Parent.prefab");
            UnityEngine.Object.DestroyImmediate(parentObject);

            var source = (GameObject)PrefabUtility.InstantiatePrefab(parentAsset);
            var target = new GameObject("Target");
            try
            {
                var tool = new PrefabMatProjectTool();
                Set(tool, "source", source);
                Set(tool, "target", target);
                Set(tool, "fxToggle", true);
                Set(tool, "matToggle", false);
                var plan = tool.BuildPlan();
                Require(plan.errors.Count == 0, string.Join("；", plan.errors));
                var transaction = new ToolFileTransaction(plan.title);
                foreach (var step in plan.steps) step.Run(transaction);
                transaction.Complete();

                Require(target.transform.childCount == 1, "目标下没有生成唯一的嵌套预制体");
                var copied = target.transform.GetChild(0).gameObject;
                string copiedPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(copied);
                Require(copiedPath == "Assets/MappingRegression/NestedChild.prefab",
                    "复制源错误，实际得到：" + copiedPath);

                var conflictTarget = new GameObject("ConflictTarget");
                var staleTarget = new GameObject("StaleTarget");
                try
                {
                    var otherInstance = (GameObject)PrefabUtility.InstantiatePrefab(otherAsset, conflictTarget.transform);
                    otherInstance.name = source.transform.GetChild(0).name;
                    var conflictTool = new PrefabMatProjectTool();
                    Set(conflictTool, "source", source);
                    Set(conflictTool, "target", conflictTarget);
                    Set(conflictTool, "fxToggle", true);
                    Set(conflictTool, "matToggle", false);
                    var conflictPlan = conflictTool.BuildPlan();
                    Require(conflictPlan.steps.Count == 0 && conflictPlan.errors.Count == 1,
                        $"同名但来源不同的节点没有作为冲突跳过：步骤 {conflictPlan.steps.Count}，问题 {conflictPlan.errors.Count}，{string.Join("；", conflictPlan.errors)}");

                    var staleTool = new PrefabMatProjectTool();
                    Set(staleTool, "source", source);
                    Set(staleTool, "target", staleTarget);
                    Set(staleTool, "fxToggle", true);
                    Set(staleTool, "matToggle", false);
                    var stalePlan = staleTool.BuildPlan();
                    source.transform.GetChild(0).gameObject.SetActive(!source.transform.GetChild(0).gameObject.activeSelf);
                    Require(!string.IsNullOrEmpty(stalePlan.validate?.Invoke()), "源层级变化后旧预览仍可执行");

                    var renameNode = new GameObject("RenameNode");
                    try
                    {
                        Selection.activeGameObject = renameNode;
                        var renameTool = new BatchRenameTool();
                        Set(renameTool, "renameTarget", RenameTarget.Hierarchy节点);
                        Set(renameTool, "newBaseName", "Before");
                        var renamePlan = renameTool.BuildPlan();
                        Set(renameTool, "newBaseName", "After");
                        Require(!string.IsNullOrEmpty(renamePlan.validate?.Invoke()), "重命名规则变化后旧预览仍可执行");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(renameNode); }

                    Folder("Assets/MappingRegression/SourceMats");
                    Folder("Assets/MappingRegression/TargetA");
                    Folder("Assets/MappingRegression/TargetB");
                    var migrationTool = new MaterialTextureMigrator();
                    Set(migrationTool, "sourceMaterialsFolder", AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets/MappingRegression/SourceMats"));
                    Set(migrationTool, "targetTexturesFolder", AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets/MappingRegression/TargetA"));
                    var migrationPlan = migrationTool.BuildPlan();
                    Set(migrationTool, "targetTexturesFolder", AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets/MappingRegression/TargetB"));
                    Require(!string.IsNullOrEmpty(migrationPlan.validate?.Invoke()), "贴图迁移目录变化后旧预览仍可执行");

                    Selection.objects = Array.Empty<UnityEngine.Object>();
                    var importerTool = new BatchChangeModelImport();
                    var importerPlan = importerTool.BuildPlan();
                    Set(importerTool, "importMode", ImportMode.None);
                    Require(!string.IsNullOrEmpty(importerPlan.validate?.Invoke()), "模型导入设置变化后旧预览仍可执行");

                    Folder("Assets/MappingRegression/CopyOutput");
                    EditorUtility.SetDirty(parentAsset);
                    try
                    {
                        var copyPlan = PrefabCopyPlan.Build(parentAsset,
                            AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets/MappingRegression/CopyOutput"),
                            Array.Empty<DefaultAsset>(), _ => { });
                        Require(copyPlan.blockingErrors.Count > 0, "存在未保存源资源时复制计划没有阻断执行");
                    }
                    finally { EditorUtility.ClearDirty(parentAsset); }

                    var missing = new DependencyCheckItem
                    {
                        fileName = "NoCandidate_987654321.prefab",
                        state = DependencyState.Missing
                    };
                    var report = new DependencyCheckReport();
                    report.items.Add(missing);
                    typeof(DependencyRecordTool).GetMethod("SearchCandidatesForMissing", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { report, 5 });
                    Require(missing.candidateSearchCompleted && missing.candidatePaths.Count == 0,
                        "候选搜索无结果时仍被标记成尚未搜索");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(conflictTarget);
                    UnityEngine.Object.DestroyImmediate(staleTarget);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(target);
            }
            Finish(null);
        }
        catch (Exception e) { Finish(e); }
    }

    static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, Fields).SetValue(target, value);

    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        if (parent != "Assets") Folder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Finish(Exception error)
    {
        string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "prefab-mapping-results.txt");
        File.WriteAllText(path, error == null ? "PASS" : "FAIL\n" + error);
        EditorApplication.Exit(error == null ? 0 : 1);
    }
}
