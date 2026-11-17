using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace ToolSet
{
    internal static class PrefabCopyPlan
    {
        static string Category(string path, HashSet<string> particleMaterials)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null) return null;
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return "Prefabs";
            if (AssetImporter.GetAtPath(path) is ModelImporter || typeof(Mesh).IsAssignableFrom(type)) return "Meshes";
            if (typeof(Material).IsAssignableFrom(type)) return particleMaterials.Contains(path) ? "FxMaterials" : "Materials";
            if (typeof(Texture).IsAssignableFrom(type) || typeof(Sprite).IsAssignableFrom(type)) return "Textures";
            if (typeof(Font).IsAssignableFrom(type)) return "ImageText";
            if (typeof(AnimationClip).IsAssignableFrom(type) || typeof(RuntimeAnimatorController).IsAssignableFrom(type) || typeof(PlayableAsset).IsAssignableFrom(type) || typeof(Avatar).IsAssignableFrom(type)) return "Animations";
            if (typeof(AudioClip).IsAssignableFrom(type)) return "Audio";
            return null;
        }
        internal static ToolPlan Build(GameObject source, DefaultAsset output, IEnumerable<DefaultAsset> exclusions, Action<string> completed)
            => Build(new[] { source }, output, exclusions, paths => completed?.Invoke(paths[0]));

        internal static bool IsSourcePrefab(Object source)
        {
            string path = AssetDatabase.GetAssetPath(source);
            return source is GameObject && EditorUtility.IsPersistent(source) &&
                ToolPaths.Contains("Assets", path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                AssetDatabase.LoadMainAssetAtPath(path) == source;
        }

        internal static ToolPlan Build(IEnumerable<GameObject> sources, DefaultAsset output, IEnumerable<DefaultAsset> exclusions, Action<string[]> completed)
        {
            var inputs = sources?.ToArray() ?? Array.Empty<GameObject>();
            if (inputs.Length == 0) throw new InvalidOperationException("请先添加至少一个预制体。");
            for (int i = 0; i < inputs.Length; i++)
                if (!IsSourcePrefab(inputs[i])) throw new InvalidOperationException($"第 {i + 1} 项无效，请选择 Assets 中的 .prefab 资源；场景实例请先保存为预制体。");
            var roots = inputs.GroupBy(AssetDatabase.GetAssetPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            var sourcePaths = roots.Select(AssetDatabase.GetAssetPath).ToArray();
            string folder = AssetDatabase.GetAssetPath(output);
            if (!AssetDatabase.IsValidFolder(folder) || !ToolPaths.Contains("Assets", folder)) throw new InvalidOperationException("输出目录必须位于 Assets 内。");
            var excluded = (exclusions ?? Enumerable.Empty<DefaultAsset>()).Where(e => e != null).Select(AssetDatabase.GetAssetPath).ToArray();
            foreach (string sourcePath in sourcePaths)
                if (excluded.Any(e => ToolPaths.Contains(e, sourcePath))) throw new InvalidOperationException("源预制体位于排除目录，请调整排除规则：" + sourcePath);
            var overview = new CopyOverview { sourceName = roots.Length == 1 ? roots[0].name : $"{roots.Length} 个预制体",
                sourcePath = roots.Length == 1 ? sourcePaths[0] : null, outputFolder = folder, fullCopy = true,
                description = roots.Length > 1 ? "每个预制体各生成一个副本；本批次共用的资源只复制一份，副本之间继续共用。" : null };
            var plan = new ToolPlan { title = roots.Length == 1 ? "复制预制体" : $"批量复制 {roots.Length} 个预制体", owner = PrefabCopyTool.CompleteWorkflowOwner, overview = overview };
            var particleMaterials = new HashSet<string>(roots.SelectMany(source => source.GetComponentsInChildren<ParticleSystemRenderer>(true)).SelectMany(r => r.sharedMaterials.Concat(new[] { r.trailMaterial })).Where(m => m != null).Select(AssetDatabase.GetAssetPath), StringComparer.OrdinalIgnoreCase);
            var allocator = new CopyPathAllocator();
            var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // One union and one mapping for the whole batch, including roots nested in other selected roots.
            var dependencies = sourcePaths.SelectMany(path => AssetDatabase.GetDependencies(path, true)).Concat(sourcePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (string path in dependencies)
            {
                if (!File.Exists(path)) continue;
                string category = Category(path, particleMaterials);
                if (category == null || excluded.Any(e => ToolPaths.Contains(e, path)))
                {
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    string reason = excluded.Any(e => ToolPaths.Contains(e, path)) ? "位于排除目录" :
                        (extension == ".cs" || extension == ".shader" || extension == ".shadergraph" ? "共用脚本 / 着色器" : "此类型暂不复制");
                    overview.resources.Add(new CopyResource { source = path, category = CopyOverview.CategoryName(category), reason = reason });
                    plan.Add("保留共享引用 " + path, _ => { }, "正在整理共享资源");
                    continue;
                }
                if (!ToolPaths.Contains("Assets", path))
                {
                    overview.resources.Add(new CopyResource { source = path, category = CopyOverview.CategoryName(category), reason = "来自资源包" });
                    plan.Add("保留包内资源 " + path, _ => { }, "正在整理共享资源");
                    continue;
                }
                try { ToolPaths.RequireSavedAsset(path); } catch (Exception e) { plan.blockingErrors.Add(e.Message); }
                string targetFolder = folder + "/" + category;
                if (File.Exists(targetFolder)) { plan.blockingErrors.Add("分类目录被同名文件占用：" + targetFolder); continue; }
                folders.Add(targetFolder);
                string destination = allocator.Reserve(path, targetFolder);
                overview.resources.Add(new CopyResource { source = path, destination = destination, category = CopyOverview.CategoryName(category) });
                copies.Add(path, destination); plan.Watch(path); plan.Watch(destination);
            }
            foreach (string sourcePath in sourcePaths)
            {
                if (!copies.ContainsKey(sourcePath)) throw new InvalidOperationException("源预制体未加入复制计划：" + sourcePath);
                overview.roots.Add(overview.resources.Single(r => r.source.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)));
            }
            // Folder creation is explicit and does not import/load resources under StartAssetEditing.
            foreach (string directory in folders.OrderBy(p => p))
            {
                if (AssetDatabase.IsValidFolder(directory)) continue;
                plan.Add("创建分类目录 " + directory, transaction =>
                {
                    if (Directory.Exists(directory)) throw new IOException("输出目录在预览后被创建，请重新预览：" + directory);
                    transaction.CaptureNewFolder(directory);
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(folder, Path.GetFileName(directory)))) throw new IOException("创建目录失败：" + directory);
                }, "正在准备输出目录");
            }
            foreach (var copy in copies)
                plan.Add("复制 " + copy.Key + " → " + copy.Value, transaction =>
                {
                    transaction.Capture(copy.Value);
                    if (!AssetDatabase.CopyAsset(copy.Key, copy.Value)) throw new IOException("复制失败：" + copy.Key);
                    AssetDatabase.ImportAsset(copy.Value, ImportAssetOptions.ForceSynchronousImport);
                }, "正在复制：" + Path.GetFileName(copy.Key));
            var guidMap = new Dictionary<string, string>();
            plan.Add("建立源资源与副本 GUID 映射", transaction =>
            {
                foreach (var copy in copies)
                {
                    string guid = AssetDatabase.AssetPathToGUID(copy.Value);
                    if (string.IsNullOrEmpty(guid)) throw new IOException("副本导入失败：" + copy.Value);
                    guidMap[AssetDatabase.AssetPathToGUID(copy.Key)] = guid;
                }
            }, "正在关联副本资源");
            foreach (var copy in copies)
                plan.Add("更新副本对象引用及导入器引用 " + copy.Value, transaction =>
                {
                    Rewrite(copy.Value, guidMap, true);
                    Rewrite(copy.Value + ".meta", guidMap, false);
                    AssetDatabase.ImportAsset(copy.Value, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }, "正在更新资源引用");
            string[] rootCopies = sourcePaths.Select(path => copies[path]).ToArray();
            overview.resultPath = rootCopies.Length == 1 ? rootCopies[0] : null;
            plan.Add("复查整批副本的旧资源引用", transaction =>
            {
                ReportRetainedReferences(overview, copies);
            }, "正在检查整批资源引用", keepChangesOnFailure: true);
            foreach (string rootCopy in rootCopies)
                plan.Add("验证预制体副本 " + rootCopy, transaction =>
            {
                var loaded = PrefabUtility.LoadPrefabContents(rootCopy);
                try
                {
                    if (loaded == null) throw new IOException("根预制体副本无法加载。");
                    foreach (var particle in loaded.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    {
                        var meshes = new Mesh[particle.meshCount]; particle.GetMeshes(meshes);
                        foreach (string path in meshes.Where(m => m != null).Select(AssetDatabase.GetAssetPath).Distinct())
                            if (copies.TryGetValue(path, out string expected))
                                overview.AddReferenceWarning(rootCopy, path, expected, "粒子 Mesh 槽仍引用原模型：" + particle.name);
                    }
                }
                finally { if (loaded != null) PrefabUtility.UnloadPrefabContents(loaded); }
            }, "正在检查：" + Path.GetFileName(rootCopy), keepChangesOnFailure: true);
            plan.completed = () => completed?.Invoke(rootCopies);
            return plan;
        }
        internal static void ReportRetainedReferences(CopyOverview overview, IDictionary<string, string> copies)
        {
            // An importer may recreate references (for example textures on embedded FBX
            // materials) that are not serialized in the asset's YAML or .meta file.
            // Keep the successfully copied batch and report each owner/dependency pair.
            foreach (string owner in copies.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                foreach (string dependency in AssetDatabase.GetDependencies(owner, false).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (copies.TryGetValue(dependency, out string expected))
                        overview.AddReferenceWarning(owner, dependency, expected, "导入后的副本仍引用原资源");
        }
        static void Rewrite(string path, Dictionary<string, string> map, bool requireYaml)
        {
            if (!File.Exists(path)) return;
            // Models/textures/audio are binary; only read serialized Unity text as UTF-8.
            if (requireYaml)
            {
                using (var reader = new StreamReader(path))
                    if (reader.ReadLine()?.StartsWith("%YAML", StringComparison.Ordinal) != true) return;
            }
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191;
            string content = File.ReadAllText(path);
            string result = ReferenceRepair.Reference.Replace(content, m => map.TryGetValue(m.Groups["guid"].Value, out var guid) ? m.Value.Replace(m.Groups["guid"].Value, guid) : m.Value);
            if (result != content) File.WriteAllText(path, result, new UTF8Encoding(bom));
        }
    }

    public partial class PrefabCopyTool
    {
        internal const string CompleteWorkflowOwner = nameof(PrefabCopyTool) + ".Complete";
        internal const string SceneWorkflowOwner = nameof(PrefabCopyTool) + ".Scene";
        readonly CopyPathAllocator copyPaths = new CopyPathAllocator();
        readonly HashSet<string> copiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<GameObject> sourcePrefabs = new List<GameObject>();
        readonly HashSet<string> rootCopyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ToolFileTransaction activeTransaction;
        bool showAdvanced;
        bool showExcludeFolders;
        int sourcePage;
        string sourceNotice;
        bool sourceNoticeWarning;
        public override string WorkflowOwner => showAdvanced ? SceneWorkflowOwner : CompleteWorkflowOwner;
        string OutputInputSignature() => AssetDatabase.GetAssetPath(outputFolder) + "|" +
            string.Join("|", excludeFolders.Select(f => f == null ? "" : AssetDatabase.GetAssetPath(f)));
        string CompleteCopyInputSignature() => string.Join(",", sourcePrefabs.Select(p => p == null ? "<missing>" : p.GetInstanceID().ToString())) + "|" + OutputInputSignature();
        string SceneInstanceInputSignature() => string.Join(",", sceneInstances.Select(p => p == null ? "<missing>" : p.GetInstanceID().ToString()));
        string SceneCopyInputSignature() => SceneInstanceInputSignature() + "|" + OutputInputSignature();

        internal void MigrateLegacyCopyInput()
        {
            if (sourcePrefabs.Count == 0 && PrefabCopyPlan.IsSourcePrefab(rootPrefab))
            {
                sourcePrefabs.Add(rootPrefab);
                rootPrefab = null;
            }
        }

        internal void AddSourcePrefabs(IEnumerable<Object> objects)
        {
            int added = 0, repeated = 0, rejected = 0;
            var known = new HashSet<string>(sourcePrefabs.Where(p => p != null).Select(AssetDatabase.GetAssetPath), StringComparer.OrdinalIgnoreCase);
            foreach (var obj in objects)
            {
                if (!PrefabCopyPlan.IsSourcePrefab(obj)) { rejected++; continue; }
                if (!known.Add(AssetDatabase.GetAssetPath(obj))) { repeated++; continue; }
                sourcePrefabs.Add((GameObject)obj); added++;
            }
            sourceNotice = $"已添加 {added} 个预制体" + (repeated > 0 ? $"，跳过 {repeated} 个重复项" : "") +
                (rejected > 0 ? $"；{rejected} 项未添加，仅支持 Assets 中的 .prefab 资源。场景实例请先保存为预制体。" : "。");
            sourceNoticeWarning = rejected > 0;
            if (added > 0)
            {
                sourcePage = (sourcePrefabs.Count - 1) / 6;
                ToolWorkflow.DiscardPreparedPlan(CompleteWorkflowOwner);
            }
        }

        void DrawSourcePrefabs(string title = "源预制体")
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"{title} · {sourcePrefabs.Count} 个", EditorStyles.boldLabel);
                var drop = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
                GUI.Box(drop, "将 Project 中的一个或多个预制体拖到这里", EditorStyles.helpBox);
                var evt = Event.current;
                if (GUI.enabled && drop.Contains(evt.mousePosition) && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
                {
                    DragAndDrop.visualMode = DragAndDrop.objectReferences.Any(PrefabCopyPlan.IsSourcePrefab) ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        AddSourcePrefabs(DragAndDrop.objectReferences);
                    }
                    evt.Use();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("添加 Project 中选中的预制体")) AddSourcePrefabs(Selection.objects);
                    using (new EditorGUI.DisabledScope(sourcePrefabs.Count == 0))
                        if (GUILayout.Button("清空列表", GUILayout.Width(80))) { sourcePrefabs.Clear(); sourcePage = 0; sourceNotice = null; }
                }
                if (!string.IsNullOrEmpty(sourceNotice)) EditorGUILayout.HelpBox(sourceNotice, sourceNoticeWarning ? MessageType.Warning : MessageType.Info);
                if (sourcePrefabs.Count > 6) ToolUi.DrawPager(ref sourcePage, sourcePrefabs.Count, 6);
                sourcePage = Mathf.Clamp(sourcePage, 0, Math.Max(0, (sourcePrefabs.Count - 1) / 6));
                int end = Math.Min(sourcePrefabs.Count, (sourcePage + 1) * 6);
                for (int i = sourcePage * 6; i < end; i++)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label((i + 1).ToString(), GUILayout.Width(25));
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(sourcePrefabs[i], typeof(GameObject), false);
                        if (GUILayout.Button("移除", GUILayout.Width(50))) { sourcePrefabs.RemoveAt(i--); end--; sourceNotice = null; }
                    }
                if (sourcePrefabs.Count == 0) EditorGUILayout.LabelField("直接添加原预制体即可，无须在场景中组装临时预制体。", EditorStyles.wordWrappedMiniLabel);
            }
        }

        internal ToolPlan PrepareCompleteCopy()
        {
            string input = CompleteCopyInputSignature();
            var plan = PrefabCopyPlan.Build(sourcePrefabs, outputFolder, excludeFolders, paths =>
            {
                foreach (string path in paths) rootCopyPaths.Add(path);
                currentStatus = $"已保存 {paths.Length} 个预制体副本。";
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(paths[0]));
            });
            plan.overview.placeInScene = PlaceCopiesInScene;
            plan.validate = () => CompleteCopyInputSignature() != input ? "复制设置已更改，请重新整理复制清单。" : null;
            return plan;
        }

        void SelectCurrentInstance(GameObject instance)
        {
            rootPrefab = instance;
            string path = instance == null ? null : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
            rootPrefabCopyPath = path != null && rootCopyPaths.Contains(path) ? path : null;
            rootPrefabCopy = string.IsNullOrEmpty(rootPrefabCopyPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(rootPrefabCopyPath);
        }

        internal void PlaceCopiesInScene(string[] paths)
        {
            if (ToolWorkflow.Running || paths == null || paths.Length == 0) return;
            var assets = paths.Select(AssetDatabase.LoadAssetAtPath<GameObject>).ToArray();
            if (assets.Any(a => a == null)) throw new InvalidOperationException("部分副本已被删除或移动，请重新复制或在 Project 中定位资源。");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("放入预制体副本");
            var instances = new List<GameObject>();
            try
            {
                foreach (var asset in assets)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                    if (instance == null) throw new InvalidOperationException("无法将副本放入场景：" + asset.name);
                    Undo.RegisterCreatedObjectUndo(instance, "放入预制体副本");
                    instances.Add(instance);
                }
                Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            SelectCurrentInstance(instances[0]);
            sceneInstances.Clear(); sceneInstances.AddRange(instances);
            ToolWorkflow.ClearPage(SceneWorkflowOwner, "副本已放入场景，请选择需要加工的资源类型。");
            showAdvanced = true;
            Selection.objects = instances.Cast<Object>().ToArray();
        }

        void PrepareCopyStep(string title, Action<GameObject> execute)
            => ToolWorkflow.Preview(() => BuildSceneCopyPlan(title, execute), SceneWorkflowOwner);
    }
}
