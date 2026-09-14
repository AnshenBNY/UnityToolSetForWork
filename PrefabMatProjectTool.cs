using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ToolSet
{
    public class PrefabMatProjectTool : ToolPanel
    {
        GameObject source, target;
        bool fxToggle = true, matToggle;
        public override void DrawGUI()
        {
            ToolUi.DrawToolHeader("预制体与材质映射", "按相对节点路径映射，源对象保持原样。目标必须是场景对象，修改支持 Undo。");
            source = (GameObject)EditorGUILayout.ObjectField("源根节点", source, typeof(GameObject), true);
            target = (GameObject)EditorGUILayout.ObjectField("目标根节点", target, typeof(GameObject), true);
            fxToggle = EditorGUILayout.ToggleLeft("复制嵌套特效预制体", fxToggle);
            matToggle = EditorGUILayout.ToggleLeft("映射所有 Renderer 的材质（包含根节点）", matToggle);
            EditorGUILayout.HelpBox("目标已有同名特效节点时跳过；重名路径、缺失节点和无法保留的结构覆盖会在预览中列出。", MessageType.Info);
            if (GUILayout.Button("预览映射", GUILayout.Height(30))) ToolWorkflow.Preview(BuildPlan);
        }
        static string Relative(Transform root, Transform node)
        {
            if (node == root) return "";
            var parts = new List<string>();
            while (node != null && node != root) { parts.Add(node.name); node = node.parent; }
            if (node == null) throw new InvalidOperationException("节点不在源根节点下。");
            parts.Reverse(); return string.Join("/", parts);
        }
        internal static Transform FindUnique(Transform root, string path)
        {
            foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var found = root.Cast<Transform>().Where(t => t.name == part).ToArray();
                if (found.Length != 1) throw new InvalidOperationException("路径缺失或存在重名节点：" + path);
                root = found[0];
            }
            return root;
        }
        internal ToolPlan BuildPlan()
        {
            if (source == null || target == null || source == target || EditorUtility.IsPersistent(target) || !target.scene.IsValid())
                throw new InvalidOperationException("请选择不同的源对象和有效的目标场景对象。");
            if (target.transform.IsChildOf(source.transform) || source.transform.IsChildOf(target.transform))
                throw new InvalidOperationException("源和目标不能互为父子节点。");
            var plan = new ToolPlan { title = "预制体与材质映射" };
            if (!fxToggle && !matToggle) plan.errors.Add("至少启用一种映射。");
            var created = new List<GameObject>();
            var materialsBefore = new Dictionary<Renderer, Material[]>();
            var sourceRoot = source; var targetRoot = target;
            plan.validate = () => sourceRoot == null || targetRoot == null ? "源或目标已丢失。" : null;
            plan.rollback = () =>
            {
                foreach (var go in created.Where(go => go != null)) UnityEngine.Object.DestroyImmediate(go);
                foreach (var pair in materialsBefore) if (pair.Key != null) { pair.Key.sharedMaterials = pair.Value; Mark(pair.Key); }
                if (targetRoot != null) EditorSceneManager.MarkSceneDirty(targetRoot.scene);
            };
            if (fxToggle)
            {
                var prefabs = source.GetComponentsInChildren<Transform>(true).Where(t => t != source.transform && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)).ToArray();
                foreach (var node in prefabs)
                {
                    if (prefabs.Any(parent => parent != node && node.IsChildOf(parent))) continue;
                    try
                    {
                        string parentPath = Relative(source.transform, node.parent);
                        var parent = FindUnique(target.transform, parentPath);
                        var existing = parent.Cast<Transform>().Where(t => t.name == node.name).ToArray();
                        if (existing.Length == 1) continue;
                        if (existing.Length > 1) throw new InvalidOperationException("目标同名节点不唯一：" + parentPath + "/" + node.name);
                        string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(node.gameObject);
                        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                        if (asset == null) throw new InvalidOperationException("嵌套预制体来源不可用：" + node.name);
                        if (PrefabUtility.GetAddedGameObjects(node.gameObject).Count > 0 || PrefabUtility.GetAddedComponents(node.gameObject).Count > 0 || PrefabUtility.GetRemovedComponents(node.gameObject).Count > 0 || PrefabUtility.GetRemovedGameObjects(node.gameObject).Count > 0)
                            throw new InvalidOperationException(node.name + " 包含添加/删除节点或组件的覆盖，请先保存为独立预制体再映射。");
                        var modifications = PrefabUtility.GetPropertyModifications(node.gameObject);
                        string before = EditorJsonUtility.ToJson(node);
                        plan.Watch(AssetDatabase.GetAssetPath(asset));
                        plan.Add("新增 " + parentPath + "/" + node.name + "（保留属性覆盖和局部变换）", transaction =>
                        {
                            if (node == null || parent == null || EditorJsonUtility.ToJson(node) != before || parent.Cast<Transform>().Any(t => t.name == node.name))
                                throw new InvalidOperationException("节点已变化，请重新预览。");
                            var clone = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
                            created.Add(clone); Undo.RegisterCreatedObjectUndo(clone, "映射特效预制体");
                            if (modifications != null) PrefabUtility.SetPropertyModifications(clone, modifications);
                            clone.name = node.name; clone.SetActive(node.gameObject.activeSelf);
                            clone.transform.localPosition = node.localPosition;
                            clone.transform.localRotation = node.localRotation;
                            clone.transform.localScale = node.localScale;
                            PrefabUtility.RecordPrefabInstancePropertyModifications(clone);
                            PrefabUtility.RecordPrefabInstancePropertyModifications(clone.transform);
                            EditorSceneManager.MarkSceneDirty(clone.scene);
                        });
                    }
                    catch (Exception e) { plan.errors.Add(e.Message); }
                }
            }
            if (matToggle)
                foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
                {
                    try
                    {
                        string path = Relative(source.transform, renderer.transform);
                        var destination = FindUnique(target.transform, path);
                        var peers = renderer.GetComponents(renderer.GetType());
                        int index = Array.IndexOf(peers, renderer);
                        var candidates = destination.GetComponents(renderer.GetType());
                        if (index >= candidates.Length) throw new InvalidOperationException("目标 Renderer 类型或数量不匹配：" + path);
                        var targetRenderer = (Renderer)candidates[index];
                        var before = targetRenderer.sharedMaterials; var after = renderer.sharedMaterials;
                        if (before.SequenceEqual(after)) continue;
                        plan.Add("材质 " + (path.Length == 0 ? "<根节点>" : path) + " · " + renderer.GetType().Name + "[" + index + "]", transaction =>
                        {
                            if (targetRenderer == null || !targetRenderer.sharedMaterials.SequenceEqual(before)) throw new InvalidOperationException("目标材质已变化，请重新预览。");
                            materialsBefore[targetRenderer] = before;
                            Undo.RecordObject(targetRenderer, "映射材质"); targetRenderer.sharedMaterials = after; Mark(targetRenderer);
                        });
                    }
                    catch (Exception e) { plan.errors.Add(e.Message); }
                }
            return plan;
        }
        static void Mark(Renderer renderer)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(renderer)) PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
        }
    }
}
