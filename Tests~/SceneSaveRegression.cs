using System;
using System.IO;
using ToolSet;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class SceneSaveRegression
{
    public static void Run()
    {
        GameObject first = null;
        GameObject second = null;
        try
        {
            Folder("Assets/SceneSaveFocused/Input");
            var sourceObject = new GameObject("SaveSource");
            sourceObject.AddComponent<MeshRenderer>().enabled = true;
            var source = PrefabUtility.SaveAsPrefabAsset(sourceObject, "Assets/SceneSaveFocused/Input/SaveSource.prefab");
            Object.DestroyImmediate(sourceObject);

            first = (GameObject)PrefabUtility.InstantiatePrefab(source);
            first.GetComponent<MeshRenderer>().enabled = false;
            PrefabUtility.RecordPrefabInstancePropertyModifications(first.GetComponent<MeshRenderer>());
            var tool = new PrefabCopyTool();
            tool.AddSceneInstances(new[] { first });
            var save = tool.BuildSceneSavePlan();
            Require(save.overview.ResultPaths[0] == AssetDatabase.GetAssetPath(source), "保存目标不是实例当前连接的预制体");
            RunPlan(save);
            Require(!AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(source)).GetComponent<MeshRenderer>().enabled, "场景加工值没有保存到当前预制体");

            second = (GameObject)PrefabUtility.InstantiatePrefab(source);
            var duplicateTool = new PrefabCopyTool();
            duplicateTool.AddSceneInstances(new[] { first, second });
            bool blocked = false;
            try { duplicateTool.BuildSceneSavePlan(); }
            catch (InvalidOperationException) { blocked = true; }
            Require(blocked, "连接到同一预制体的多个实例没有阻止冲突保存");

            Finish("PASS");
        }
        catch (Exception e) { Finish("FAIL\n" + e); }
        finally
        {
            if (first != null) Object.DestroyImmediate(first);
            if (second != null) Object.DestroyImmediate(second);
        }
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
