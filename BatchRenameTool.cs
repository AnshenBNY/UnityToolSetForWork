using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.SceneManagement;

namespace ToolSet
{
    public class BatchRenameTool : EditorWindow, IToolGUI
    {
        private string newBaseName = "";
        private string replacePart = "";
        private string newPart = "";
        private string insertPart = "";
        private int insertIndex = 0;
        private InsertPosition insertPosition = InsertPosition.开头;
        private RenameTarget renameTarget = RenameTarget.资源文件;
        private bool includeChildren = true;

        private Options options = Options.基础名称加序号;

        public void DrawGUI()
        {
            EditorGUILayout.Space(10);
            GUILayout.Label("批量重命名工具", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

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
                newBaseName = EditorGUILayout.TextField("新基础名称:", newBaseName);
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

            if (GUILayout.Button("重命名"))
            {
                PerformRename();
            }

            DrawCurrentSelection();
        }

        private void PerformRename()
        {
            if (!ValidateInput())
            {
                return;
            }

            switch (renameTarget)
            {
                case RenameTarget.资源文件:
                    RenameAssets();
                    break;
                case RenameTarget.Hierarchy节点:
                    RenameHierarchyNodes();
                    break;
            }
        }

        private void DrawCurrentSelection()
        {
            switch (renameTarget)
            {
                case RenameTarget.资源文件:
                {
                    var assetPaths = GetSelectedAssetPaths();
                    GUILayout.Label($"当前选择文件（{assetPaths.Count}）：");
                    foreach (var assetPath in assetPaths)
                    {
                        GUILayout.Label(Path.GetFileName(assetPath));
                    }
                    break;
                }
                case RenameTarget.Hierarchy节点:
                {
                    var nodes = GetSelectedHierarchyNodes(includeChildren);
                    GUILayout.Label($"当前选择Hierarchy节点（{nodes.Count}）：");
                    foreach (var go in nodes)
                    {
                        GUILayout.Label(GetHierarchyPath(go.transform));
                    }
                    break;
                }
            }
        }

        private bool ValidateInput()
        {
            if (options == Options.基础名称加序号 && string.IsNullOrEmpty(newBaseName))
            {
                Debug.LogWarning("请输入新基础名称。");
                return false;
            }

            if (options == Options.替换部分字符串 && string.IsNullOrEmpty(replacePart))
            {
                Debug.LogWarning("替换字符串不能为空。");
                return false;
            }
            if (options == Options.插入字符串 && string.IsNullOrEmpty(insertPart))
            {
                Debug.LogWarning("插入字符串不能为空。");
                return false;
            }

            switch (renameTarget)
            {
                case RenameTarget.资源文件:
                    if (GetSelectedAssetPaths().Count == 0)
                    {
                        Debug.LogWarning("请至少选择一个资源文件。");
                        return false;
                    }
                    break;
                case RenameTarget.Hierarchy节点:
                    if (GetSelectedHierarchyNodes(includeChildren).Count == 0)
                    {
                        Debug.LogWarning("请至少选择一个Hierarchy节点。");
                        return false;
                    }
                    break;
            }

            return true;
        }

        private void RenameAssets()
        {
            var selectedPaths = GetSelectedAssetPaths();
            int sequence = 1;
            int renamedCount = 0;

            foreach (var path in selectedPaths)
            {
                string oldName = Path.GetFileNameWithoutExtension(path);
                string newName = BuildNewName(oldName, sequence);
                if (options == Options.基础名称加序号)
                {
                    sequence++;
                }

                if (oldName == newName)
                {
                    continue;
                }

                string error = AssetDatabase.RenameAsset(path, newName);
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning($"重命名失败：{path}，原因：{error}");
                    continue;
                }

                renamedCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"资源重命名完成，共成功 {renamedCount} 个。");
        }

        private void RenameHierarchyNodes()
        {
            var nodes = GetSelectedHierarchyNodes(includeChildren);
            var unityObjects = nodes.Select(n => (UnityEngine.Object)n).ToArray();
            Undo.RecordObjects(unityObjects, "Batch Rename Hierarchy Nodes");

            int sequence = 1;
            int renamedCount = 0;
            foreach (var node in nodes)
            {
                string newName = BuildNewName(node.name, sequence);
                if (options == Options.基础名称加序号)
                {
                    sequence++;
                }

                if (newName == node.name)
                {
                    continue;
                }

                node.name = newName;
                EditorUtility.SetDirty(node);
                renamedCount++;
            }

            if (renamedCount > 0)
            {
                EditorSceneManager.MarkAllScenesDirty();
            }

            Debug.Log($"Hierarchy节点重命名完成，共成功 {renamedCount} 个。");
        }

        private string BuildNewName(string oldName, int sequence)
        {
            if (options == Options.基础名称加序号)
            {
                return $"{newBaseName}_{sequence}";
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
            var paths = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath))
                    paths.Add(assetPath);
            }

            return paths;
        }

        private List<GameObject> GetSelectedHierarchyNodes(bool withChildren)
        {
            var selected = Selection.gameObjects;
            var results = new List<GameObject>();
            var visited = new HashSet<int>();

            foreach (var go in selected)
            {
                if (go == null)
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

            return results
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
