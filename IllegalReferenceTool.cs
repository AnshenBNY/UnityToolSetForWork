using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    public class IllegalReferenceTool : IToolGUI
    {
        private const string ZeroGuid = "00000000000000000000000000000000";

        private readonly List<UnityEngine.Object> queryTargets = new();
        private readonly List<DefaultAsset> excludeFolders = new();
        private readonly List<IllegalReferenceItem> results = new();

        private string excludeExtensionsInput = ".cs,.js,.dll,.asmdef,.asmref";
        private bool excludeEditorScripts = true;
        private DefaultAsset replaceCopyTargetFolder;
        private string statusMessage = "等待查询";

        private Vector2 queryTargetScroll;
        private Vector2 excludeFolderScroll;
        private Vector2 resultScroll;

        private bool targetFoldout = true;
        private bool ruleFoldout = true;
        private bool resultFoldout = true;

        private GUIStyle sectionTitleStyle;
        private GUIStyle sectionHintStyle;

        private sealed class ScanEntry
        {
            public string assetPath;
            public readonly HashSet<string> ownerDirectoryRoots = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class IllegalReferenceItem
        {
            public string illegalPath;
            public string illegalGuid;
            public string fileName;
            public readonly List<string> referencedByPaths = new();
            public readonly HashSet<string> ownerDirectoryRoots = new(StringComparer.OrdinalIgnoreCase);
            public List<string> candidatePaths = new();
            public bool foldout = true;
        }

        private static readonly HashSet<string> GuidReplaceSupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".mat", ".asset", ".controller", ".overrideController", ".anim", ".playable", ".guiskin"
        };

        public void DrawGUI()
        {
            EnsureUiStyles();

            ToolUi.DrawToolHeader("非法引用查询工具",
                "用途：在指定查询对象中扫描“命中排除规则”的依赖引用，支持定位、候选替换、删除引用（GUID置空）、复制并替换。");

            ToolUi.DrawFoldoutCard("1) 查询对象", ref targetFoldout, DrawTargetSection);
            ToolUi.DrawFoldoutCard("2) 非法规则（排除目录/类型）", ref ruleFoldout, DrawRuleSection);
            ToolUi.DrawFoldoutCard("3) 查询结果与处理", ref resultFoldout, DrawResultSection);

            EditorGUILayout.Space(8);
            ToolUi.DrawStatus(statusMessage);
        }

        private void DrawTargetSection()
        {
            EditorGUILayout.LabelField("支持拖入多个目录或文件进行查询", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("添加当前Selection", GUILayout.Width(140)))
                {
                    AddSelectionTargets();
                }

                if (GUILayout.Button("清空查询对象", GUILayout.Width(120)))
                {
                    queryTargets.Clear();
                    results.Clear();
                    statusMessage = "已清空查询对象";
                }
            }

            DrawTargetDropArea();
            EditorGUILayout.Space(4);

            queryTargetScroll = EditorGUILayout.BeginScrollView(queryTargetScroll, GUILayout.Height(120));
            for (int i = 0; i < queryTargets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    queryTargets[i] = EditorGUILayout.ObjectField(queryTargets[i], typeof(UnityEngine.Object), false);
                    if (GUILayout.Button("移除", GUILayout.Width(56)))
                    {
                        queryTargets.RemoveAt(i);
                        i--;
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawRuleSection()
        {
            excludeEditorScripts = EditorGUILayout.ToggleLeft("排除 Editor 目录资源", excludeEditorScripts);
            excludeExtensionsInput = EditorGUILayout.TextField("排除扩展名（逗号分隔）", excludeExtensionsInput);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("排除目录", EditorStyles.boldLabel);
                if (GUILayout.Button("添加排除目录", GUILayout.Width(100)))
                {
                    excludeFolders.Add(null);
                }
            }

            excludeFolderScroll = EditorGUILayout.BeginScrollView(excludeFolderScroll, GUILayout.Height(86));
            for (int i = 0; i < excludeFolders.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    excludeFolders[i] = EditorGUILayout.ObjectField(excludeFolders[i], typeof(DefaultAsset), false) as DefaultAsset;
                    if (GUILayout.Button("移除", GUILayout.Width(56)))
                    {
                        excludeFolders.RemoveAt(i);
                        i--;
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawResultSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("开始查询", GUILayout.Height(28)))
                {
                    RunQuery();
                }

                replaceCopyTargetFolder =
                    EditorGUILayout.ObjectField("复制替换目标目录", replaceCopyTargetFolder, typeof(DefaultAsset), false) as DefaultAsset;
            }

            EditorGUILayout.Space(6);
            if (results.Count == 0)
            {
                EditorGUILayout.HelpBox("暂无结果。点击“开始查询”后会按卡片显示非法引用项。", MessageType.Info);
                return;
            }

            resultScroll = EditorGUILayout.BeginScrollView(resultScroll, GUILayout.Height(360));
            for (int i = 0; i < results.Count; i++)
            {
                DrawResultItem(results[i], i + 1);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawResultItem(IllegalReferenceItem item, int index)
        {
            EditorGUILayout.BeginVertical("box");
            item.foldout = EditorGUILayout.Foldout(
                item.foldout,
                $"[{index}] {item.fileName}  |  被引用对象数：{item.referencedByPaths.Count}",
                true);
            if (!item.foldout)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField("非法资源", sectionTitleStyle);
            DrawPathWithPing(item.illegalPath, "定位非法资源");
            EditorGUILayout.LabelField($"GUID: {item.illegalGuid}", EditorStyles.miniLabel);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("被引用对象", sectionTitleStyle);
            if (item.referencedByPaths.Count == 0)
            {
                EditorGUILayout.LabelField("-", EditorStyles.miniLabel);
            }
            else
            {
                foreach (string refPath in item.referencedByPaths)
                {
                    DrawPathWithPing(refPath, "定位引用对象");
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("处理操作", sectionTitleStyle);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("查找候选替换", GUILayout.Width(120)))
                {
                    item.candidatePaths = SearchCandidatePaths(item, 30);
                    statusMessage = item.candidatePaths.Count == 0
                        ? $"未找到候选：{item.fileName}"
                        : $"找到候选 {item.candidatePaths.Count} 个：{item.fileName}";
                }

                if (GUILayout.Button("删除引用(GUID置空)", GUILayout.Width(140)))
                {
                    ReplaceGuidForItem(item, ZeroGuid, "删除引用");
                    return;
                }

                if (GUILayout.Button("复制到目标目录并替换", GUILayout.Width(160)))
                {
                    CopyAndReplace(item);
                    return;
                }
            }

            if (item.candidatePaths.Count > 0)
            {
                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("候选替换资源", EditorStyles.miniBoldLabel);
                foreach (string candidatePath in item.candidatePaths)
                {
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(candidatePath, EditorStyles.linkLabel))
                    {
                        PingAssetByPath(candidatePath);
                    }

                    if (GUILayout.Button("替换为此资源", GUILayout.Width(96)))
                    {
                        string candidateGuid = AssetDatabase.AssetPathToGUID(candidatePath);
                        ReplaceGuidForItem(item, candidateGuid, "候选替换");
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndVertical();
                        return;
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(3);
        }

        private void AddSelectionTargets()
        {
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                AddTargetObject(obj);
            }
        }

        private void AddTargetObject(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!queryTargets.Contains(obj))
            {
                queryTargets.Add(obj);
            }
        }

        private void DrawTargetDropArea()
        {
            Rect dropRect = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "拖拽目录或文件到这里（可多选）", sectionHintStyle);

            Event evt = Event.current;
            if (!dropRect.Contains(evt.mousePosition))
            {
                return;
            }

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (UnityEngine.Object dragged in DragAndDrop.objectReferences)
                    {
                        AddTargetObject(dragged);
                    }
                }

                evt.Use();
            }
        }

        private void RunQuery()
        {
            Dictionary<string, ScanEntry> scanEntries = CollectScanEntries();
            if (scanEntries.Count == 0)
            {
                results.Clear();
                statusMessage = "未找到可查询对象，请先拖入目录或文件";
                return;
            }

            HashSet<string> excludedExtensions = ParseExcludeExtensions();
            List<string> excludedFolderPaths = GetExcludeFolderPaths();
            Dictionary<string, IllegalReferenceItem> itemMap = new(StringComparer.OrdinalIgnoreCase);

            foreach (ScanEntry entry in scanEntries.Values)
            {
                string[] deps = AssetDatabase.GetDependencies(entry.assetPath, false);
                foreach (string depPath in deps)
                {
                    if (string.Equals(depPath, entry.assetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string depGuid = AssetDatabase.AssetPathToGUID(depPath);
                    if (IsBuiltInDependency(depPath, depGuid))
                    {
                        continue;
                    }

                    if (IsPathInAnyFolder(depPath, entry.ownerDirectoryRoots))
                    {
                        continue;
                    }

                    if (!IsIllegalDependency(depPath, excludedExtensions, excludedFolderPaths))
                    {
                        continue;
                    }

                    string key = string.IsNullOrWhiteSpace(depGuid) ? depPath : depGuid;
                    if (!itemMap.TryGetValue(key, out IllegalReferenceItem item))
                    {
                        item = new IllegalReferenceItem
                        {
                            illegalPath = depPath,
                            illegalGuid = depGuid,
                            fileName = Path.GetFileName(depPath)
                        };
                        itemMap[key] = item;
                    }

                    if (!item.referencedByPaths.Contains(entry.assetPath))
                    {
                        item.referencedByPaths.Add(entry.assetPath);
                    }

                    foreach (string ownerDir in entry.ownerDirectoryRoots)
                    {
                        item.ownerDirectoryRoots.Add(ownerDir);
                    }
                }
            }

            results.Clear();
            results.AddRange(itemMap.Values
                .OrderByDescending(v => v.referencedByPaths.Count)
                .ThenBy(v => v.fileName, StringComparer.OrdinalIgnoreCase));

            statusMessage = $"查询完成：发现 {results.Count} 项非法引用";
        }

        private Dictionary<string, ScanEntry> CollectScanEntries()
        {
            Dictionary<string, ScanEntry> map = new(StringComparer.OrdinalIgnoreCase);
            foreach (UnityEngine.Object target in queryTargets)
            {
                if (target == null)
                {
                    continue;
                }

                string targetPath = AssetDatabase.GetAssetPath(target);
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    continue;
                }

                if (AssetDatabase.IsValidFolder(targetPath))
                {
                    string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { targetPath });
                    foreach (string guid in guids)
                    {
                        string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrWhiteSpace(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                        {
                            continue;
                        }

                        ScanEntry entry = GetOrCreateScanEntry(map, assetPath);
                        entry.ownerDirectoryRoots.Add(targetPath);
                    }
                }
                else
                {
                    GetOrCreateScanEntry(map, targetPath);
                }
            }

            return map;
        }

        private static ScanEntry GetOrCreateScanEntry(Dictionary<string, ScanEntry> map, string assetPath)
        {
            if (map.TryGetValue(assetPath, out ScanEntry entry))
            {
                return entry;
            }

            entry = new ScanEntry { assetPath = assetPath };
            map[assetPath] = entry;
            return entry;
        }

        private bool IsIllegalDependency(string depPath, HashSet<string> excludedExtensions, List<string> excludedFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(depPath))
            {
                return false;
            }

            if (excludeEditorScripts && depPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string extension = Path.GetExtension(depPath);
            if (!string.IsNullOrWhiteSpace(extension) && excludedExtensions.Contains(extension))
            {
                return true;
            }

            return IsPathInAnyFolder(depPath, excludedFolderPaths);
        }

        private static bool IsBuiltInDependency(string depPath, string guid)
        {
            if (!string.IsNullOrWhiteSpace(depPath) && depPath.StartsWith("Resources/unity_builtin_extra", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(depPath) && string.IsNullOrWhiteSpace(guid);
        }

        private HashSet<string> ParseExcludeExtensions()
        {
            HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(excludeExtensionsInput))
            {
                return result;
            }

            string[] parts = excludeExtensionsInput.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string ext = part.Trim();
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }

                if (!ext.StartsWith(".", StringComparison.Ordinal))
                {
                    ext = "." + ext;
                }

                result.Add(ext);
            }

            return result;
        }

        private List<string> GetExcludeFolderPaths()
        {
            List<string> paths = new();
            foreach (DefaultAsset folder in excludeFolders)
            {
                if (folder == null)
                {
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(folder);
                if (!string.IsNullOrWhiteSpace(path) && AssetDatabase.IsValidFolder(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private List<string> SearchCandidatePaths(IllegalReferenceItem item, int maxCount)
        {
            HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in GetExcludeFolderPaths())
            {
                roots.Add(folder);
            }

            foreach (string ownerRoot in item.ownerDirectoryRoots)
            {
                if (!string.IsNullOrWhiteSpace(ownerRoot) && AssetDatabase.IsValidFolder(ownerRoot))
                {
                    roots.Add(ownerRoot);
                }
            }

            if (roots.Count == 0)
            {
                return new List<string>();
            }

            string fileName = Path.GetFileName(item.illegalPath);
            string fileNameNoExt = Path.GetFileNameWithoutExtension(item.illegalPath);
            string extension = Path.GetExtension(item.illegalPath);
            HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

            foreach (string root in roots)
            {
                string[] guids = AssetDatabase.FindAssets(fileNameNoExt, new[] { root });
                foreach (string guid in guids)
                {
                    string candidatePath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrWhiteSpace(candidatePath) || AssetDatabase.IsValidFolder(candidatePath))
                    {
                        continue;
                    }

                    if (!string.Equals(Path.GetFileName(candidatePath), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(Path.GetExtension(candidatePath), extension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(candidatePath, item.illegalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    candidates.Add(candidatePath);
                    if (candidates.Count >= maxCount)
                    {
                        break;
                    }
                }

                if (candidates.Count >= maxCount)
                {
                    break;
                }
            }

            return candidates.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void ReplaceGuidForItem(IllegalReferenceItem item, string newGuid, string actionName)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.illegalGuid))
            {
                statusMessage = "当前非法引用GUID无效，无法执行替换";
                return;
            }

            if (string.IsNullOrWhiteSpace(newGuid))
            {
                statusMessage = "新GUID无效";
                return;
            }

            int changedCount = 0;
            int scannedCount = 0;
            List<string> changedAssets = new();

            foreach (string refPath in item.referencedByPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!IsSupportedGuidReplaceFile(refPath))
                {
                    continue;
                }

                string absolutePath = ToAbsolutePath(refPath);
                if (!File.Exists(absolutePath))
                {
                    continue;
                }

                scannedCount++;
                string content;
                try
                {
                    content = File.ReadAllText(absolutePath, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                if (!content.Contains(item.illegalGuid))
                {
                    continue;
                }

                string replaced = content.Replace(item.illegalGuid, newGuid);
                if (ReferenceEquals(content, replaced) || content == replaced)
                {
                    continue;
                }

                File.WriteAllText(absolutePath, replaced, new UTF8Encoding(false));
                changedAssets.Add(refPath);
                changedCount++;
            }

            if (changedAssets.Count > 0)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (string changedPath in changedAssets)
                    {
                        AssetDatabase.ImportAsset(changedPath, ImportAssetOptions.ForceUpdate);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            statusMessage = $"{actionName}完成：扫描 {scannedCount}，修改 {changedCount}";
            RunQuery();
        }

        private void CopyAndReplace(IllegalReferenceItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.illegalPath) || !File.Exists(ToAbsolutePath(item.illegalPath)))
            {
                statusMessage = "非法资源不存在，无法复制";
                return;
            }

            string targetFolderPath = replaceCopyTargetFolder == null ? string.Empty : AssetDatabase.GetAssetPath(replaceCopyTargetFolder);
            if (string.IsNullOrWhiteSpace(targetFolderPath) || !AssetDatabase.IsValidFolder(targetFolderPath))
            {
                statusMessage = "请先指定有效的复制替换目标目录";
                return;
            }

            string desiredPath = $"{targetFolderPath}/{Path.GetFileName(item.illegalPath)}";
            string newAssetPath = AssetDatabase.GenerateUniqueAssetPath(desiredPath);
            bool copied = AssetDatabase.CopyAsset(item.illegalPath, newAssetPath);
            if (!copied)
            {
                statusMessage = $"复制失败：{item.illegalPath}";
                return;
            }

            AssetDatabase.ImportAsset(newAssetPath, ImportAssetOptions.ForceUpdate);
            string newGuid = AssetDatabase.AssetPathToGUID(newAssetPath);
            ReplaceGuidForItem(item, newGuid, "复制并替换引用");
        }

        private static bool IsSupportedGuidReplaceFile(string assetPath)
        {
            string ext = Path.GetExtension(assetPath);
            return !string.IsNullOrWhiteSpace(ext) && GuidReplaceSupportedExtensions.Contains(ext);
        }

        private static bool IsPathInAnyFolder(string assetPath, IEnumerable<string> folderPaths)
        {
            foreach (string folderPath in folderPaths)
            {
                if (IsPathInFolder(assetPath, folderPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPathInFolder(string assetPath, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            if (string.Equals(assetPath, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return assetPath.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static void PingAssetByPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                return;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            string normalizedAssetPath = assetPath.Replace('\\', '/');
            string combined = Path.Combine(root, normalizedAssetPath);
            return Path.GetFullPath(combined);
        }

        private void DrawPathWithPing(string path, string buttonText)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(path, EditorStyles.linkLabel))
                {
                    PingAssetByPath(path);
                }

                if (GUILayout.Button(buttonText, GUILayout.Width(96)))
                {
                    PingAssetByPath(path);
                }
            }
        }

        private void EnsureUiStyles()
        {
            if (sectionTitleStyle == null)
            {
                sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12
                };
            }

            if (sectionHintStyle == null)
            {
                sectionHintStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 11
                };
            }
        }
    }
}
