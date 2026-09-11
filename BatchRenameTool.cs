using System;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.SceneManagement;

namespace ToolSet
{
    public class BatchRenameTool : ToolPanel
    {
        private string newBaseName = "";
        private string replacePart = "";
        private string newPart = "";
        private string insertPart = "";
        private int insertIndex = 0;
        private InsertPosition insertPosition = InsertPosition.开头;
        private RenameTarget renameTarget = RenameTarget.资源文件;
        private bool includeChildren = true;
        private string statusMessage = "等待操作";

        private List<string> cachedAssets;
        private List<GameObject> cachedNodes;
        private bool cachedIncludeChildren;
        private int startNumber = 1, numberDigits = 1;
        public override void Invalidate() { cachedAssets = null; cachedNodes = null; }
        private Options options = Options.基础名称加序号;

        public override void DrawGUI()
        {
            ToolUi.DrawToolHeader("批量重命名工具", "资源文件按路径排序；仅处理文件。场景节点支持撤销，预览列出原名与新名。");

            ToolUi.BeginCard("1) 目标与规则");
            GUILayout.Label("作用对象：", EditorStyles.boldLabel);
            renameTarget = (RenameTarget)EditorGUILayout.EnumPopup(renameTarget);

            if (renameTarget == RenameTarget.Hierarchy节点)
            {
                includeChildren = EditorGUILayout.ToggleLeft("包含子节点", includeChildren);
            }

            EditorGUILayout.Space(5);
            GUILayout.Label("重命名方式：", EditorStyles.boldLabel);
            options = (Options)EditorGUILayout.EnumPopup(options);

            if (options == Options.基础名称加序号)
                {
                newBaseName = EditorGUILayout.TextField("新基础名称:", newBaseName);
                startNumber = EditorGUILayout.IntField("起始序号", startNumber);
                numberDigits = Mathf.Clamp(EditorGUILayout.IntField("序号位数", numberDigits), 1, 8);
                }
            if (options == Options.替换部分字符串)
            {
                replacePart = EditorGUILayout.TextField("替换字符串:", replacePart);
                newPart = EditorGUILayout.TextField("新字符串:", newPart);
            }
            if (options == Options.插入字符串)
            {
                insertPart = EditorGUILayout.TextField("插入字符串:", insertPart);
                insertPosition = (InsertPosition)EditorGUILayout.EnumPopup("插入位置:", insertPosition);
                if (insertPosition == InsertPosition.指定位置)
                {
                    insertIndex = EditorGUILayout.IntField("位置索引:", insertIndex);
                }
            }
            ToolUi.EndCard();

            ToolUi.BeginCard("2) 执行");
            if (GUILayout.Button("预览重命名"))
            {
                ToolWorkflow.Preview(BuildPlan);
            }
            ToolUi.EndCard();

            ToolUi.BeginCard("3) 当前选择");
            DrawCurrentSelection();
            ToolUi.EndCard();
            ToolUi.DrawStatus(statusMessage);
        }



        private void DrawCurrentSelection()
        {
            switch (renameTarget)
            {
                case RenameTarget.资源文件:
                {
                    var assetPaths = GetSelectedAssetPaths();
                    GUILayout.Label($"当前选择文件（{assetPaths.Count}）：");
                    foreach (var assetPath in assetPaths.Take(100))
                    {
                        GUILayout.Label(Path.GetFileName(assetPath));
                    }
                    break;
                }
                case RenameTarget.Hierarchy节点:
                {
                    var nodes = GetSelectedHierarchyNodes(includeChildren);
                    GUILayout.Label($"当前选择Hierarchy节点（{nodes.Count}）：");
                    foreach (var go in nodes.Take(100))
                    {
                        GUILayout.Label(GetHierarchyPath(go.transform));
                    }
                    break;
                }
            }
        }

        private bool ValidateInput()
        {
            if (options == Options.基础名称加序号 && string.IsNullOrWhiteSpace(newBaseName)) throw new InvalidOperationException("请输入基础名称。");
            if (options == Options.替换部分字符串 && string.IsNullOrEmpty(replacePart)) throw new InvalidOperationException("替换字符串不能为空。");
            if (options == Options.插入字符串 && string.IsNullOrEmpty(insertPart)) throw new InvalidOperationException("插入字符串不能为空。");
            return true;
        }





