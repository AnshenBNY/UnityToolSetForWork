using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ToolSet
{
    // Capture both component values and prefab overrides: restoring only values can leave stale overrides.
    internal sealed class SceneCopySnapshot
    {
        readonly GameObject[] roots;
        readonly Dictionary<Object, string> values;
        readonly Dictionary<GameObject, PropertyModification[]> modifications;
        readonly string hierarchy;
        public readonly Component[] components;

        public SceneCopySnapshot(GameObject[] roots)
        {
            this.roots = roots;
            var nodes = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();
            components = nodes.SelectMany(t => t.GetComponents<Component>()).Where(c => c != null && !(c is Transform)).ToArray();
            values = nodes.SelectMany(t => t.GetComponents<Component>().Cast<Object>().Append(t.gameObject))
                .Where(o => o != null).ToDictionary(o => o, EditorJsonUtility.ToJson);
            modifications = nodes.Select(t => t.gameObject).Where(PrefabUtility.IsAnyPrefabInstanceRoot)
                .ToDictionary(o => o, PrefabUtility.GetPropertyModifications);
            hierarchy = HierarchySignature();
        }

        string HierarchySignature() => string.Join("|", roots.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Select(t => t.GetInstanceID() + ":" + (t.parent == null ? 0 : t.parent.GetInstanceID()) + ":" + t.GetSiblingIndex() + ":" +
                string.Join(",", t.GetComponents<Component>().Select(c => c == null ? 0 : c.GetInstanceID()))));

        static bool SameModifications(PropertyModification[] before, PropertyModification[] after)
        {
            before = before ?? Array.Empty<PropertyModification>();
            after = after ?? Array.Empty<PropertyModification>();
            if (before.Length != after.Length) return false;
            for (int i = 0; i < before.Length; i++)
                if (before[i].target != after[i].target || before[i].propertyPath != after[i].propertyPath ||
                    before[i].value != after[i].value || before[i].objectReference != after[i].objectReference) return false;
            return true;
        }

        public bool Changed() => roots.Any(r => r == null) || HierarchySignature() != hierarchy ||
            values.Any(p => p.Key == null || EditorJsonUtility.ToJson(p.Key) != p.Value) ||
            modifications.Any(p => p.Key == null || !SameModifications(p.Value, PrefabUtility.GetPropertyModifications(p.Key)));

        public void Restore()
        {
            foreach (var component in components)
                if (component != null)
                {
                    EditorJsonUtility.FromJsonOverwrite(values[component], component);
                    EditorUtility.SetDirty(component);
                }
            foreach (var pair in modifications)
                if (pair.Key != null) PrefabUtility.SetPropertyModifications(pair.Key, pair.Value ?? Array.Empty<PropertyModification>());
            foreach (var root in roots)
                if (root != null && root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
        }
    }

    public partial class PrefabCopyTool
    {
        // Scene references are deliberately not included in ToolPreferences.
        readonly List<GameObject> sceneInstances = new List<GameObject>();
        int scenePage;
        string sceneNotice;
        bool sceneNoticeWarning;

        static bool IsSceneInstance(Object obj) => obj is GameObject go && !EditorUtility.IsPersistent(go) &&
            go.scene.IsValid() && go.scene.isLoaded && !EditorSceneManager.IsPreviewScene(go.scene) &&
            PrefabUtility.IsAnyPrefabInstanceRoot(go) && !PrefabUtility.IsPrefabAssetMissing(go);

        static GameObject[] TopLevelInstances(IEnumerable<GameObject> objects)
        {
            var all = objects.Distinct().ToArray();
            return all.Where(go => go == null || !all.Any(parent => parent != null && parent != go && go.transform.IsChildOf(parent.transform))).ToArray();
        }

        internal void AddSceneInstances(IEnumerable<Object> objects)
        {
            int rejected = 0, repeated = 0;
            var combined = sceneInstances.ToList();
            foreach (var obj in objects)
            {
                if (!IsSceneInstance(obj)) { rejected++; continue; }
                if (combined.Contains((GameObject)obj)) { repeated++; continue; }
                combined.Add((GameObject)obj);
            }
            var selected = TopLevelInstances(combined);
            int nested = combined.Count - selected.Length;
            bool changed = !sceneInstances.SequenceEqual(selected);
            sceneInstances.Clear(); sceneInstances.AddRange(selected);
            sceneNotice = $"列表共 {sceneInstances.Count} 个实例" + (repeated > 0 ? $"，跳过 {repeated} 个重复项" : "") +
                (nested > 0 ? $"；{nested} 个嵌套实例随已选父实例处理" : "") +
                (rejected > 0 ? $"；{rejected} 项未添加，请选择 Hierarchy 中的预制体根实例。" : "。");
            sceneNoticeWarning = rejected > 0;
            if (changed)
            {
                scenePage = Math.Max(0, (sceneInstances.Count - 1) / 6);
                ToolWorkflow.DiscardPreparedPlan(SceneWorkflowOwner);
            }
        }

        void DrawSceneInstances(string title = "场景实例")
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"{title} · {sceneInstances.Count} 个", EditorStyles.boldLabel);
                var drop = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
                GUI.Box(drop, "将 Hierarchy 中的一个或多个预制体根实例拖到这里", EditorStyles.helpBox);
                var evt = Event.current;
                if (GUI.enabled && drop.Contains(evt.mousePosition) && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
                {
                    DragAndDrop.visualMode = DragAndDrop.objectReferences.Any(IsSceneInstance) ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                    if (evt.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); AddSceneInstances(DragAndDrop.objectReferences); }
                    evt.Use();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("添加 Hierarchy 中选中的实例")) AddSceneInstances(Selection.objects);
                    using (new EditorGUI.DisabledScope(sceneInstances.Count == 0))
                        if (GUILayout.Button("清空列表", GUILayout.Width(80))) { sceneInstances.Clear(); scenePage = 0; sceneNotice = null; }
                }
                if (!string.IsNullOrEmpty(sceneNotice)) EditorGUILayout.HelpBox(sceneNotice, sceneNoticeWarning ? MessageType.Warning : MessageType.Info);
                scenePage = Mathf.Clamp(scenePage, 0, Math.Max(0, (sceneInstances.Count - 1) / 6));
                if (sceneInstances.Count > 6) ToolUi.DrawPager(ref scenePage, sceneInstances.Count, 6);
                int end = Math.Min(sceneInstances.Count, (scenePage + 1) * 6);
                for (int i = scenePage * 6; i < end; i++)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label((i + 1).ToString(), GUILayout.Width(25));
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(sceneInstances[i], typeof(GameObject), true);
                        if (GUILayout.Button("移除", GUILayout.Width(50))) { sceneInstances.RemoveAt(i--); end--; sceneNotice = null; }
                    }
            }
        }

        GameObject[] RequireSceneInstances()
        {
            if (sceneInstances.Count == 0) throw new InvalidOperationException("请先添加至少一个场景中的预制体根实例。");
            for (int i = 0; i < sceneInstances.Count; i++)
                if (!IsSceneInstance(sceneInstances[i])) throw new InvalidOperationException($"第 {i + 1} 个场景实例已失效，请移除后重新添加。仅支持已加载场景中的预制体根实例。");
            return TopLevelInstances(sceneInstances);
        }

        internal ToolPlan BuildSceneCopyPlan(string title, Action<GameObject> execute)
        {
            var instances = RequireSceneInstances();
            string folder = AssetDatabase.GetAssetPath(outputFolder);
            if (!AssetDatabase.IsValidFolder(folder) || !ToolPaths.Contains("Assets", folder)) throw new InvalidOperationException("请选择 Assets 内的有效输出目录。");
            string input = SceneCopyInputSignature();
            var snapshot = new SceneCopySnapshot(instances);
            var plan = new ToolPlan
            {
                title = $"复制{title} · {instances.Length} 个场景实例", owner = SceneWorkflowOwner,
                overview = new CopyOverview { sourceName = $"{instances.Length} 个场景实例", outputFolder = folder, actionLabel = "复制" + title,
                    description = $"处理以下全部实例的{title}，本批次共用的资源只复制一份。修改保留在实例中；完成后可保存本工具生成的副本。" }
            };
            plan.overview.sceneTargets.AddRange(instances);
            plan.validate = () => SceneCopyInputSignature() != input || snapshot.Changed() || instances.Any(i => !IsSceneInstance(i)) ? "实例、层级或复制设置已更改，请重新整理操作内容。" : null;
            int undoGroup = -1;
            plan.rollback = () =>
            {
                try { if (undoGroup >= 0) Undo.RevertAllDownToGroup(undoGroup); }
                finally { snapshot.Restore(); }
            };
            plan.Add("准备整批复制及场景撤销", transaction =>
            {
                copyPaths.Clear(); copiedPaths.Clear();
                Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(plan.title);
                if (snapshot.components.Length > 0) Undo.RegisterCompleteObjectUndo(snapshot.components, plan.title);
                activeTransaction = transaction;
                try { SetupCategorizedOutputPaths(folder); }
                finally { activeTransaction = null; }
            }, "正在准备场景实例复制");
            foreach (var instance in instances)
                plan.Add("复制 " + instance.name + " 的" + title, transaction =>
                {
                    if (!IsSceneInstance(instance)) throw new InvalidOperationException("执行时场景实例已失效：" + instance);
                    activeTransaction = transaction;
                    try
                    {
                        execute(instance);
                        foreach (var component in instance.GetComponentsInChildren<Component>(true))
                            if (component != null && !(component is Transform))
                            {
                                EditorUtility.SetDirty(component);
                                if (PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                            }
                        EditorSceneManager.MarkSceneDirty(instance.scene);
                    }
                    finally { activeTransaction = null; EditorUtility.ClearProgressBar(); }
                }, "正在处理：" + instance.name);
            plan.completed = () => { Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(undoGroup); };
            return plan;
        }

        internal ToolPlan BuildSceneSavePlan()
        {
            var instances = RequireSceneInstances();
            var targets = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in instances)
            {
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
                if (!rootCopyPaths.Contains(path)) throw new InvalidOperationException("只能保存本工具在当前窗口会话中生成的根副本：" + instance.name);
                if (targets.ContainsKey(path)) throw new InvalidOperationException("多个实例对应同一个副本文件，请只保留其中一个再保存，避免不同修改相互覆盖：" + path);
                ToolPaths.RequireWritableAsset(path);
                targets.Add(path, instance);
            }
            string input = SceneCopyInputSignature();
            var snapshot = new SceneCopySnapshot(instances);
            var plan = new ToolPlan
            {
                title = $"保存 {instances.Length} 个预制体副本", owner = SceneWorkflowOwner,
                overview = new CopyOverview { sourceName = $"{instances.Length} 个场景实例", actionLabel = instances.Length > 1 ? "保存所选副本" : "保存副本",
                    description = "将以下实例的修改保存到各自副本。嵌套对象的修改保存在根副本中。" }
            };
            plan.overview.sceneTargets.AddRange(instances);
            plan.validate = () => SceneCopyInputSignature() != input || snapshot.Changed() || targets.Any(p => !IsSceneInstance(p.Value) || PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(p.Value) != p.Key) ? "保存对象或实例内容已更改，请重新整理保存清单。" : null;
            plan.rollback = snapshot.Restore;
            foreach (var target in targets)
            {
                plan.overview.roots.Add(new CopyResource { source = target.Key, destination = target.Key, category = "预制体" });
                plan.Watch(target.Key);
                plan.Add("保存 " + target.Value.name + " → " + target.Key, transaction =>
                {
                    ToolPaths.RequireWritableAsset(target.Key);
                    transaction.Capture(target.Key);
                    PrefabUtility.SaveAsPrefabAsset(target.Value, target.Key, out bool success);
                    if (!success) throw new IOException("保存预制体副本失败：" + target.Key);
                }, "正在保存：" + target.Value.name);
            }
            return plan;
        }
    }
}
