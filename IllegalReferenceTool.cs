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
        private const int CandidateLimit = 30;
        public event Action Changed;

        private readonly List<UnityEngine.Object> queryTargets = new();
        private readonly List<DefaultAsset> excludeFolders = new();
        private readonly List<IllegalReferenceItem> results = new();

        private string excludeExtensionsInput = ".cs,.js,.dll,.asmdef,.asmref,.shader";
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
            public bool searchingCandidates;
            public string candidateStatus;
            public MessageType candidateStatusType = MessageType.Info;
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
                "用途：在指定查询对象中扫描“越界依赖引用”，可通过排除规则忽略目录/类型，支持定位、候选替换、清空对象引用、复制并替换。");

            ToolUi.DrawFoldoutCard("1) 查询对象", ref targetFoldout, DrawTargetSection);
            ToolUi.DrawFoldoutCard("2) 忽略规则（排除目录/类型）", ref ruleFoldout, DrawRuleSection);
            ToolUi.DrawFoldoutCard("3) 查询结果与处理", ref resultFoldout, DrawResultSection);

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

        private string resultFilter = "";
        private int resultPage;

        private void DrawResultSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("开始查询", GUILayout.Height(28)))
                {
                    EditorApplication.delayCall += RunQuery;
                }

                replaceCopyTargetFolder =
                    EditorGUILayout.ObjectField("复制替换目标目录", replaceCopyTargetFolder, typeof(DefaultAsset), false) as DefaultAsset;
            }

            ToolUi.DrawStatus(statusMessage);
            EditorGUILayout.Space(6);
            if (results.Count == 0)
            {
                EditorGUILayout.HelpBox("暂无结果。点击“开始查询”后会按卡片显示非法引用项。", MessageType.Info);
                return;
            }

            resultFilter = EditorGUILayout.TextField("筛选名称或路径", resultFilter);
            var visible = results.Where(r => r.illegalPath.IndexOf(resultFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            ToolUi.DrawPager(ref resultPage, visible.Count, 30);
            resultScroll = EditorGUILayout.BeginScrollView(resultScroll, GUILayout.Height(360));
            for (int i = resultPage * 30; i < Math.Min(visible.Count, (resultPage + 1) * 30); i++)
            {
                DrawResultItem(visible[i], i + 1);
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
            if (!string.IsNullOrEmpty(item.candidateStatus))
                EditorGUILayout.HelpBox(item.candidateStatus, item.candidateStatusType);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(item.searchingCandidates))
                    if (GUILayout.Button(item.searchingCandidates ? "正在查找…" : "查找候选替换", GUILayout.Width(120)))
                        FindCandidates(item);

                if (GUILayout.Button("预览清空引用", GUILayout.Width(140)))
                {
                    ReplaceGuidForItem(item, ZeroGuid, "删除引用");
                }

                if (GUILayout.Button("复制到目标目录并替换", GUILayout.Width(160)))
                {
                    CopyAndReplace(item);
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
            try { ToolAssetCache.Clear(); RunQueryCore(); }
            catch (OperationCanceledException) { statusMessage = "查询已取消，保留上次结果"; }
            catch (Exception e) { statusMessage = "查询失败：" + e.Message; Debug.LogException(e); }
            finally { EditorUtility.ClearProgressBar(); Changed?.Invoke(); }
        }
        private void RunQueryCore()
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

            int scanned = 0;
            foreach (ScanEntry entry in scanEntries.Values)
            {
                if (EditorUtility.DisplayCancelableProgressBar("检查外部引用", entry.assetPath, (float)scanned++ / scanEntries.Count))
                    throw new OperationCanceledException();
                if (ShouldExcludeDependency(entry.assetPath, excludedExtensions, excludedFolderPaths)) continue;
                string[] deps = ToolAssetCache.Dependencies(entry.assetPath, false);
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

                    if (ShouldExcludeDependency(depPath, excludedExtensions, excludedFolderPaths))
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
                    GetOrCreateScanEntry(map, targetPath).ownerDirectoryRoots.Add(Path.GetDirectoryName(targetPath).Replace('\\', '/'));
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

        private bool ShouldExcludeDependency(string depPath, HashSet<string> excludedExtensions, List<string> excludedFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(depPath))
            {
                return true;
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

            string[] parts = excludeExtensionsInput.Split(new[] { ',', '，', ';', '；', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
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

        private void FindCandidates(IllegalReferenceItem item)
        {
            if (item.searchingCandidates) return;
            item.searchingCandidates = true;
            item.candidatePaths.Clear();
            item.candidateStatus = "正在查找候选替换资源…";
            item.candidateStatusType = MessageType.Info;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var roots = GetCandidateRoots(item);
                    string scope = "搜索范围：" + string.Join("、", roots) + "\n匹配规则：文件名（含扩展名）相同，忽略大小写，并排除原资源。";
                    item.candidatePaths = SearchCandidatePaths(item, roots, CandidateLimit);
                    item.candidateStatus = roots.Length == 0
                        ? "无法查找候选：没有有效的搜索目录。请重新查询或添加有效的排除目录。"
                        : item.candidatePaths.Count == 0
                            ? "未找到同名候选资源。\n" + scope
                            : $"找到 {item.candidatePaths.Count} 个候选资源" +
                              (item.candidatePaths.Count >= CandidateLimit ? $"（最多显示 {CandidateLimit} 个）" : "") + "。\n" + scope;
                    item.candidateStatusType = item.candidatePaths.Count == 0 ? MessageType.Warning : MessageType.Info;
                    statusMessage = item.candidatePaths.Count == 0
                        ? $"未找到候选：{item.fileName}（详见该项提示）"
                        : $"找到候选 {item.candidatePaths.Count} 个：{item.fileName}";
                }
                catch (OperationCanceledException)
                {
                    item.candidateStatus = "候选查找已取消，可再次点击查找。";
                    item.candidateStatusType = MessageType.Info;
                    statusMessage = $"候选查找已取消：{item.fileName}";
                }
                catch (Exception e)
                {
                    item.candidateStatus = "候选查找失败：" + e.Message;
                    item.candidateStatusType = MessageType.Error;
                    statusMessage = $"候选查找失败：{item.fileName}（详见该项提示）";
                    Debug.LogException(e);
                }
                finally
                {
                    item.searchingCandidates = false;
                    EditorUtility.ClearProgressBar();
                    Changed?.Invoke();
                }
            };
        }

        private string[] GetCandidateRoots(IllegalReferenceItem item)
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

            return roots.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private List<string> SearchCandidatePaths(IllegalReferenceItem item, string[] roots, int maxCount)
        {
            string fileName = Path.GetFileName(item.illegalPath);
            string fileNameNoExt = Path.GetFileNameWithoutExtension(item.illegalPath);
            string extension = Path.GetExtension(item.illegalPath);
            HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                string root = roots[rootIndex];
                if (EditorUtility.DisplayCancelableProgressBar("查找候选替换", root, (float)rootIndex / roots.Length))
                    throw new OperationCanceledException();
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
            ToolWorkflow.Preview(() =>
            {
                var plan = ReferenceRepair.Build(actionName, item.illegalGuid, item.illegalPath,
                    AssetDatabase.GUIDToAssetPath(newGuid), item.referencedByPaths, newGuid == ZeroGuid);
                plan.completed = RunQuery;
                return plan;
            });
        }

        private void CopyAndReplace(IllegalReferenceItem item)
        {
            ToolWorkflow.Preview(() =>
            {
                string folder = AssetDatabase.GetAssetPath(replaceCopyTargetFolder);
                if (!ToolPaths.Contains("Assets", folder) || !AssetDatabase.IsValidFolder(folder))
                    throw new InvalidOperationException("请选择 Assets 内的复制输出目录。");
                string destination = new CopyPathAllocator().Reserve(item.illegalPath, folder, "");
                var plan = ReferenceRepair.Build("复制并替换引用", item.illegalGuid, item.illegalPath,
                    item.illegalPath, item.referencedByPaths, identicalCopy: true, copyDestination: destination);
                plan.completed = RunQuery;
                return plan;
            });
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