        internal ToolPlan BuildPlan()
        {
            Invalidate(); ValidateInput();
            var plan = new ToolPlan { title = "批量重命名（资源保持 GUID，场景节点支持 Undo）" };
            if (renameTarget == RenameTarget.资源文件)
            {
                var selected = GetSelectedAssetPaths();
                var changes = selected.Select((p, i) => new {
                    oldPath = p, guid = AssetDatabase.AssetPathToGUID(p),
                    name = BuildNewName(Path.GetFileNameWithoutExtension(p), i + 1),
                    temp = AssetDatabase.GenerateUniqueAssetPath(Path.GetDirectoryName(p).Replace('\\', '/') + "/__ToolSet_" + Guid.NewGuid().ToString("N") + Path.GetExtension(p))
                }).Where(c => c.name != Path.GetFileNameWithoutExtension(c.oldPath)).ToArray();
                var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var moving = new HashSet<string>(changes.Select(c => c.oldPath), StringComparer.OrdinalIgnoreCase);
                foreach (var c in changes)
                {
                    string destination = Path.GetDirectoryName(c.oldPath).Replace('\\', '/') + "/" + c.name + Path.GetExtension(c.oldPath);
                    if (!ValidName(c.name)) plan.errors.Add("无效文件名：" + c.name);
                    if (!destinations.Add(destination) || (File.Exists(destination) && !moving.Contains(destination)) || Directory.Exists(destination))
                        plan.errors.Add("目标名称冲突：" + destination);
                    try { ToolPaths.RequireWritableAsset(c.oldPath); } catch (Exception e) { plan.errors.Add(e.Message); }
                    plan.Watch(c.oldPath); plan.Watch(destination); plan.Watch(c.temp);
                    plan.Add("准备重命名 " + c.oldPath, transaction =>
                    {
                        transaction.Capture(c.oldPath); transaction.Capture(destination); transaction.Capture(c.temp);
                        string error = AssetDatabase.RenameAsset(AssetDatabase.GUIDToAssetPath(c.guid), Path.GetFileNameWithoutExtension(c.temp));
                        if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                    });
                }
                foreach (var c in changes)
                    plan.Add(c.oldPath + " → " + c.name + Path.GetExtension(c.oldPath), transaction =>
                    {
                        string error = AssetDatabase.RenameAsset(AssetDatabase.GUIDToAssetPath(c.guid), c.name);
                        if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                    });
            }
            else
            {
                var changes = GetSelectedHierarchyNodes(includeChildren).Select((go, i) => new { go, before = go.name, after = BuildNewName(go.name, i + 1) }).Where(c => c.before != c.after).ToArray();
                foreach (var c in changes)
                {
                    if (!ValidName(c.after)) plan.errors.Add("无效节点名称：" + c.after);
                    plan.Add(GetHierarchyPath(c.go.transform) + " → " + c.after, transaction =>
                    {
                        if (c.go == null || c.go.name != c.before) throw new InvalidOperationException("节点已改变，请重新预览。");
                        Undo.RecordObject(c.go, "重命名节点"); c.go.name = c.after;
                        if (PrefabUtility.IsPartOfPrefabInstance(c.go)) PrefabUtility.RecordPrefabInstancePropertyModifications(c.go);
                        EditorSceneManager.MarkSceneDirty(c.go.scene);
                    });
                }
                plan.rollback = () =>
                {
                    foreach (var c in changes)
                        if (c.go != null && c.go.name == c.after)
                        {
                            c.go.name = c.before;
                            if (PrefabUtility.IsPartOfPrefabInstance(c.go)) PrefabUtility.RecordPrefabInstancePropertyModifications(c.go);
                            EditorSceneManager.MarkSceneDirty(c.go.scene);
                        }
                };
                plan.validate = () => changes.Any(c => c.go == null || c.go.name != c.before) ? "节点已改变，请重新预览。" : null;
            }
            plan.completed = Invalidate;
            return plan;
        }
        private static bool ValidName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.EndsWith(".") || value.EndsWith(" ") || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            string stem = value.Split('.')[0].ToUpperInvariant();
            return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem);
        }

        private string BuildNewName(string oldName, int sequence)
        {
            if (options == Options.基础名称加序号)
            {
                return newBaseName + "_" + (sequence + startNumber - 1).ToString("D" + numberDigits);
            }

            if (options == Options.替换部分字符串)
            {
                return oldName.Replace(replacePart, newPart);
            }

            if (options == Options.插入字符串)
            {
                if (insertPosition == InsertPosition.开头)
                {
                    return insertPart + oldName;
                }

                if (insertPosition == InsertPosition.结尾)
                {
                    return oldName + insertPart;
                }

                int safeIndex = Mathf.Clamp(insertIndex, 0, oldName.Length);
                return oldName.Insert(safeIndex, insertPart);
            }

            return oldName;
        }

        private List<string> GetSelectedAssetPaths()
        {
            if (cachedAssets == null)
                cachedAssets = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => ToolPaths.Contains("Assets", p) && File.Exists(p) && !AssetDatabase.IsValidFolder(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            return cachedAssets;
        }

        private List<GameObject> GetSelectedHierarchyNodes(bool withChildren)
        {
            if (cachedNodes != null && cachedIncludeChildren == withChildren) return cachedNodes;
            cachedIncludeChildren = withChildren;
            var selected = Selection.gameObjects;
            var results = new List<GameObject>();
            var visited = new HashSet<int>();

            foreach (var go in selected)
            {
                if (go == null || EditorUtility.IsPersistent(go) || !go.scene.IsValid())
                {
                    continue;
                }

                if (!withChildren)
                {
                    if (visited.Add(go.GetInstanceID()))
                    {
                        results.Add(go);
                    }

                    continue;
                }

                CollectHierarchyNodes(go.transform, results, visited);
            }

            return cachedNodes = results
                .OrderBy(go => go.scene.name)
                .ThenBy(go => GetHierarchyPath(go.transform))
                .ToList();
        }

        private void CollectHierarchyNodes(Transform root, List<GameObject> results, HashSet<int> visited)
        {
            if (root == null)
            {
                return;
            }

            if (visited.Add(root.gameObject.GetInstanceID()))
            {
                results.Add(root.gameObject);
            }

            foreach (Transform child in root)
            {
                CollectHierarchyNodes(child, results, visited);
            }
        }

        private string GetHierarchyPath(Transform current)
        {
            if (current == null)
            {
                return string.Empty;
            }

            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = $"{current.name}/{path}";
            }

            return path;
        }
    }

    public enum Options
    {
        基础名称加序号,
        替换部分字符串,
        插入字符串,
    }

    public enum InsertPosition
    {
        开头,
        结尾,
        指定位置,
    }

    public enum RenameTarget
    {
        资源文件,
        Hierarchy节点,
    }
}
