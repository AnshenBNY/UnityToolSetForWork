using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    // Plain panels have no hidden EditorWindow instances or update subscriptions.
    public abstract class ToolPanel : IToolGUI
    {
        public abstract void DrawGUI();
        public virtual void Invalidate() { }
    }

    internal static class ToolPaths
    {
        public static string Normalize(string path) => (path ?? "").Replace('\\', '/').TrimEnd('/');
        public static bool Contains(string folder, string path)
        {
            folder = Normalize(folder); path = Normalize(path);
            return folder.Length > 0 && (path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
        }
        public static string PreferenceKey(string key) => "ToolSet." + Hash128.Compute(Application.dataPath) + "." + key;
        public static string Fingerprint(string path)
        {
            if (!File.Exists(path)) return "<missing>";
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream));
        }
        public static void RequireWritableAsset(string path)
        {
            if (!Contains("Assets", path) || !File.Exists(path)) throw new InvalidOperationException("资源路径无效：" + path);
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("资源只读：" + path);
            if (EditorUtility.IsDirty(AssetDatabase.LoadMainAssetAtPath(path)))
                throw new InvalidOperationException("资源有未保存修改，请先保存再预览：" + path);
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == path && stage.scene.isDirty)
                throw new InvalidOperationException("预制体编辑模式有未保存修改：" + path);
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.path == path && scene.isDirty) throw new InvalidOperationException("场景有未保存修改：" + path);
                }
        }
    }

    internal sealed class CopyPathAllocator
    {
        readonly Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string Reserve(string source, string folder, string suffix = "_Copy")
        {
            string key = ToolPaths.Normalize(source) + "|" + ToolPaths.Normalize(folder);
            if (paths.TryGetValue(key, out var known)) return known;
            string stem = ToolPaths.Normalize(folder) + "/" + Path.GetFileNameWithoutExtension(source) + suffix;
            string ext = Path.GetExtension(source), result = stem + ext;
            int i = 1;
            while (reserved.Contains(result) || File.Exists(result) || File.Exists(result + ".meta") || Directory.Exists(result))
                result = stem + "_" + i++ + ext;
            reserved.Add(result); paths.Add(key, result); return result;
        }
        public void Clear() { paths.Clear(); reserved.Clear(); }
    }

    [Serializable] internal sealed class BackupEntry
    {
        public string path, backup, after;
        public bool existed, isDirectory;
    }
    [Serializable] internal sealed class BackupManifest
    {
        public string title;
        public List<BackupEntry> entries = new List<BackupEntry>();
    }
    internal sealed class ToolFileTransaction
    {
        readonly BackupManifest manifest = new BackupManifest();
        readonly string directory;
        public string ManifestPath => directory + "/manifest.json";
        public ToolFileTransaction(string title)
        {
            manifest.title = title;
            directory = "Library/ToolSetBackups/" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            Save();
        }
        public void Capture(string path, bool withMeta = true)
        {
            path = ToolPaths.Normalize(path);
            if (!ToolPaths.Contains("Assets", path)) throw new IOException("备份仅支持 Assets 内的文件：" + path);
            if (!manifest.entries.Any(e => e.path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                var entry = new BackupEntry { path = path, existed = File.Exists(path), backup = directory + "/" + manifest.entries.Count + ".bak" };
                if (entry.existed) File.Copy(path, entry.backup);
                manifest.entries.Add(entry); Save();
            }
            if (withMeta) Capture(path + ".meta", false);
        }
        public void CaptureNewFolder(string path)
        {
            if (Directory.Exists(path) || !ToolPaths.Contains("Assets", path)) throw new IOException("新目录路径已存在或无效：" + path);
            manifest.entries.Add(new BackupEntry { path = path, isDirectory = true, existed = false });
            Save(); Capture(path + ".meta", false);
        }
        public void Complete()
        {
            foreach (var entry in manifest.entries) entry.after = entry.isDirectory ? (Directory.Exists(entry.path) ? "<directory>" : "<missing>") : ToolPaths.Fingerprint(entry.path);
            Save();
            if (manifest.entries.Count > 0) EditorPrefs.SetString(ToolPaths.PreferenceKey("LastBackup"), Path.GetFullPath(ManifestPath));
        }
        void Save() => File.WriteAllText(ManifestPath, JsonUtility.ToJson(manifest, true));
        public void Rollback() => RestoreManifest(manifest, false);
        public static void RestoreLast()
        {
            string path = EditorPrefs.GetString(ToolPaths.PreferenceKey("LastBackup"));
            if (!File.Exists(path)) throw new IOException("没有可恢复的操作备份。");
            RestoreManifest(JsonUtility.FromJson<BackupManifest>(File.ReadAllText(path)), true);
            EditorPrefs.DeleteKey(ToolPaths.PreferenceKey("LastBackup"));
        }
        static void RestoreManifest(BackupManifest data, bool checkChanges)
        {
            // Preflight every entry before writing anything; do not overwrite later user edits.
            foreach (var entry in data.entries)
            {
                if (!ToolPaths.Contains("Assets", entry.path) || !Path.GetFullPath(entry.path).StartsWith(Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("无效的恢复路径：" + entry.path);
                string current = entry.isDirectory ? (Directory.Exists(entry.path) ? "<directory>" : "<missing>") : ToolPaths.Fingerprint(entry.path);
                if (checkChanges && current != entry.after)
                    throw new IOException("操作后文件又有修改，未恢复任何文件：" + entry.path);
                if (checkChanges && !entry.isDirectory)
                {
                    string assetPath = entry.path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? entry.path.Substring(0, entry.path.Length - 5) : entry.path;
                    if (File.Exists(assetPath)) ToolPaths.RequireWritableAsset(assetPath);
                    if (EditorUtility.IsDirty(AssetImporter.GetAtPath(assetPath)))
                        throw new IOException("导入设置有未保存修改，未恢复：" + assetPath);
                }
                if (entry.isDirectory && Directory.Exists(entry.path) && Directory.EnumerateFiles(entry.path, "*", SearchOption.AllDirectories)
                    .Any(p => !data.entries.Any(e => ToolPaths.Normalize(e.path).Equals(ToolPaths.Normalize(p), StringComparison.OrdinalIgnoreCase))))
                    throw new IOException("新目录包含操作范围之外的文件，未恢复：" + entry.path);
                if (entry.existed && !File.Exists(entry.backup)) throw new IOException("备份缺失：" + entry.backup);
            }
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in data.entries.AsEnumerable().Reverse())
                {
                    if (entry.isDirectory) { if (Directory.Exists(entry.path)) Directory.Delete(entry.path, false); continue; }
                    if (entry.existed) File.Copy(entry.backup, entry.path, true);
                    else if (File.Exists(entry.path)) File.Delete(entry.path);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); ToolAssetCache.Clear(); }
        }
    }

    internal sealed class ToolStep
    {
        public string description, progressLabel, state = "待执行";
        public Action<ToolFileTransaction> execute;
        public void Run(ToolFileTransaction transaction)
        {
            var errors = new List<string>();
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
            };
            Application.logMessageReceived += capture;
            try
            {
                execute(transaction);
                if (errors.Count > 0) throw new InvalidOperationException("Unity 报告执行错误：" + string.Join("；", errors));
            }
            finally { Application.logMessageReceived -= capture; }
        }
    }
    internal sealed class ToolPlan
    {
        public string title;
        public string owner;
        public CopyOverview overview;
        public ToolPlanPhase phase = ToolPlanPhase.Ready;
        public readonly List<ToolStep> steps = new List<ToolStep>();
        public readonly List<string> errors = new List<string>();
        public readonly Dictionary<string, string> fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Action completed, rollback;
        public Func<string> validate;
        public void Watch(string path)
        {
            if (!fingerprints.ContainsKey(path)) fingerprints[path] = ToolPaths.Fingerprint(path);
            if (File.Exists(path + ".meta") && !fingerprints.ContainsKey(path + ".meta")) fingerprints[path + ".meta"] = ToolPaths.Fingerprint(path + ".meta");
        }
        public void Add(string description, Action<ToolFileTransaction> action, string progressLabel = null) => steps.Add(new ToolStep { description = description, execute = action, progressLabel = progressLabel });
    }

    [InitializeOnLoad] internal static class ToolWorkflow
    {
        sealed class PageState
        {
            public ToolPlan plan;
            public string status = "选择对象 → 生成预览 → 执行 → 查看结果";
            public string filter = "";
            public int page;
            public bool onlyProblems;
            public bool buildFailed;
            public bool drawerExpanded;
            public Vector2 scroll;
            public Vector2 drawerScroll;
        }

        public static ToolPlan Plan { get; private set; }
        public static bool Running { get; private set; }
        public static string Status { get; private set; } = "选择对象 → 生成预览 → 执行 → 查看结果";
        public static event Action Changed;
        static readonly Dictionary<string, PageState> pages = new Dictionary<string, PageState>();
        static ToolFileTransaction transaction;
        static int next;
        static bool cancel;
        static string activeOwner;
        static string executionOwner;
        static ToolWorkflow()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Abort;
            EditorApplication.quitting += Abort;
        }
        static string OwnerKey(string owner) => string.IsNullOrEmpty(owner) ? "<默认>" : owner;
        static PageState Page(string owner)
        {
            string key = OwnerKey(owner);
            if (!pages.TryGetValue(key, out var state)) pages.Add(key, state = new PageState());
            return state;
        }
        public static void SetActiveOwner(string owner) => activeOwner = OwnerKey(owner);
        static void SetStatus(string value)
        {
            Status = value;
            if (!string.IsNullOrEmpty(executionOwner))
            {
                var state = Page(executionOwner);
                state.plan = Plan;
                state.status = value;
            }
        }
        public static void Preview(Func<ToolPlan> build, string owner = null)
        {
            if (Running) return;
            string requestedOwner = OwnerKey(owner ?? activeOwner);
            // Leave the current IMGUI enumeration/layout before changing lists or assets.
            EditorApplication.delayCall += () =>
            {
                if (Running) return;
                var state = Page(requestedOwner);
                state.buildFailed = false;
                try
                {
                    Plan = build();
                    Plan.owner = OwnerKey(Plan.owner ?? requestedOwner);
                    state = Page(Plan.owner);
                    state.plan = Plan;
                    state.status = Plan.overview?.ReadyMessage ?? $"预览：可执行 {Plan.steps.Count} 项，问题 {Plan.errors.Count} 项";
                    if (Plan.overview != null && Plan.errors.Count > 0) state.status += $" · 问题 {Plan.errors.Count} 项将跳过";
                    state.page = 0;
                    Status = state.status;
                }
                catch (Exception e)
                {
                    state.plan = null; state.buildFailed = true; state.status = "内容整理失败：" + e.Message;
                    Plan = null; Status = state.status; Debug.LogException(e);
                }
                finally { state.drawerExpanded = true; state.drawerScroll = Vector2.zero; EditorUtility.ClearProgressBar(); Changed?.Invoke(); }
            };
        }
        static void Start(string owner)
        {
            var state = Page(owner);
            Plan = state.plan;
            Status = state.status;
            executionOwner = OwnerKey(owner);
            Start();
        }
        public static void Start()
        {
            if (Running || Plan == null || Plan.phase != ToolPlanPhase.Ready || Plan.steps.Count == 0) return;
            executionOwner = OwnerKey(Plan.owner ?? executionOwner);
            try
            {
                string issue = Plan.validate?.Invoke();
                if (!string.IsNullOrEmpty(issue)) throw new InvalidOperationException(issue);
                foreach (var pair in Plan.fingerprints)
                    if (ToolPaths.Fingerprint(pair.Key) != pair.Value) throw new IOException("文件已变化，请重新整理操作内容：" + pair.Key);
                transaction = new ToolFileTransaction(Plan.title);
                next = 0; cancel = false; Running = true; Plan.phase = ToolPlanPhase.Running;
                foreach (var step in Plan.steps) step.state = "待执行";
                EditorApplication.update += Tick;
            }
            catch (Exception e) { SetStatus(e.Message); Plan.errors.Add(e.Message); Plan.phase = ToolPlanPhase.Failed; }
            Changed?.Invoke();
        }
        static void Tick()
        {
            if (!Running) return;
            try
            {
                if (cancel || EditorUtility.DisplayCancelableProgressBar(Plan.title, Plan.steps[next].progressLabel ?? $"{next}/{Plan.steps.Count}", (float)next / Plan.steps.Count))
                    throw new OperationCanceledException("用户取消");
                var step = Plan.steps[next];
                step.Run(transaction); step.state = "成功"; next++;
                SetStatus(Plan.overview != null ? (step.progressLabel ?? "正在处理") + $" · {Mathf.RoundToInt(100f * next / Plan.steps.Count)}%" : $"执行中：{next}/{Plan.steps.Count}");
                if (next == Plan.steps.Count)
                {
                    transaction.Complete();
                    Finish(); Plan.phase = ToolPlanPhase.Succeeded;
                    SetStatus(Plan.overview?.SuccessMessage ?? $"完成：成功 {next}，跳过问题 {Plan.errors.Count}");
                    // An optional rescan failing must not roll back a completed operation.
                    try { Plan.completed?.Invoke(); }
                    catch (Exception e) { SetStatus(Status + "；结果复查失败：" + e.Message); Debug.LogException(e); }
                }
            }
            catch (Exception e)
            {
                if (next < Plan.steps.Count) Plan.steps[next].state = e is OperationCanceledException ? "取消" : "失败：" + e.Message;
                SetStatus("执行未完成：" + e.Message);
                try { RollbackCurrent(); SetStatus(Status + "；已恢复修改"); foreach (var step in Plan.steps.Take(next)) step.state = "已恢复"; }
                catch (Exception restore) { SetStatus(Status + "；恢复失败：" + restore.Message + "，备份：" + transaction?.ManifestPath); Debug.LogException(restore); }
                Plan.errors.Add(Status); Finish();
                Plan.phase = e is OperationCanceledException ? ToolPlanPhase.Cancelled : ToolPlanPhase.Failed;
                if (!(e is OperationCanceledException)) Debug.LogException(e);
            }
            Changed?.Invoke();
        }
        static void Finish() { Running = false; EditorApplication.update -= Tick; EditorUtility.ClearProgressBar(); }
        static void RollbackCurrent()
        {
            var errors = new List<Exception>();
            try { Plan.rollback?.Invoke(); } catch (Exception e) { errors.Add(e); }
            try { transaction?.Rollback(); } catch (Exception e) { errors.Add(e); }
            if (errors.Count > 0) throw new AggregateException("部分恢复失败", errors);
        }
        static void Abort()
        {
            if (!Running) return;
            try { RollbackCurrent(); } catch (Exception e) { Debug.LogException(e); }
            finally { Finish(); }
        }
        public static void DiscardPreparedPlan(string owner)
        {
            var state = Page(owner);
            if (Running || state.plan == null || state.plan.phase == ToolPlanPhase.Succeeded) return;
            if (ReferenceEquals(Plan, state.plan)) Plan = null;
            state.plan = null; state.buildFailed = false;
            state.status = "设置已修改，请重新点击复制按钮整理清单。";
            Status = state.status;
            Changed?.Invoke();
        }
        public static void DrawSummary(string owner)
        {
            var plan = Page(owner).plan;
            plan?.overview?.Draw(plan);
        }
        static void RestoreLast(string owner)
        {
            if (Running) return;
            var state = Page(owner);
            if (!EditorUtility.DisplayDialog("确认恢复上次文件操作",
                "将恢复整个工具最近一次有文件变化的已完成操作，不一定是当前页面的操作。\n\n" +
                "• 删除该次操作新建的资源及 .meta，并清理新建的空目录。\n" +
                "• 还原该次操作对原有文件的修改或重命名。\n\n" +
                "场景中的实例不会自动删除，需要单独撤销。\n\n确定继续恢复吗？",
                "确认恢复", "取消")) return;
            try
            {
                ToolFileTransaction.RestoreLast();
                state.status = "已恢复上次文件操作，请重新整理清单或查询。";
                if (ReferenceEquals(Plan, state.plan)) Plan = null;
                state.plan = null; state.buildFailed = false; Status = state.status;
            }
            catch (Exception e) { state.status = e.Message; state.buildFailed = true; Status = state.status; }
            Changed?.Invoke();
        }
        static void DrawCopyActions(string owner, PageState state)
        {
            var plan = state.plan;
            bool runningHere = Running && ReferenceEquals(Plan, plan);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(runningHere ? Status : state.status, EditorStyles.wordWrappedLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (plan.phase == ToolPlanPhase.Ready)
                    {
                        using (new EditorGUI.DisabledScope(Running || plan.steps.Count == 0))
                            if (GUILayout.Button(plan.errors.Count > 0 ? "执行可用更改" : plan.overview.actionLabel, GUILayout.Height(30)))
                                EditorApplication.delayCall += () => Start(owner);
                    }
                    else if (runningHere)
                    {
                        var rect = GUILayoutUtility.GetRect(100, 20, GUILayout.ExpandWidth(true));
                        EditorGUI.ProgressBar(rect, (float)next / Math.Max(1, plan.steps.Count), $"{Mathf.RoundToInt(100f * next / Math.Max(1, plan.steps.Count))}%");
                        if (GUILayout.Button("取消复制", GUILayout.Width(80))) cancel = true;
                    }
                    else if (plan.phase == ToolPlanPhase.Succeeded && plan.overview.ResultPaths.Length > 0)
                    {
                        bool batch = plan.overview.roots.Count > 1;
                        if (GUILayout.Button(batch ? "定位全部副本" : "定位副本", GUILayout.Height(26))) plan.overview.SelectResults();
                        if (plan.overview.placeInScene != null && GUILayout.Button(batch ? "将全部副本放入场景" : "将副本放入场景", GUILayout.Height(26)))
                            plan.overview.PlaceResultsInScene();
                    }
                    using (new EditorGUI.DisabledScope(Running))
                        if (GUILayout.Button("恢复上次文件操作", GUILayout.Width(135))) EditorApplication.delayCall += () => RestoreLast(owner);
                }
            }
        }
        public static void Draw(string activeOwner = null, float windowHeight = 800)
        {
            string owner = OwnerKey(activeOwner);
            var state = Page(owner);
            var plan = state.plan;
            if (plan?.overview != null)
            {
                DrawCopyActions(owner, state);
                return;
            }
            if (owner == nameof(PrefabCopyTool) && !Running)
            {
                if (plan == null && state.status != "选择对象 → 生成预览 → 执行 → 查看结果")
                    EditorGUILayout.HelpBox(state.status, state.buildFailed ? MessageType.Error : MessageType.Info);
                if (EditorPrefs.HasKey(ToolPaths.PreferenceKey("LastBackup")))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("恢复上次文件操作", GUILayout.Width(135))) EditorApplication.delayCall += () => RestoreLast(owner);
                    }
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    state.drawerExpanded = EditorGUILayout.Foldout(state.drawerExpanded,
                        plan?.title ?? "操作预览与结果", true, EditorStyles.foldoutHeader);
                    string visibleStatus = Running && ReferenceEquals(Plan, plan) ? Status : state.status;
                    GUILayout.Label(new GUIContent(visibleStatus, visibleStatus), EditorStyles.miniLabel, GUILayout.MinWidth(0), GUILayout.MaxWidth(240));
                }
                if (!state.drawerExpanded) return;
                using (var drawer = new EditorGUILayout.ScrollViewScope(state.drawerScroll,
                    GUILayout.Height(plan == null ? 100 : Mathf.Clamp(windowHeight * 0.32f, 140, 280))))
                {
                    state.drawerScroll = drawer.scrollPosition;
                    DrawOperationDetails(owner, state);
                }
            }
        }

        static void DrawOperationDetails(string owner, PageState state)
        {
            var plan = state.plan;
            bool runningHere = Running && ReferenceEquals(Plan, plan);
            string visibleStatus = runningHere ? Status : state.status;
            EditorGUILayout.HelpBox(visibleStatus, plan != null && plan.errors.Count > 0 ? MessageType.Warning : MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(Running || plan == null || plan.steps.Count == 0 || plan.phase != ToolPlanPhase.Ready))
                    if (GUILayout.Button(plan != null && plan.errors.Count > 0 ? "执行可用更改" : "执行预览中的更改", GUILayout.Height(26)))
                        EditorApplication.delayCall += () => Start(owner);
                using (new EditorGUI.DisabledScope(!runningHere)) if (GUILayout.Button("取消", GUILayout.Width(60))) cancel = true;
                using (new EditorGUI.DisabledScope(Running)) if (GUILayout.Button("恢复上次文件操作", GUILayout.Width(135)))
                    EditorApplication.delayCall += () => RestoreLast(owner);
            }
            if (plan == null) return;
            state.filter = EditorGUILayout.TextField("筛选结果", state.filter);
            state.onlyProblems = EditorGUILayout.ToggleLeft("仅显示问题", state.onlyProblems);
            var lines = plan.errors.Select(e => "[问题] " + e).Concat(plan.steps.Where(s => !state.onlyProblems || s.state.StartsWith("失败") || s.state == "取消").Select(s => "[" + s.state + "] " + s.description))
                .Where(s => s.IndexOf(state.filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            const int size = 30;
            state.page = Mathf.Clamp(state.page, 0, Math.Max(0, (lines.Length - 1) / size));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("上一页", GUILayout.Width(65))) state.page = Math.Max(0, state.page - 1);
                GUILayout.Label($"{state.page + 1}/{Math.Max(1, (lines.Length + size - 1) / size)} 页 · {lines.Length} 项");
                if (GUILayout.Button("下一页", GUILayout.Width(65))) state.page = Math.Min(Math.Max(0, (lines.Length - 1) / size), state.page + 1);
                if (GUILayout.Button("复制报告", GUILayout.Width(80))) EditorGUIUtility.systemCopyBuffer = plan.title + "\n" + visibleStatus + "\n" + string.Join("\n", plan.errors.Concat(plan.steps.Select(s => s.state + " " + s.description)));
            }
            state.scroll = EditorGUILayout.BeginScrollView(state.scroll, GUILayout.Height(110));
            foreach (string line in lines.Skip(state.page * size).Take(size)) EditorGUILayout.SelectableLabel(line, GUILayout.Height(18));
            EditorGUILayout.EndScrollView();
        }
    }
}
