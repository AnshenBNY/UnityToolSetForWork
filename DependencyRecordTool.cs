using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    [Serializable]
    public class DependencyRecordFile
    {
        public string version = "1.0";
        public string createdAtUtc;
        public string projectName;
        public List<AssetRecordItem> roots = new();
        public List<DependencyRecordItem> dependencies = new();
    }

    [Serializable]
    public class AssetRecordItem
    {
        public string path;
        public string guid;
        public string fileName;
    }

    [Serializable]
    public class DependencyRecordItem
    {
        public string path;
        public string guid;
        public string fileName;
        public bool isRoot;
        public bool isBuiltIn;
        public List<string> referencedByRoots = new();
    }

    internal enum DependencyState
    {
        Present,
        Missing,
        BuiltIn
    }

    internal sealed class DependencyCheckItem
    {
        public string fileName;
        public string originalPath;
        public string guid;
        public string currentPath;
        public DependencyState state;
        public List<string> referencedByRoots;
        public List<string> candidatePaths = new();
    }

    internal sealed class DependencyCheckReport
    {
        public string sourceFileName;
        public int totalCount;
        public int presentCount;
        public int missingCount;
        public int builtInCount;
        public List<DependencyCheckItem> items = new();
        public List<string> rootPaths = new();
    }

    [Serializable]
    internal sealed class MissingReportExportItem
    {
        public string fileName;
        public string originalPath;
        public string guid;
        public string referencedByRoots;
    }

    [Serializable]
    internal sealed class MissingReportExportFile
    {
        public string sourceFileName;
        public string createdAtUtc;
        public int missingCount;
        public List<MissingReportExportItem> items = new();
    }

    public class DependencyRecordTool : IToolGUI
    {
        private const string PrefKeyJsonOutputDir = "ToolSet.DependencyRecordTool.JsonOutputDir";
        private const string PrefKeyJsonFilePrefix = "ToolSet.DependencyRecordTool.JsonFilePrefix";

        private readonly List<UnityEngine.Object> targets = new();
        private readonly GUIContent removeButton = new("移除");
        private Vector2 targetScroll;
        private Vector2 resultScroll;

        private TextAsset recordAsset;
        private string recordExternalPath = string.Empty;
        private string statusMessage = "等待操作";
        private bool onlyShowMissing = false;
        private bool includeBuiltInDependencies = false;
        private bool excludeEditorScripts = true;
        private string excludeExtensionsInput = ".cs,.js,.dll,.asmdef,.asmref";
        private readonly List<DefaultAsset> excludeFolders = new();
        private Vector2 excludeFolderScroll;
        private int candidateMaxCount = 5;
        private string jsonOutputDirectory = string.Empty;
        private string jsonFilePrefix = "DependencyRecord";
        private bool preferencesLoaded = false;
        private bool targetFoldout = true;
        private bool exportFoldout = true;
        private bool parseFoldout = true;
        private static readonly HashSet<string> RepairSupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".mat", ".asset", ".controller", ".overrideController", ".anim", ".playable", ".guiskin"
        };

        private DependencyCheckReport lastReport;

        public void DrawGUI()
        {
            EnsurePreferencesLoaded();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("依赖记录工具", EditorStyles.largeLabel);
            EditorGUILayout.HelpBox(
                "推荐流程：\n" +
                "① 选择目标资源/目录  ② 配置导出规则并保存 JSON  ③ 在目标工程解析 JSON 检查丢失依赖",
                MessageType.Info);
            EditorGUILayout.Space(6);

            DrawSectionCard("1) 目标选择", ref targetFoldout, DrawTargetSection);
            DrawSectionCard("2) 导出配置与保存", ref exportFoldout, DrawExportSection);
            DrawSectionCard("3) 解析检查与报告", ref parseFoldout, DrawParseSection);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField($"状态：{statusMessage}", EditorStyles.helpBox);
        }

        private static void DrawSectionCard(string title, ref bool foldout, Action drawContent)
        {
            EditorGUILayout.BeginVertical("box");
            foldout = EditorGUILayout.Foldout(foldout, title, true);
            if (foldout)
            {
                EditorGUILayout.Space(2);
                drawContent?.Invoke();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void DrawTargetSection()
        {
            EditorGUILayout.LabelField("1) 选择要记录依赖的对象", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("添加当前Selection"))
                {
                    AddFromSelection();
                }

                if (GUILayout.Button("清空目标"))
                {
                    targets.Clear();
                    statusMessage = "已清空目标列表";
                }
            }

            DrawDropArea();

            targetScroll = EditorGUILayout.BeginScrollView(targetScroll, GUILayout.Height(140));
            if (targets.Count == 0)
            {
                EditorGUILayout.HelpBox("请拖入目录或资源对象，或点击“添加当前Selection”。", MessageType.None);
            }
            else
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        targets[i] = EditorGUILayout.ObjectField(targets[i], typeof(UnityEngine.Object), false);
                        if (GUILayout.Button(removeButton, GUILayout.Width(64)))
                        {
                            targets.RemoveAt(i);
                            i--;
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawDropArea()
        {
            Rect dropRect = GUILayoutUtility.GetRect(0f, 50f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "拖拽目录/资源到这里");

            Event currentEvent = Event.current;
            if (!dropRect.Contains(currentEvent.mousePosition))
            {
                return;
            }

            if (currentEvent.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (UnityEngine.Object dragged in DragAndDrop.objectReferences)
                {
                    TryAddTarget(dragged);
                }

                currentEvent.Use();
            }
        }

        private void DrawExportSection()
        {
            DrawJsonOutputDirectoryControls();
            includeBuiltInDependencies = EditorGUILayout.ToggleLeft("包含 Unity 内建依赖（通常不建议）", includeBuiltInDependencies);
            excludeEditorScripts = EditorGUILayout.ToggleLeft("排除 Editor 目录资源", excludeEditorScripts);
            excludeExtensionsInput = EditorGUILayout.TextField("排除扩展名(逗号分隔)", excludeExtensionsInput);
            jsonFilePrefix = EditorGUILayout.TextField("JSON文件名前缀", jsonFilePrefix);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("排除目录规则", EditorStyles.boldLabel);
                if (GUILayout.Button("添加排除目录", GUILayout.Width(110)))
                {
                    excludeFolders.Add(null);
                }
            }

            excludeFolderScroll = EditorGUILayout.BeginScrollView(excludeFolderScroll, GUILayout.Height(72));
            for (int i = 0; i < excludeFolders.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    excludeFolders[i] = EditorGUILayout.ObjectField(excludeFolders[i], typeof(DefaultAsset), false) as DefaultAsset;
                    if (GUILayout.Button(removeButton, GUILayout.Width(64)))
                    {
                        excludeFolders.RemoveAt(i);
                        i--;
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存到输出目录(JSON)", GUILayout.Height(26)))
                {
                    SaveDependencyRecord(saveToOutputDirectory: true);
                }

                if (GUILayout.Button("另存为(JSON)...", GUILayout.Height(26)))
                {
                    SaveDependencyRecord(saveToOutputDirectory: false);
                }
            }
        }

        private void DrawParseSection()
        {
            EditorGUILayout.LabelField("3) 解析并检查依赖状态", EditorStyles.boldLabel);
            recordAsset = EditorGUILayout.ObjectField("记录文件(TextAsset)", recordAsset, typeof(TextAsset), false) as TextAsset;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("外部路径", string.IsNullOrEmpty(recordExternalPath) ? "(未选择)" : recordExternalPath);
                if (GUILayout.Button("选择JSON文件", GUILayout.Width(120)))
                {
                    string selectedPath = EditorUtility.OpenFilePanel("选择依赖记录JSON", Application.dataPath, "json");
                    if (!string.IsNullOrEmpty(selectedPath))
                    {
                        recordExternalPath = selectedPath;
                        statusMessage = $"已选择记录文件：{Path.GetFileName(selectedPath)}";
                    }
                }
            }

            if (GUILayout.Button("解析并检查依赖"))
            {
                ParseAndCheck();
            }

            if (lastReport == null)
            {
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"记录文件：{lastReport.sourceFileName}");
            EditorGUILayout.LabelField($"总依赖：{lastReport.totalCount}  |  存在：{lastReport.presentCount}  |  丢失：{lastReport.missingCount}  |  内建：{lastReport.builtInCount}");
            onlyShowMissing = EditorGUILayout.ToggleLeft("仅显示丢失项", onlyShowMissing);
            candidateMaxCount = EditorGUILayout.IntSlider("候选显示上限", candidateMaxCount, 1, 20);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("为丢失项搜索候选资源"))
                {
                    SearchCandidatesForMissing(lastReport, candidateMaxCount);
                }

                if (GUILayout.Button("导出丢失报告(CSV)"))
                {
                    ExportMissingReportCsv(lastReport);
                }

                if (GUILayout.Button("导出丢失报告(JSON)"))
                {
                    ExportMissingReportJson(lastReport);
                }
            }

            resultScroll = EditorGUILayout.BeginScrollView(resultScroll, GUILayout.Height(220));
            foreach (DependencyCheckItem item in lastReport.items)
            {
                if (onlyShowMissing && item.state != DependencyState.Missing)
                {
                    continue;
                }

                DrawCheckItem(item);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawCheckItem(DependencyCheckItem item)
        {
            MessageType messageType = item.state switch
            {
                DependencyState.Present => MessageType.Info,
                DependencyState.Missing => MessageType.Error,
                _ => MessageType.None
            };

            string stateText = item.state switch
            {
                DependencyState.Present => "已找到",
                DependencyState.Missing => "引用丢失",
                _ => "内建资源"
            };

            StringBuilder sb = new();
            sb.AppendLine($"{stateText} | 原文件名：{item.fileName}");
            sb.AppendLine($"原路径：{item.originalPath}");
            if (!string.IsNullOrEmpty(item.guid))
            {
                sb.AppendLine($"GUID：{item.guid}");
            }

            if (item.state == DependencyState.Present)
            {
                sb.AppendLine($"当前路径：{item.currentPath}");
            }

            if (item.referencedByRoots != null && item.referencedByRoots.Count > 0)
            {
                string roots = string.Join(", ", item.referencedByRoots);
                sb.AppendLine($"被以下根对象引用：{roots}");
            }

            EditorGUILayout.HelpBox(sb.ToString(), messageType);

            if (item.state == DependencyState.Missing && item.referencedByRoots != null && item.referencedByRoots.Count > 0)
            {
                EditorGUILayout.LabelField("引用来源对象（可跳转）:");
                for (int i = 0; i < item.referencedByRoots.Count; i++)
                {
                    string rootPath = item.referencedByRoots[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.SelectableLabel(rootPath, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        if (GUILayout.Button("跳转", GUILayout.Width(58)))
                        {
                            PingAssetByPath(rootPath);
                        }
                    }
                }
            }

            if (item.state == DependencyState.Missing && item.candidatePaths != null && item.candidatePaths.Count > 0)
            {
                EditorGUILayout.LabelField("候选资源（可定位）:");
                foreach (string candidate in item.candidatePaths)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.SelectableLabel(candidate, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        if (GUILayout.Button("Ping", GUILayout.Width(58)))
                        {
                            PingAssetByPath(candidate);
                        }

                        if (GUILayout.Button("使用该候补修复", GUILayout.Width(110)))
                        {
                            TryRepairMissingReference(item, candidate);
                        }
                    }
                }
            }
        }

        private void AddFromSelection()
        {
            int added = 0;
            foreach (UnityEngine.Object selected in Selection.objects)
            {
                if (TryAddTarget(selected))
                {
                    added++;
                }
            }

            statusMessage = added > 0 ? $"已添加 {added} 个对象" : "Selection 中没有可用的项目资源";
        }

        private bool TryAddTarget(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets", StringComparison.Ordinal))
            {
                return false;
            }

            if (targets.Contains(obj))
            {
                return false;
            }

            targets.Add(obj);
            return true;
        }

        private void SaveDependencyRecord(bool saveToOutputDirectory)
        {
            List<string> rootPaths = CollectRootAssetPaths();
            if (rootPaths.Count == 0)
            {
                statusMessage = "没有可用目标，请先添加目录或资源对象";
                EditorUtility.DisplayDialog("提示", "请先添加至少一个目录或资源对象。", "确定");
                return;
            }

            DependencyRecordFile data = BuildRecord(rootPaths);
            string defaultPrefix = string.IsNullOrWhiteSpace(jsonFilePrefix) ? "DependencyRecord" : jsonFilePrefix.Trim();
            string defaultFileName = $"{defaultPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string savePath = saveToOutputDirectory
                ? BuildOutputFilePath(defaultFileName)
                : EditorUtility.SaveFilePanel("保存依赖记录", GetSafeOutputDirectory(), Path.GetFileNameWithoutExtension(defaultFileName), "json");
            if (string.IsNullOrEmpty(savePath))
            {
                statusMessage = "已取消保存";
                return;
            }

            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(savePath, json, new UTF8Encoding(false));
            AssetDatabase.Refresh();

            statusMessage = $"依赖记录已保存：{savePath}";
            EditorUtility.DisplayDialog("完成", $"依赖记录已保存。\n{savePath}", "确定");
        }

        private void DrawJsonOutputDirectoryControls()
        {
            EditorGUILayout.LabelField("JSON输出目录", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(GetSafeOutputDirectory(), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("浏览", GUILayout.Width(70)))
                {
                    string selected = EditorUtility.OpenFolderPanel("选择JSON输出目录", GetSafeOutputDirectory(), string.Empty);
                    if (!string.IsNullOrEmpty(selected))
                    {
                        jsonOutputDirectory = selected;
                        SavePreferences();
                        statusMessage = $"已设置输出目录：{jsonOutputDirectory}";
                    }
                }

                if (GUILayout.Button("默认", GUILayout.Width(70)))
                {
                    jsonOutputDirectory = Application.dataPath;
                    SavePreferences();
                    statusMessage = $"输出目录已重置为默认：{jsonOutputDirectory}";
                }
            }
        }

        private string BuildOutputFilePath(string fileName)
        {
            string outputDir = GetSafeOutputDirectory();
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            SavePreferences();
            return Path.Combine(outputDir, fileName);
        }

        private string GetSafeOutputDirectory()
        {
            if (string.IsNullOrWhiteSpace(jsonOutputDirectory))
            {
                jsonOutputDirectory = Application.dataPath;
            }

            return jsonOutputDirectory;
        }

        private void EnsurePreferencesLoaded()
        {
            if (preferencesLoaded)
            {
                return;
            }

            jsonOutputDirectory = EditorPrefs.GetString(PrefKeyJsonOutputDir, Application.dataPath);
            jsonFilePrefix = EditorPrefs.GetString(PrefKeyJsonFilePrefix, "DependencyRecord");
            preferencesLoaded = true;
        }

        private void SavePreferences()
        {
            EditorPrefs.SetString(PrefKeyJsonOutputDir, GetSafeOutputDirectory());
            EditorPrefs.SetString(PrefKeyJsonFilePrefix, string.IsNullOrWhiteSpace(jsonFilePrefix) ? "DependencyRecord" : jsonFilePrefix.Trim());
        }

        private DependencyRecordFile BuildRecord(List<string> rootPaths)
        {
            HashSet<string> excludedExtensions = ParseExcludeExtensions();
            HashSet<string> rootSet = new(rootPaths);
            Dictionary<string, HashSet<string>> referencedByMap = BuildReferencedByMap(rootPaths);
            string[] allDependencies = AssetDatabase.GetDependencies(rootPaths.ToArray(), true);

            DependencyRecordFile data = new()
            {
                createdAtUtc = DateTime.UtcNow.ToString("o"),
                projectName = Application.productName
            };

            foreach (string rootPath in rootPaths.OrderBy(p => p, StringComparer.Ordinal))
            {
                data.roots.Add(new AssetRecordItem
                {
                    path = rootPath,
                    guid = AssetDatabase.AssetPathToGUID(rootPath),
                    fileName = Path.GetFileName(rootPath)
                });
            }

            foreach (string depPath in allDependencies.OrderBy(p => p, StringComparer.Ordinal))
            {
                string guid = AssetDatabase.AssetPathToGUID(depPath);
                bool isBuiltIn = IsBuiltInDependency(depPath, guid);
                if (ShouldExcludeAssetPath(depPath, isBuiltIn, excludedExtensions))
                {
                    continue;
                }

                if (isBuiltIn && !includeBuiltInDependencies)
                {
                    continue;
                }

                data.dependencies.Add(new DependencyRecordItem
                {
                    path = depPath,
                    guid = guid,
                    fileName = Path.GetFileName(depPath),
                    isRoot = rootSet.Contains(depPath),
                    isBuiltIn = isBuiltIn,
                    referencedByRoots = referencedByMap.TryGetValue(depPath, out HashSet<string> roots)
                        ? roots.OrderBy(v => v, StringComparer.Ordinal).ToList()
                        : new List<string>()
                });
            }

            return data;
        }

        private static Dictionary<string, HashSet<string>> BuildReferencedByMap(List<string> rootPaths)
        {
            Dictionary<string, HashSet<string>> map = new();
            foreach (string rootPath in rootPaths)
            {
                string[] dependencies = AssetDatabase.GetDependencies(rootPath, true);
                foreach (string dep in dependencies)
                {
                    if (!map.TryGetValue(dep, out HashSet<string> refs))
                    {
                        refs = new HashSet<string>();
                        map[dep] = refs;
                    }

                    refs.Add(rootPath);
                }
            }

            return map;
        }

        private List<string> CollectRootAssetPaths()
        {
            HashSet<string> excludedExtensions = ParseExcludeExtensions();
            HashSet<string> paths = new(StringComparer.Ordinal);

            foreach (UnityEngine.Object target in targets)
            {
                if (target == null)
                {
                    continue;
                }

                string targetPath = AssetDatabase.GetAssetPath(target);
                if (string.IsNullOrEmpty(targetPath) || !targetPath.StartsWith("Assets", StringComparison.Ordinal))
                {
                    continue;
                }

                if (AssetDatabase.IsValidFolder(targetPath))
                {
                    string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { targetPath });
                    foreach (string guid in guids)
                    {
                        string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                        {
                            continue;
                        }

                        if (ShouldExcludeAssetPath(assetPath, false, excludedExtensions))
                        {
                            continue;
                        }

                        paths.Add(assetPath);
                    }
                }
                else
                {
                    if (ShouldExcludeAssetPath(targetPath, false, excludedExtensions))
                    {
                        continue;
                    }

                    paths.Add(targetPath);
                }
            }

            return paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
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
                if (string.IsNullOrEmpty(ext))
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

        private bool ShouldExcludeAssetPath(string assetPath, bool isBuiltIn, HashSet<string> excludedExtensions)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return true;
            }

            if (isBuiltIn)
            {
                return false;
            }

            if (excludeEditorScripts && assetPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string extension = Path.GetExtension(assetPath);
            if (!string.IsNullOrEmpty(extension) && excludedExtensions.Contains(extension))
            {
                return true;
            }

            foreach (DefaultAsset excludeFolder in excludeFolders)
            {
                if (excludeFolder == null)
                {
                    continue;
                }

                string folderPath = AssetDatabase.GetAssetPath(excludeFolder);
                if (string.IsNullOrEmpty(folderPath))
                {
                    continue;
                }

                if (assetPath.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(assetPath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void ParseAndCheck()
        {
            string json = GetRecordJsonContent(out string sourceName);
            if (string.IsNullOrEmpty(json))
            {
                statusMessage = "未获取到可解析的 JSON 内容";
                EditorUtility.DisplayDialog("提示", "请先指定记录文件（TextAsset 或 JSON 文件路径）。", "确定");
                return;
            }

            DependencyRecordFile data;
            try
            {
                data = JsonUtility.FromJson<DependencyRecordFile>(json);
            }
            catch (Exception ex)
            {
                statusMessage = "JSON 解析失败";
                EditorUtility.DisplayDialog("错误", $"JSON 解析失败：\n{ex.Message}", "确定");
                return;
            }

            if (data == null || data.dependencies == null || data.dependencies.Count == 0)
            {
                statusMessage = "记录文件中没有依赖数据";
                EditorUtility.DisplayDialog("提示", "记录文件中没有有效的依赖数据。", "确定");
                return;
            }

            lastReport = BuildCheckReport(data, sourceName);
            statusMessage = $"检查完成：丢失 {lastReport.missingCount} / {lastReport.totalCount}";
        }

        private string GetRecordJsonContent(out string sourceName)
        {
            sourceName = "(未知)";
            if (recordAsset != null)
            {
                sourceName = recordAsset.name;
                return recordAsset.text;
            }

            if (!string.IsNullOrEmpty(recordExternalPath) && File.Exists(recordExternalPath))
            {
                sourceName = Path.GetFileName(recordExternalPath);
                return File.ReadAllText(recordExternalPath, Encoding.UTF8);
            }

            return null;
        }

        private static DependencyCheckReport BuildCheckReport(DependencyRecordFile data, string sourceFileName)
        {
            DependencyCheckReport report = new()
            {
                sourceFileName = sourceFileName,
                rootPaths = data.roots == null
                    ? new List<string>()
                    : data.roots
                        .Where(r => !string.IsNullOrEmpty(r.path))
                        .Select(r => r.path)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
            };

            foreach (DependencyRecordItem dep in data.dependencies)
            {
                DependencyCheckItem item = new()
                {
                    fileName = string.IsNullOrEmpty(dep.fileName) ? Path.GetFileName(dep.path) : dep.fileName,
                    originalPath = dep.path,
                    guid = dep.guid,
                    referencedByRoots = dep.referencedByRoots ?? new List<string>()
                };

                if (dep.isBuiltIn)
                {
                    item.state = DependencyState.BuiltIn;
                }
                else
                {
                    string currentPath = FindAssetPath(dep);
                    item.currentPath = currentPath;
                    item.state = string.IsNullOrEmpty(currentPath) ? DependencyState.Missing : DependencyState.Present;
                }

                report.items.Add(item);
            }

            report.items = report.items
                .OrderByDescending(i => i.state == DependencyState.Missing)
                .ThenBy(i => i.fileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            report.totalCount = report.items.Count;
            report.presentCount = report.items.Count(i => i.state == DependencyState.Present);
            report.missingCount = report.items.Count(i => i.state == DependencyState.Missing);
            report.builtInCount = report.items.Count(i => i.state == DependencyState.BuiltIn);
            return report;
        }

        private static string FindAssetPath(DependencyRecordItem dep)
        {
            if (!string.IsNullOrEmpty(dep.guid))
            {
                string pathByGuid = AssetDatabase.GUIDToAssetPath(dep.guid);
                if (!string.IsNullOrEmpty(pathByGuid))
                {
                    return pathByGuid;
                }
            }

            if (!string.IsNullOrEmpty(dep.path) && AssetDatabase.LoadMainAssetAtPath(dep.path) != null)
            {
                return dep.path;
            }

            return string.Empty;
        }

        private static bool IsBuiltInDependency(string depPath, string guid)
        {
            if (string.IsNullOrEmpty(depPath))
            {
                return true;
            }

            if (!depPath.StartsWith("Assets", StringComparison.Ordinal) && !depPath.StartsWith("Packages", StringComparison.Ordinal))
            {
                return true;
            }

            return string.IsNullOrEmpty(guid);
        }

        private void ExportMissingReportCsv(DependencyCheckReport report)
        {
            List<DependencyCheckItem> missingItems = report.items.Where(i => i.state == DependencyState.Missing).ToList();
            if (missingItems.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "当前没有丢失项可导出。", "确定");
                return;
            }

            string fileName = $"MissingDependencyReport_{DateTime.Now:yyyyMMdd_HHmmss}";
            string savePath = EditorUtility.SaveFilePanel("导出丢失依赖报告(CSV)", Application.dataPath, fileName, "csv");
            if (string.IsNullOrEmpty(savePath))
            {
                return;
            }

            StringBuilder sb = new();
            sb.AppendLine("FileName,OriginalPath,GUID,ReferencedByRoots");
            foreach (DependencyCheckItem item in missingItems)
            {
                string roots = item.referencedByRoots == null ? string.Empty : string.Join(" | ", item.referencedByRoots);
                sb.AppendLine($"{ToCsv(item.fileName)},{ToCsv(item.originalPath)},{ToCsv(item.guid)},{ToCsv(roots)}");
            }

            File.WriteAllText(savePath, sb.ToString(), new UTF8Encoding(false));
            statusMessage = $"丢失报告已导出：{savePath}";
            EditorUtility.DisplayDialog("完成", $"已导出 {missingItems.Count} 条丢失项。\n{savePath}", "确定");
        }

        private void ExportMissingReportJson(DependencyCheckReport report)
        {
            List<DependencyCheckItem> missingItems = report.items.Where(i => i.state == DependencyState.Missing).ToList();
            if (missingItems.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "当前没有丢失项可导出。", "确定");
                return;
            }

            string fileName = $"MissingDependencyReport_{DateTime.Now:yyyyMMdd_HHmmss}";
            string savePath = EditorUtility.SaveFilePanel("导出丢失依赖报告(JSON)", Application.dataPath, fileName, "json");
            if (string.IsNullOrEmpty(savePath))
            {
                return;
            }

            MissingReportExportFile exportData = new()
            {
                sourceFileName = report.sourceFileName,
                createdAtUtc = DateTime.UtcNow.ToString("o"),
                missingCount = missingItems.Count
            };

            foreach (DependencyCheckItem item in missingItems)
            {
                exportData.items.Add(new MissingReportExportItem
                {
                    fileName = item.fileName,
                    originalPath = item.originalPath,
                    guid = item.guid,
                    referencedByRoots = item.referencedByRoots == null ? string.Empty : string.Join(" | ", item.referencedByRoots)
                });
            }

            string json = JsonUtility.ToJson(exportData, true);
            File.WriteAllText(savePath, json, new UTF8Encoding(false));
            statusMessage = $"丢失报告已导出：{savePath}";
            EditorUtility.DisplayDialog("完成", $"已导出 {missingItems.Count} 条丢失项。\n{savePath}", "确定");
        }

        private static string ToCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        private static void SearchCandidatesForMissing(DependencyCheckReport report, int maxCount)
        {
            int validMax = Mathf.Max(1, maxCount);
            foreach (DependencyCheckItem item in report.items)
            {
                item.candidatePaths.Clear();
                if (item.state != DependencyState.Missing || string.IsNullOrWhiteSpace(item.fileName))
                {
                    continue;
                }

                string nameWithoutExt = Path.GetFileNameWithoutExtension(item.fileName);
                if (string.IsNullOrWhiteSpace(nameWithoutExt))
                {
                    continue;
                }

                string[] guids = AssetDatabase.FindAssets(nameWithoutExt);
                foreach (string guid in guids)
                {
                    string candidatePath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(candidatePath))
                    {
                        continue;
                    }

                    string candidateFileName = Path.GetFileName(candidatePath);
                    bool sameName = string.Equals(candidateFileName, item.fileName, StringComparison.OrdinalIgnoreCase);
                    bool sameNameWithoutExt = string.Equals(Path.GetFileNameWithoutExtension(candidatePath), nameWithoutExt, StringComparison.OrdinalIgnoreCase);
                    if (!sameName && !sameNameWithoutExt)
                    {
                        continue;
                    }

                    item.candidatePaths.Add(candidatePath);
                    if (item.candidatePaths.Count >= validMax)
                    {
                        break;
                    }
                }
            }
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

        private void TryRepairMissingReference(DependencyCheckItem item, string candidatePath)
        {
            if (item == null || string.IsNullOrWhiteSpace(candidatePath))
            {
                return;
            }

            if (item.state != DependencyState.Missing)
            {
                EditorUtility.DisplayDialog("提示", "当前项不是丢失引用，无需修复。", "确定");
                return;
            }

            if (string.IsNullOrWhiteSpace(item.guid))
            {
                EditorUtility.DisplayDialog("提示", "该丢失项没有 GUID，暂时无法自动修复。", "确定");
                return;
            }

            string candidateGuid = AssetDatabase.AssetPathToGUID(candidatePath);
            if (string.IsNullOrWhiteSpace(candidateGuid))
            {
                EditorUtility.DisplayDialog("提示", "候补资源 GUID 无效，无法修复。", "确定");
                return;
            }

            if (candidateGuid == item.guid)
            {
                EditorUtility.DisplayDialog("提示", "候补资源与缺失 GUID 相同，不需要修复。", "确定");
                return;
            }

            bool confirm = EditorUtility.DisplayDialog(
                "确认修复引用",
                $"将把引用 GUID\n{item.guid}\n替换为\n{candidateGuid}\n\n候补资源：{candidatePath}\n\n仅会修改可文本处理的资产文件。",
                "确认修复",
                "取消");
            if (!confirm)
            {
                return;
            }

            List<string> targetAssetPaths = CollectRepairTargetAssetPaths(item);
            if (targetAssetPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "没有找到可尝试修复的目标资源。", "确定");
                return;
            }

            int changedCount = 0;
            int scannedCount = 0;
            for (int i = 0; i < targetAssetPaths.Count; i++)
            {
                string assetPath = targetAssetPaths[i];
                if (!IsSupportedRepairFile(assetPath))
                {
                    continue;
                }

                string absolutePath = ToAbsolutePath(assetPath);
                if (!File.Exists(absolutePath))
                {
                    continue;
                }

                scannedCount++;
                string content = File.ReadAllText(absolutePath, Encoding.UTF8);
                if (!content.Contains(item.guid))
                {
                    continue;
                }

                string replaced = content.Replace(item.guid, candidateGuid);
                if (ReferenceEquals(content, replaced) || content == replaced)
                {
                    continue;
                }

                File.WriteAllText(absolutePath, replaced, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                changedCount++;
            }

            AssetDatabase.Refresh();
            string result = $"扫描 {scannedCount} 个资源，修复 {changedCount} 个。";
            if (changedCount > 0)
            {
                item.state = DependencyState.Present;
                item.currentPath = candidatePath;
                item.guid = candidateGuid;
                item.candidatePaths.Clear();
                statusMessage = $"修复完成：{item.fileName} -> {Path.GetFileName(candidatePath)}";
            }
            else
            {
                statusMessage = "未找到可替换的引用片段";
            }

            EditorUtility.DisplayDialog("修复结果", result, "确定");
            if (lastReport != null)
            {
                lastReport.presentCount = lastReport.items.Count(v => v.state == DependencyState.Present);
                lastReport.missingCount = lastReport.items.Count(v => v.state == DependencyState.Missing);
            }
        }

        private List<string> CollectRepairTargetAssetPaths(DependencyCheckItem item)
        {
            HashSet<string> targetPaths = new(StringComparer.OrdinalIgnoreCase);

            if (item.referencedByRoots != null)
            {
                foreach (string rootPath in item.referencedByRoots)
                {
                    if (!string.IsNullOrWhiteSpace(rootPath))
                    {
                        targetPaths.Add(rootPath);
                    }
                }
            }

            if (lastReport?.rootPaths != null)
            {
                foreach (string rootPath in lastReport.rootPaths)
                {
                    if (!string.IsNullOrWhiteSpace(rootPath))
                    {
                        targetPaths.Add(rootPath);
                    }
                }
            }

            foreach (string foundPath in FindAssetPathsContainingGuid(item.guid))
            {
                targetPaths.Add(foundPath);
            }

            return targetPaths.ToList();
        }

        private static IEnumerable<string> FindAssetPathsContainingGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                yield break;
            }

            string assetsAbsolutePath = Application.dataPath;
            if (!Directory.Exists(assetsAbsolutePath))
            {
                yield break;
            }

            foreach (string absolutePath in Directory.EnumerateFiles(assetsAbsolutePath, "*.*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(absolutePath);
                if (!RepairSupportedExtensions.Contains(extension))
                {
                    continue;
                }

                string content;
                try
                {
                    content = File.ReadAllText(absolutePath, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                if (!content.Contains(guid))
                {
                    continue;
                }

                string assetPath = ToAssetPath(absolutePath);
                if (!string.IsNullOrWhiteSpace(assetPath))
                {
                    yield return assetPath;
                }
            }
        }

        private static bool IsSupportedRepairFile(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            string extension = Path.GetExtension(assetPath);
            return RepairSupportedExtensions.Contains(extension);
        }

        private static string ToAbsolutePath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            if (!assetPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string relative = assetPath.Substring("Assets".Length).TrimStart('/', '\\');
            return Path.Combine(Application.dataPath, relative);
        }

        private static string ToAssetPath(string absolutePath)
        {
            string assetsAbsolutePath = Application.dataPath;
            if (!absolutePath.StartsWith(assetsAbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string relative = absolutePath.Substring(assetsAbsolutePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return "Assets/" + relative.Replace('\\', '/');
        }
    }
}
