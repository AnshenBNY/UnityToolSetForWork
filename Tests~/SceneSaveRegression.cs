using System;
using System.IO;
using System.Reflection;
using ToolSet;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class SceneSaveRegression
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        GameObject originalInstance = null;
        GameObject copiedInstance = null;
        try
        {
            Folder("Assets/SceneSaveFocused/Input");
            var sourceObject = new GameObject("SaveSource");
            sourceObject.AddComponent<MeshRenderer>().enabled = true;
            var source = PrefabUtility.SaveAsPrefabAsset(sourceObject, "Assets/SceneSaveFocused/Input/SaveSource.prefab");
            Object.DestroyImmediate(sourceObject);

            originalInstance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            originalInstance.GetComponent<MeshRenderer>().enabled = false;
            PrefabUtility.RecordPrefabInstancePropertyModifications(originalInstance.GetComponent<MeshRenderer>());
            var tool = Tool(originalInstance, "Assets/SceneSaveFocused/Output");
            var create = tool.BuildSceneSavePlan();
            string copyPath = create.overview.ResultPaths[0];
            Require(copyPath != AssetDatabase.GetAssetPath(source), "普通场景实例仍指向原始预制体路径");
            RunPlan(create);

            var copy = AssetDatabase.LoadAssetAtPath<GameObject>(copyPath);
            Require(copy != null && !copy.GetComponent<MeshRenderer>().enabled, "场景加工值没有保存到新副本");
            Require(PrefabUtility.GetPrefabAssetType(copy) == PrefabAssetType.Regular, "新副本不是独立常规预制体");
            Require(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(source)).GetComponent<MeshRenderer>().enabled, "原始预制体被覆盖");

            copiedInstance = (GameObject)PrefabUtility.InstantiatePrefab(copy);
            copiedInstance.GetComponent<MeshRenderer>().enabled = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(copiedInstance.GetComponent<MeshRenderer>());
            var reloadedTool = Tool(copiedInstance, "Assets/SceneSaveFocused/Output");
            var update = reloadedTool.BuildSceneSavePlan();
            Require(update.overview.ResultPaths[0] == copyPath, "丢失会话记录后没有识别已有工具副本");
            RunPlan(update);
            Require(AssetDatabase.LoadAssetAtPath<GameObject>(copyPath).GetComponent<MeshRenderer>().enabled, "已有工具副本没有原位更新");

            Finish("PASS");
        }
        catch (Exception e) { Finish("FAIL\n" + e); }
        finally
        {
            if (originalInstance != null) Object.DestroyImmediate(originalInstance);
            if (copiedInstance != null) Object.DestroyImmediate(copiedInstance);
        }
    }

    static PrefabCopyTool Tool(GameObject instance, string output)
    {
        var tool = new PrefabCopyTool();
        tool.AddSceneInstances(new[] { instance });
        typeof(PrefabCopyTool).GetField("outputFolder", Fields).SetValue(tool, Folder(output));
        return tool;
    }

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

    static void RunPlan(ToolPlan plan)
    {
        Require(plan.errors.Count == 0 && plan.blockingErrors.Count == 0, "保存计划包含问题");
        Require(string.IsNullOrEmpty(plan.validate?.Invoke()), "保存计划生成后立即失效");
        var transaction = new ToolFileTransaction(plan.title);
        try
        {
            foreach (var step in plan.steps) step.Run(transaction);
            transaction.Complete();
            plan.completed?.Invoke();
        }
        catch
        {
            try { plan.rollback?.Invoke(); }
            finally { transaction.Rollback(); }
            throw;
        }
    }

    static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static void Finish(string result)
    {
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "scene-save-results.txt"), result);
        EditorApplication.Exit(result == "PASS" ? 0 : 1);
    }
}
