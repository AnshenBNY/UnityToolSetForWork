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
