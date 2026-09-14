using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    internal enum ToolPlanPhase { Ready, Running, Succeeded, Failed, Cancelled }

    internal sealed class CopyResource
    {
        public string source, destination, category, reason;
        public bool Shared => string.IsNullOrEmpty(destination);
        public bool Renamed => !Shared && Path.GetFileNameWithoutExtension(destination) != Path.GetFileNameWithoutExtension(source) + "_Copy";
    }

    // The artist-facing list describes resources once; execution may need several steps per file.
    internal sealed class CopyOverview
    {
        public string sourceName, sourcePath, outputFolder, resultPath;
        public string actionLabel = "开始复制", description;
        public bool fullCopy;
        public readonly List<CopyResource> resources = new List<CopyResource>();
        public readonly List<CopyResource> roots = new List<CopyResource>();
        public readonly List<GameObject> sceneTargets = new List<GameObject>();
        public Action<string[]> placeInScene;
        public string[] ResultPaths => roots.Count > 0 ? roots.Select(r => r.destination).ToArray() :
            (string.IsNullOrEmpty(resultPath) ? Array.Empty<string>() : new[] { resultPath });
        public int CopyCount => resources.Count(r => !r.Shared);
        public int SharedCount => resources.Count(r => r.Shared);
        public string ReadyMessage => fullCopy ? $"将生成 {roots.Count} 个预制体副本 · 复制 {CopyCount} 个资源，沿用 {SharedCount} 个现有资源" : "内容已整理，点击下方「" + actionLabel + "」继续";
        public string SuccessMessage => fullCopy ? $"复制完成：{roots.Count} 个预制体副本，共保存 {CopyCount} 个资源" : actionLabel.Replace("开始", "") + "完成";

        bool showFiles, showShared, showTechnical;
        string search = "";
        int resourcePage, sharedPage, logPage, categoryIndex, rootPage, scenePage;
        string selectedSource;
        Vector2 logScroll;

        public static string CategoryName(string folder)
        {
            switch (folder)
            {
                case "Prefabs": return "预制体";
                case "Materials": case "FxMaterials": return "材质";
                case "Meshes": return "模型与网格";
                case "Textures": return "贴图";
                case "Animations": return "动画";
                case "ImageText": return "字体";
                case "Audio": return "音频";
                default: return "其他资源";
            }
        }

        public void Draw(ToolPlan plan)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(plan.phase == ToolPlanPhase.Succeeded ? (fullCopy ? "复制结果" : "操作结果") : (fullCopy ? "复制清单" : "操作内容"), EditorStyles.boldLabel);
                EditorGUILayout.LabelField(new GUIContent(sourceName ?? "当前实例", sourcePath), EditorStyles.boldLabel);
                if (!string.IsNullOrEmpty(description)) GUILayout.Label(description, EditorStyles.wordWrappedLabel);

                if (sceneTargets.Count > 0)
                {
                    EditorGUILayout.LabelField($"处理实例 · {sceneTargets.Count} 个", EditorStyles.miniBoldLabel);
                    if (sceneTargets.Count > 6) ToolUi.DrawPager(ref scenePage, sceneTargets.Count, 6);
                    foreach (var target in sceneTargets.Skip(scenePage * 6).Take(6))
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(target, typeof(GameObject), true);
                }
                if (!string.IsNullOrEmpty(outputFolder))
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField("输出位置", EditorStyles.miniBoldLabel);
                    DrawPath(outputFolder);
                }
                if (roots.Count > 0) DrawRoots(plan.phase == ToolPlanPhase.Succeeded);
                else if (!string.IsNullOrEmpty(resultPath))
                    EditorGUILayout.LabelField(new GUIContent("副本名称"), new GUIContent(Path.GetFileName(resultPath), resultPath));

                if (fullCopy)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField($"复制内容 · {CopyCount} 个资源", EditorStyles.miniBoldLabel);
                    var groups = resources.Where(r => !r.Shared).GroupBy(r => r.category).ToArray();
                    for (int i = 0; i < groups.Length; i += 3)
                        using (new EditorGUILayout.HorizontalScope())
                            foreach (var group in groups.Skip(i).Take(3))
                                GUILayout.Label($"{group.Key}  {group.Count()}", EditorStyles.helpBox, GUILayout.MinWidth(90));

                    int renamed = resources.Count(r => r.Renamed);
                    if (renamed > 0) EditorGUILayout.HelpBox($"{renamed} 个同名文件已自动编号，可展开文件明细查看新名称。", MessageType.Info);
                    showFiles = EditorGUILayout.Foldout(showFiles, "查看文件明细", true);
                    if (showFiles) DrawResources();

                    if (SharedCount > 0)
                    {
                        showShared = EditorGUILayout.Foldout(showShared, $"沿用现有资源 · {SharedCount} 个", true);
                        if (showShared)
                        {
                            EditorGUILayout.LabelField("以下资源继续使用原文件。", EditorStyles.wordWrappedMiniLabel);
                            var shared = resources.Where(r => r.Shared).ToArray();
                            ToolUi.DrawPager(ref sharedPage, shared.Length, 12);
                            foreach (var item in shared.Skip(sharedPage * 12).Take(12))
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    if (GUILayout.Button(new GUIContent(Path.GetFileName(item.source), item.source), EditorStyles.linkLabel)) Ping(item.source);
                                    GUILayout.Label(item.reason, EditorStyles.miniLabel, GUILayout.Width(140));
                                }
                        }
                    }
                }

                foreach (string error in plan.blockingErrors.Take(5)) EditorGUILayout.HelpBox("阻断执行：" + error, MessageType.Error);
                if (plan.blockingErrors.Count > 5) EditorGUILayout.LabelField($"另有 {plan.blockingErrors.Count - 5} 项阻断问题，详见操作记录。", EditorStyles.wordWrappedMiniLabel);
                // Skipped problems are visible without requiring the artist to open the log.
                foreach (string error in plan.errors.Take(5)) EditorGUILayout.HelpBox(error, MessageType.Warning);
                if (plan.errors.Count > 5) EditorGUILayout.LabelField($"另有 {plan.errors.Count - 5} 项问题，详见操作记录。", EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(4);
                showTechnical = EditorGUILayout.Foldout(showTechnical, "操作记录", true);
                if (showTechnical)
                {
                    string[] lines = plan.blockingErrors.Select(e => "[阻断] " + e).Concat(plan.errors.Select(e => "[问题] " + e)).Concat(plan.steps.Select(s => "[" + s.state + "] " + s.description)).ToArray();
                    ToolUi.DrawPager(ref logPage, lines.Length, 20);
                    logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.Height(120));
                    foreach (string line in lines.Skip(logPage * 20).Take(20)) EditorGUILayout.SelectableLabel(line, GUILayout.Height(18));
                    EditorGUILayout.EndScrollView();
                    if (GUILayout.Button("复制操作记录")) EditorGUIUtility.systemCopyBuffer = plan.title + "\n" + string.Join("\n", lines);
                }
            }
        }

        void DrawRoots(bool completed)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField($"预制体副本 · {roots.Count} 个", EditorStyles.miniBoldLabel);
            if (roots.Count > 6) ToolUi.DrawPager(ref rootPage, roots.Count, 6);
            foreach (var item in roots.Skip(rootPage * 6).Take(6))
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = item.source == item.destination ? Path.GetFileName(item.destination) : Path.GetFileName(item.source) + " → " + Path.GetFileName(item.destination);
                    GUILayout.Label(new GUIContent(label,
                        item.source + "\n" + item.destination), EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
                    if (!completed) continue;
                    if (GUILayout.Button("定位", GUILayout.Width(44))) Ping(item.destination);
                    if (placeInScene != null && GUILayout.Button("放入场景", GUILayout.Width(72)))
                    {
                        string path = item.destination;
                        EditorApplication.delayCall += () => placeInScene(new[] { path });
                    }
                }
        }

        public void SelectResults()
        {
            var assets = ResultPaths.Select(AssetDatabase.LoadMainAssetAtPath).Where(a => a != null).ToArray();
            if (assets.Length == 0) return;
            Selection.objects = assets;
            EditorGUIUtility.PingObject(assets[0]);
        }

        public void PlaceResultsInScene()
        {
            var paths = ResultPaths;
            if (placeInScene != null) EditorApplication.delayCall += () => placeInScene(paths);
        }

        void DrawResources()
        {
            string oldSearch = search;
            search = EditorGUILayout.TextField("查找文件", search);
            var categories = new[] { "全部类型" }.Concat(resources.Where(r => !r.Shared).Select(r => r.category).Distinct()).ToArray();
            int previous = categoryIndex;
            categoryIndex = EditorGUILayout.Popup("资源类型", Mathf.Clamp(categoryIndex, 0, categories.Length - 1), categories);
            if (search != oldSearch || previous != categoryIndex) resourcePage = 0;
            var rows = resources.Where(r => !r.Shared && (categoryIndex == 0 || r.category == categories[categoryIndex]) &&
                (r.source.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || r.destination.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            ToolUi.DrawPager(ref resourcePage, rows.Length, 12);
            foreach (var item in rows.Skip(resourcePage * 12).Take(12))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(item.category, EditorStyles.miniLabel, GUILayout.Width(75));
                    if (GUILayout.Button(new GUIContent(Path.GetFileName(item.source) + " → " + Path.GetFileName(item.destination), item.source + "\n" + item.destination), EditorStyles.linkLabel))
                        selectedSource = selectedSource == item.source ? null : item.source;
                }
                if (selectedSource == item.source)
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField("来源", EditorStyles.miniBoldLabel); DrawPath(item.source);
                        EditorGUILayout.LabelField("副本", EditorStyles.miniBoldLabel); DrawPath(item.destination);
                    }
            }
        }
        static void DrawPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(path, EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))))
                    if (GUILayout.Button("定位", GUILayout.Width(44))) Ping(path);
            }
        }
        static void Ping(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null) EditorGUIUtility.PingObject(asset);
        }
    }
}
