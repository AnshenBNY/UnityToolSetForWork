using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.SceneManagement;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.UI;

namespace ToolSet
{
    /// <summary>
    /// 预制体复制工具
    /// 功能概述：
    /// 1. 复制预制体及其所有子预制体到指定目录
    /// 2. 复制 MeshRenderer / SkinnedMeshRenderer 使用的材质
    ///    （由 "复制材质" 按钮触发，输出到 materialsCopyPath）
    /// 3. 复制粒子相关 Renderer 使用的材质，包括：
    ///    - ParticleSystemRenderer 主材质 与 粒子拖尾(trails)材质
    ///    - TrailRenderer 组件材质（独立拖尾组件）
    ///    （由 "复制粒子材质" 按钮触发，输出到 FxMaterialsCopyPath）
    /// 4. 复制粒子系统使用的网格资源（"复制粒子网格"按钮）
    /// 5. 复制根预制体下 MeshFilter 引用的 mesh 资源（"复制 MeshFilter 网格"按钮）
    ///    处理范围：根预制体散落节点 + 嵌套普通子 prefab 内部节点
    ///    支持类型：独立 .mesh / .asset 资源 与 模型文件(.fbx 等)中的子 mesh
    ///    自动安全跳过：嵌套模型类型预制体（PrefabAssetType.Model）的内部节点
    ///    （这些 mesh 已由"复制预制体"阶段通过模型整体复制 + GUID 替换正确处理）
    /// 6. 复制 PlayableDirector / Animator 使用的 Timeline(.playable) 与 Controller(.controller) 资源，
    ///    以及其中引用的动画片段；动画若来自模型文件(.fbx 等)则复制整个模型文件（"复制动画 Timeline"按钮）
    ///    复制 Timeline 后会自动恢复 PlayableDirector 轨道绑定（Animator / GameObject 等）及 ExposedReference
    /// 7. 复制 UI 组件相关资源（Image/Text）
    ///    - Image.material / Image.sprite / Image.overrideSprite
    ///    - Text.material / Text.font 及字体材质
    ///    （由 "复制Image/Text资源" 按钮触发，分步输出到 outputFolder；
    ///     一键复制输出到独立 ImageText 子目录）
    /// 8. 复制材质中的贴图资源并重定向材质贴图引用（"复制材质贴图"按钮）
    ///    分步输出默认使用 outputFolder；一键复制输出到独立 Textures 子目录
    /// 9. 支持排除指定目录的资源不被复制
    /// 10. "一键复制所有" / "一键复制并保存" 按面板顺序依次执行全部复制步骤；后者额外自动保存预制体
    ///    一键复制时会在输出文件夹下自动创建 Prefabs / Materials / FxMaterials / Meshes / ImageText / Textures / Animations 子目录
    ///    一键复制仅支持 Assets 中的预制体资源，不可使用场景中的预制体实例
    /// 
    /// 职责分工：
    ///   "复制材质" / "复制Image/Text资源" / "复制粒子材质" 按需组合使用：
    ///     - 模型类 prefab → 用 "复制材质"
    ///     - UI类 prefab（含 Image/Text）→ 用 "复制Image/Text资源"
    ///     - 特效类 prefab（含 ParticleSystem / TrailRenderer）→ 用 "复制粒子材质"
    ///     - 若特效 prefab 中同时含有 MeshRenderer 静态网格，仍由 "复制材质" 按钮处理
    /// 
    /// 使用流程：
    /// 1. 选择源预制体和输出目录
    /// 2. 添加需要排除的目录（可选）
    /// 3. 点击"复制预制体"按钮，复制预制体结构
    /// 4. 对复制后的预制体实例，依次执行材质和网格复制操作
    /// 5. 最后保存修改后的预制体
    /// </summary>
    public class PrefabCopyTool : EditorWindow, IToolGUI
    {
        #region 字段定义

        // ========== 预制体引用 ==========
        /// <summary>当前处理的源预制体（可以是Assets中的预制体或场景中的实例）</summary>
        private GameObject rootPrefab;
        /// <summary>复制后的预制体资源（Assets中）</summary>
        private GameObject rootPrefabCopy;

        // ========== 路径配置 ==========
        /// <summary>复制后的根预制体路径</summary>
        private string rootPrefabCopyPath;
        /// <summary>需要排除的文件夹列表（这些目录中的资源不会被复制）</summary>
        private List<DefaultAsset> excludeFolders = new List<DefaultAsset>();
        /// <summary>排除目录列表的滚动位置</summary>
        private Vector2 excludeScrollPosition;
        /// <summary>输出文件夹（运行时会写入下方各 *Path 字段）</summary>
        private DefaultAsset outputFolder;

        // ========== 输出路径（运行时由 outputFolder 决定，无需预设默认值） ==========
        /// <summary>预制体复制的根目录（点击"复制预制体"时由 outputFolder 写入）</summary>
        private string newFolderPath;
        /// <summary>材质复制的目录（点击"复制材质"时由 outputFolder 写入）</summary>
        private string materialsCopyPath;
        /// <summary>特效材质复制的目录（点击"复制粒子材质"时由 outputFolder 写入）</summary>
        private string FxMaterialsCopyPath;
        /// <summary>特效模型/网格复制的目录（点击"复制粒子网格"时由 outputFolder 写入）</summary>
        private string FxModelCopyPath;
        /// <summary>动画 / Timeline 复制的目录（点击"复制动画 Timeline"时由 outputFolder 写入）</summary>
        private string animationCopyPath;
        /// <summary>Image/Text 资源复制目录（分步=outputFolder，一键=ImageText 子目录）</summary>
        private string imageTextCopyPath;
        /// <summary>贴图复制的材质输入目录（分步=outputFolder，一键=输出根目录）</summary>
        private string textureMaterialSourcePath;
        /// <summary>材质贴图复制目录（分步=outputFolder，一键=Textures 子目录）</summary>
        private string texturesCopyPath;

        // ========== 一键复制分类子目录名 ==========
        private const string SubDirPrefabs = "Prefabs";
        private const string SubDirMaterials = "Materials";
        private const string SubDirFxMaterials = "FxMaterials";
        private const string SubDirMeshes = "Meshes";
        private const string SubDirAnimations = "Animations";
        private const string SubDirImageText = "ImageText";
        private const string SubDirTextures = "Textures";

        // ========== UI状态 ==========
        /// <summary>当前操作状态信息</summary>
        private string currentStatus = "等待开始";
        /// <summary>是否输出详细日志（Debug.Log）</summary>
        private bool enableVerboseLog = false;
        /// <summary>是否输出分步骤耗时统计</summary>
        private bool enableStepTimingLog = true;

        #endregion
    
            // ========== 菜单入口（已注释，通过ToolManagerWindow统一管理）==========
        // [MenuItem("BjTools/预制体复制工具")]
        // public static void ShowWindow()
        // {
        //     GetWindow<PrefabCopyTool>("PrefabCopyTool");
        // }
        // private void OnGUI()
        // {
        //     DrawGUI();
        // }

        #region GUI绘制
        
        /// <summary>
        /// 绘制工具的GUI界面
        /// 实现 IToolGUI 接口
        /// </summary>
        public void DrawGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Prefab复制工具", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);
            
            // 使用盒子区域组织UI
            EditorGUILayout.BeginVertical("box");
           
            EditorGUILayout.Space();
            DrawSectionSeparator("基础配置");
    
            rootPrefab = (GameObject)EditorGUILayout.ObjectField("当前处理预制体:", rootPrefab, typeof(GameObject), true);
            if (rootPrefab != null && IsGameObjectInScene(rootPrefab))
            {
                EditorGUILayout.HelpBox(
                    "当前为场景中的预制体实例。一键复制请从 Project 窗口拖入 Assets 中的预制体资源；分步操作（复制材质等）可使用场景实例。",
                    MessageType.Warning);
            }
            else if (rootPrefab != null)
            {
                EditorGUILayout.HelpBox("当前为 Assets 预制体资源，可直接使用一键复制。", MessageType.Info);
            }

            outputFolder = EditorGUILayout.ObjectField("输出文件夹：", outputFolder, typeof(DefaultAsset), false) as DefaultAsset;
            enableVerboseLog = EditorGUILayout.ToggleLeft("启用详细日志（可能影响执行速度）", enableVerboseLog);
            enableStepTimingLog = EditorGUILayout.ToggleLeft("启用分步骤耗时统计", enableStepTimingLog);
            if (GUILayout.Button("添加排除目录"))
            {
                excludeFolders.Add(null);
            }
            excludeScrollPosition = EditorGUILayout.BeginScrollView(excludeScrollPosition, GUILayout.Height(100));
           
            GUILayout.Label("添加排除目录 （以下文件夹中的资源不会被复制）:", EditorStyles.boldLabel);
            for (int i = 0; i < excludeFolders.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                excludeFolders[i] = (DefaultAsset)EditorGUILayout.ObjectField(
                    $"忽略目录 {i + 1}",
                    excludeFolders[i],
                    typeof(DefaultAsset),
                    false
                );
    
                if (GUILayout.Button("移除", GUILayout.Width(80)))
                {
                    excludeFolders.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            DrawSectionSeparator("一键操作");
            EditorGUILayout.HelpBox(
                "请先将 Project 中的预制体资源拖入「当前处理预制体」（不可使用场景实例）。\n" +
                "按顺序执行各复制步骤，并在输出文件夹下自动创建子目录：\n" +
                "Prefabs / Materials / FxMaterials / Meshes / ImageText / Textures / Animations\n" +
                "（分步与一键都会自动使用以上分类子目录）",
                MessageType.None);
            if (GUILayout.Button("一键复制所有", GUILayout.Height(30)))
            {
                TryExecuteCopyAllSteps();
            }
            if (GUILayout.Button("一键复制并保存", GUILayout.Height(30)))
            {
                TryExecuteCopyAllAndSaveSteps();
            }

            DrawSectionSeparator("分步复制");
           
            // 步骤1: 复制预制体
            //EditorGUILayout.LabelField("# 复制预制体", EditorStyles.boldLabel);
            
            if (GUILayout.Button("复制预制体"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                
                if (IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理Assets中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
    
                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                newFolderPath = GetStepOutputPath(SubDirPrefabs);
                AssetDatabase.Refresh();
    
                rootPrefab = ExecuteWithStepTiming("分步-复制预制体", () => CopyRootPrefab());
                //flagPrefab = true;
                
                currentStatus = "预制体复制完成";
            }
            
            EditorGUILayout.Space();
            //EditorGUILayout.LabelField("# 处理材质", EditorStyles.boldLabel);
            if (GUILayout.Button("复制材质"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                
                if (!IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理场景中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                
                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                materialsCopyPath = GetStepOutputPath(SubDirMaterials);
                ExecuteWithStepTiming("分步-复制材质", () => CopyMaterialsFromMeshRender(rootPrefab));
                //flagMaterial = true;
                currentStatus = "材质复制完成";
            }
    
            EditorGUILayout.Space();

            // 步骤3: 处理粒子材质
           
            
            if (GUILayout.Button("复制粒子材质"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定处理的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
               
                if (!IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理场景中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                
                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                FxMaterialsCopyPath = GetStepOutputPath(SubDirFxMaterials);
                ExecuteWithStepTiming("分步-复制粒子材质", () => CopyMaterialsFromParticle(rootPrefab));
                //flagMesh = true;
                currentStatus = "粒子材质复制完成";
            }
            EditorGUILayout.Space();
    
            // 步骤4: 处理粒子网格
           
            //EditorGUILayout.LabelField("# 处理粒子网格", EditorStyles.boldLabel);
            if (GUILayout.Button("复制粒子网格"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                
                if (!IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理场景中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                
                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                FxModelCopyPath = GetStepOutputPath(SubDirMeshes);
                ExecuteWithStepTiming("分步-复制粒子网格", () => CopyMeshFromParticle(rootPrefab));
                currentStatus = "粒子网格复制完成";
            }

            EditorGUILayout.Space();

            // 步骤5: 处理 Image/Text 资源（UI材质/贴图/字体）
            if (GUILayout.Button("复制Image/Text资源"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (!IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理场景中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                imageTextCopyPath = GetStepOutputPath(SubDirImageText);
                LogVerbose($"[ImageText资源] 点击按钮，开始处理: {rootPrefab.name}");
                currentStatus = ExecuteWithStepTiming("分步-复制Image/Text资源", () => CopyImageAndTextResources(rootPrefab));
                LogVerbose($"[ImageText资源] {currentStatus}");
            }

            EditorGUILayout.Space();

            // 步骤6: 复制材质贴图并重定向材质引用
            if (GUILayout.Button("复制材质贴图"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (!IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理场景中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                string outputPath = AssetDatabase.GetAssetPath(outputFolder);
                textureMaterialSourcePath = outputPath;
                texturesCopyPath = GetStepOutputPath(SubDirTextures);
                currentStatus = ExecuteWithStepTiming("分步-复制材质贴图",
                    () => CopyTexturesFromMaterials(textureMaterialSourcePath, texturesCopyPath));
            }

            EditorGUILayout.Space();

            // 步骤7: 处理 MeshFilter 网格
            if (GUILayout.Button("复制 MeshFilter 网格"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (!IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理场景中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                FxModelCopyPath = GetStepOutputPath(SubDirMeshes);
                ExecuteWithStepTiming("分步-复制 MeshFilter 网格", () => CopyMeshFromMeshFilter(rootPrefab));
                currentStatus = "MeshFilter 网格复制完成";
            }

            EditorGUILayout.Space();

            // 步骤8: 处理 Animator / PlayableDirector 动画与 Timeline
            if (GUILayout.Button("复制动画 Timeline"))
            {
                if (rootPrefab == null)
                {
                    EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (!IsGameObjectInScene(rootPrefab))
                {
                    EditorUtility.DisplayDialog("错误", "此操作只能处理场景中的预制体", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }

                if (outputFolder == null)
                {
                    EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                    EditorGUILayout.EndVertical();
                    return;
                }
                animationCopyPath = GetStepOutputPath(SubDirAnimations);
                ExecuteWithStepTiming("分步-复制动画 Timeline", () => CopyAnimationAndTimeline(rootPrefab));
                currentStatus = "动画 Timeline 复制完成";
            }

            EditorGUILayout.EndVertical();
    
            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical("box");
            DrawSectionSeparator("保存预制体");
            rootPrefabCopy = (GameObject)EditorGUILayout.ObjectField("复制后的预制体:", rootPrefabCopy, typeof(GameObject), false);
            
            if (GUILayout.Button("保存当前处理预制体"))
            {
                ExecuteWithStepTiming("分步-保存当前处理预制体", () => TrySaveCurrentPrefab());
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
    
            // 重置按钮
            // if (GUILayout.Button("重置工具"))
            // {
            //     ResetTool();
            // }
    
            EditorGUILayout.Space();
    
            // 显示当前状态
            EditorGUILayout.BeginVertical("box");
            DrawSectionSeparator("当前状态");
            EditorGUILayout.HelpBox(currentStatus, MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 绘制模块分隔线及标题
        /// </summary>
        private void DrawSectionSeparator(string title)
        {
            EditorGUILayout.Space(6);
            Rect lineRect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(lineRect, new Color(0.4f, 0.4f, 0.4f, 0.8f));
            EditorGUILayout.Space(4);
            if (!string.IsNullOrEmpty(title))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            }
        }

        /// <summary>
        /// 详细日志输出（可通过面板开关关闭）
        /// </summary>
        private void LogVerbose(string message)
        {
            if (enableVerboseLog)
            {
                Debug.Log(message);
            }
        }

        /// <summary>
        /// 执行步骤并记录耗时（Action）
        /// </summary>
        private void ExecuteWithStepTiming(string stepName, System.Action action)
        {
            if (action == null)
            {
                return;
            }

            if (!enableStepTimingLog)
            {
                action();
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                stopwatch.Stop();
                Debug.Log($"[耗时] {stepName}: {stopwatch.ElapsedMilliseconds} ms");
            }
        }

        /// <summary>
        /// 执行步骤并记录耗时（Func）
        /// </summary>
        private T ExecuteWithStepTiming<T>(string stepName, System.Func<T> action)
        {
            if (action == null)
            {
                return default(T);
            }

            if (!enableStepTimingLog)
            {
                return action();
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                stopwatch.Stop();
                Debug.Log($"[耗时] {stepName}: {stopwatch.ElapsedMilliseconds} ms");
            }
        }
        
        #endregion

        #region 一键复制

        /// <summary>
        /// 按 UI 面板顺序依次执行全部复制步骤，完成后自动保存预制体
        /// </summary>
        private void TryExecuteCopyAllAndSaveSteps()
        {
            if (!TryExecuteCopyAllSteps(showSuccessDialog: false))
            {
                return;
            }

            if (ExecuteWithStepTiming("一键-保存预制体", () => TrySaveCurrentPrefab(showSuccessDialog: false)))
            {
                currentStatus = "一键复制并保存完成";
                EditorUtility.DisplayDialog("", "一键复制并保存完成!", "确定");
            }
        }

        /// <summary>
        /// 保存当前场景中的预制体实例修改到 Assets
        /// </summary>
        private bool TrySaveCurrentPrefab(bool showSuccessDialog = true)
        {
            if (rootPrefab == null)
            {
                EditorUtility.DisplayDialog("错误", "预制体实例丢失！保存失败", "确定");
                return false;
            }

            if (!PrefabUtility.IsAnyPrefabInstanceRoot(rootPrefab))
            {
                EditorUtility.DisplayDialog("错误", "当前预制体已解包！保存失败", "确定");
                return false;
            }

            ApplyModificationsRecursively(rootPrefab.transform);
            currentStatus = "保存成功";
            if (showSuccessDialog)
            {
                EditorUtility.DisplayDialog("", "保存成功!", "确定");
            }

            return true;
        }

        /// <summary>
        /// 按 UI 面板顺序依次执行全部复制步骤
        /// 1. 复制预制体（源对象为 Assets 预制体时）
        /// 2. 复制材质 → 复制粒子材质 → 复制粒子网格 → 复制Image/Text资源 → 复制材质贴图 → 复制 MeshFilter 网格 → 复制动画 Timeline
        /// </summary>
        /// <param name="showSuccessDialog">是否在完成后弹出成功提示</param>
        /// <returns>是否全部执行成功</returns>
        private bool TryExecuteCopyAllSteps(bool showSuccessDialog = true)
        {
            if (rootPrefab == null)
            {
                EditorUtility.DisplayDialog("错误", "请指定复制的预制体", "确定");
                return false;
            }

            if (outputFolder == null)
            {
                EditorUtility.DisplayDialog("错误", "请设置输出文件夹", "确定");
                return false;
            }

            if (IsGameObjectInScene(rootPrefab))
            {
                EditorUtility.DisplayDialog("错误",
                    "一键复制请使用 Assets 中的预制体资源，不能拖入场景中的实例。\n请从 Project 窗口重新指定源预制体。",
                    "确定");
                return false;
            }

            string outputPath = AssetDatabase.GetAssetPath(outputFolder);
            SetupCategorizedOutputPaths(outputPath);
            var totalStopwatch = enableStepTimingLog ? System.Diagnostics.Stopwatch.StartNew() : null;

            try
            {
                AssetDatabase.Refresh();
                rootPrefab = ExecuteWithStepTiming("一键-复制预制体", () => CopyRootPrefab());
                if (rootPrefab == null)
                {
                    currentStatus = "一键复制失败：预制体复制未成功";
                    return false;
                }

                ExecuteWithStepTiming("一键-复制材质", () => CopyMaterialsFromMeshRender(rootPrefab));
                ExecuteWithStepTiming("一键-复制粒子材质", () => CopyMaterialsFromParticle(rootPrefab));
                ExecuteWithStepTiming("一键-复制粒子网格", () => CopyMeshFromParticle(rootPrefab));
                string imageTextStatus = ExecuteWithStepTiming("一键-复制Image/Text资源", () => CopyImageAndTextResources(rootPrefab));
                LogVerbose($"[一键复制] {imageTextStatus}");
                string textureStatus = ExecuteWithStepTiming("一键-复制材质贴图",
                    () => CopyTexturesFromMaterials(textureMaterialSourcePath, texturesCopyPath));
                LogVerbose($"[一键复制] {textureStatus}");
                ExecuteWithStepTiming("一键-复制 MeshFilter 网格", () => CopyMeshFromMeshFilter(rootPrefab));
                ExecuteWithStepTiming("一键-复制动画 Timeline", () => CopyAnimationAndTimeline(rootPrefab));

                currentStatus = "一键复制全部完成";
                if (showSuccessDialog)
                {
                    EditorUtility.DisplayDialog("", "一键复制全部完成!", "确定");
                }
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                currentStatus = "一键复制失败: " + ex.Message;
                EditorUtility.DisplayDialog("错误", "一键复制过程中发生错误，详见 Console", "确定");
                return false;
            }
            finally
            {
                if (totalStopwatch != null)
                {
                    totalStopwatch.Stop();
                    Debug.Log($"[耗时] 一键复制总耗时: {totalStopwatch.ElapsedMilliseconds} ms");
                }
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 一键复制：在输出根目录下创建分类子目录，并写入各模块输出路径
        /// </summary>
        private void SetupCategorizedOutputPaths(string rootOutputPath)
        {
            newFolderPath = EnsureAssetFolder(rootOutputPath, SubDirPrefabs);
            materialsCopyPath = EnsureAssetFolder(rootOutputPath, SubDirMaterials);
            FxMaterialsCopyPath = EnsureAssetFolder(rootOutputPath, SubDirFxMaterials);
            FxModelCopyPath = EnsureAssetFolder(rootOutputPath, SubDirMeshes);
            imageTextCopyPath = EnsureAssetFolder(rootOutputPath, SubDirImageText);
            texturesCopyPath = EnsureAssetFolder(rootOutputPath, SubDirTextures);
            textureMaterialSourcePath = rootOutputPath;
            animationCopyPath = EnsureAssetFolder(rootOutputPath, SubDirAnimations);

            LogVerbose($"[一键复制] 输出目录已分类：\n" +
                      $"  预制体 -> {newFolderPath}\n" +
                      $"  材质 -> {materialsCopyPath}\n" +
                      $"  粒子材质 -> {FxMaterialsCopyPath}\n" +
                      $"  网格 -> {FxModelCopyPath}\n" +
                      $"  Image/Text -> {imageTextCopyPath}\n" +
                      $"  贴图 -> {texturesCopyPath}\n" +
                      $"  动画/Timeline -> {animationCopyPath}\n" +
                      $"  贴图材质扫描目录 -> {textureMaterialSourcePath}");
        }

        /// <summary>
        /// 确保 Assets 目录存在，不存在则创建
        /// </summary>
        private string EnsureAssetFolder(string parentPath, string folderName)
        {
            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(folderName))
            {
                return parentPath;
            }

            string targetPath = parentPath + "/" + folderName;
            if (AssetDatabase.IsValidFolder(targetPath))
            {
                return targetPath;
            }

            AssetDatabase.CreateFolder(parentPath, folderName);
            AssetDatabase.Refresh();
            return targetPath;
        }

        /// <summary>
        /// 分步复制：基于输出根目录自动创建并返回对应分类子目录
        /// </summary>
        private string GetStepOutputPath(string subDirName)
        {
            string rootOutputPath = AssetDatabase.GetAssetPath(outputFolder);
            if (string.IsNullOrEmpty(rootOutputPath))
            {
                return rootOutputPath;
            }

            if (!AssetDatabase.IsValidFolder(rootOutputPath))
            {
                return rootOutputPath;
            }

            return EnsureAssetFolder(rootOutputPath, subDirName);
        }

        #endregion
        
        #region 预制体复制核心方法
        
        /// <summary>
        /// 复制根预制体
        /// 流程：
        /// 1. 调用 CopyChildPrefabs 递归复制预制体及其子预制体
        /// 2. 加载复制后的预制体资源
        /// 3. 在场景中实例化复制后的预制体
        /// </summary>
        /// <returns>场景中实例化的预制体副本</returns>
        private GameObject CopyRootPrefab()
        {
            rootPrefabCopyPath = CopyChildPrefabs(rootPrefab);
            if (!string.IsNullOrEmpty(rootPrefabCopyPath))
            {
                rootPrefabCopy = (GameObject)AssetDatabase.LoadAssetAtPath(rootPrefabCopyPath, typeof(GameObject));
            }
            AssetDatabase.Refresh();
            return (GameObject)PrefabUtility.InstantiatePrefab(rootPrefabCopy);
        }
        
        /// <summary>
        /// 递归复制子预制体
        /// 核心逻辑：
        /// 1. 遍历预制体的所有子物体，找出嵌套的子预制体
        /// 2. 对每个子预制体进行复制（如果是prefab则递归，否则直接复制）
        /// 3. 记录原GUID到新GUID的映射关系
        /// 4. 最后替换父预制体中的GUID引用，指向复制后的资源
        /// </summary>
        /// <param name="prefabRoot">需要处理的预制体根节点</param>
        /// <returns>复制后的预制体路径</returns>
        private string CopyChildPrefabs(GameObject prefabRoot)
        {
            // 存储原始GUID到复制后GUID的映射关系
            Dictionary<string, string> prefabUIDs = new Dictionary<string, string>();
            AssetDatabase.Refresh();

            List<string> excludePaths = BuildExcludePaths("预制体复制");

            EditorUtility.DisplayProgressBar("处理中", "正在复制子预制体...", 0.1f);
            string copyPath = "";
            try
            {
                foreach (Transform child in prefabRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (child.gameObject == prefabRoot.gameObject)
                        continue;
                    if (PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                    {
                        GameObject srcObj = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);
                        string path = AssetDatabase.GetAssetPath(srcObj);
                        if (IsAssetPathExcluded(path, excludePaths))
                        {
                            continue; // 跳过排除目录
                        }
                        if (srcObj == null)
                        {
                            Debug.LogWarning($"[预制体复制] 无法获取 '{child.name}' 的原始预制体");
                            continue;
                        }
    
                        LogVerbose($"[预制体复制] 正在处理子预制体: {srcObj.name}");
    
                        string id = GetAssetGuid(srcObj);
                        string childCopyId = "";
                        string childCopyPath = "";
    
                        if (prefabUIDs.ContainsKey(id))
                            continue;
    
                        if (Path.GetExtension(AssetDatabase.GetAssetPath(srcObj)) != ".prefab")//如果不是prefab类型，例如模型，则直接复制
                        {
                            childCopyPath = CopyAssets(srcObj, newFolderPath);
                        }
                        else
                        {
                            childCopyPath = CopyChildPrefabs(srcObj);
                        }
    
                        // string copyPath = CopyAssets(srcObj, newFolderPath);
    
                        if (string.IsNullOrEmpty(childCopyPath))
                            continue;
    
                        childCopyId = AssetDatabase.AssetPathToGUID(childCopyPath);
                        prefabUIDs.Add(id, childCopyId);
    
                        //copiedPrefabPath.Add(copyPath);
                    }
                }
                copyPath = ReplaceUID(prefabRoot, prefabUIDs);
                AssetDatabase.Refresh();
    
    
            }
            finally
            {
                // 确保进度条关闭
                EditorUtility.ClearProgressBar();
            }
            return copyPath;
        }
        
        #endregion
        
        #region 材质复制方法
        
        /// <summary>
        /// 从MeshRenderer和SkinnedMeshRenderer复制材质
        /// 遍历预制体实例中的所有网格渲染器，将其使用的材质复制到指定目录
        /// 并将渲染器的材质引用更新为复制后的材质
        /// </summary>
        /// <param name="instance">场景中的预制体实例</param>
        private void CopyMaterialsFromMeshRender(GameObject instance)
        {
            // 获取所有网格渲染器（包括普通网格和蒙皮网格）
            var meshRenderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            var skinnedMeshRenderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            HashSet<string> sourceMatIDs = new HashSet<string>();
            Dictionary<string, Material> copiedMatCache = new Dictionary<string, Material>();
            List<string> excludePaths = BuildExcludePaths("材质复制");
            
            EditorUtility.DisplayProgressBar("处理中", "正在复制材质...", 0.3f);
    
            try
            {
                CopyMaterialFromRenderer(meshRenderers, materialsCopyPath, sourceMatIDs, copiedMatCache, excludePaths, "材质复制");
                CopyMaterialFromRenderer(skinnedMeshRenderers, materialsCopyPath, sourceMatIDs, copiedMatCache, excludePaths, "材质复制");
                AssetDatabase.Refresh();
    
                //应用预制体中的修改
                //ApplyModificationsRecursively (instance.transform);
                //ApplyRootPrefab(rootPrefabCopy);
                //PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
    
                LogVerbose($"[材质复制] 预制体路径: {AssetDatabase.GetAssetPath(rootPrefabCopy)}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
        
        /// <summary>
        /// 从渲染器数组复制材质（基于通用核心方法）
        /// 处理逻辑统一委托给 CopyAndReplaceRendererMaterials → CopySingleMaterialIfNeeded
        /// </summary>
        /// <param name="renderers">渲染器数组（MeshRenderer或SkinnedMeshRenderer）</param>
        /// <param name="copyPath">材质复制的目标目录</param>
        /// <param name="sourceMatIDs">已复制材质GUID（跨调用共享）</param>
        /// <param name="excludePaths">排除目录路径</param>
        /// <param name="logTag">日志标签</param>
        private void CopyMaterialFromRenderer(Renderer[] renderers, string copyPath, HashSet<string> sourceMatIDs,
            Dictionary<string, Material> copiedMatCache, List<string> excludePaths, string logTag)
        {
            foreach (var render in renderers)
            {
                if (render == null) continue;

                CopyAndReplaceRendererMaterials(render, copyPath, sourceMatIDs, copiedMatCache, excludePaths, logTag);
            }
        }

        /// <summary>
        /// 复制 Image/Text 相关资源：Image材质与贴图、Text字体与字体材质
        /// </summary>
        private string CopyImageAndTextResources(GameObject instance)
        {
            if (instance == null)
            {
                Debug.LogError("[ImageText资源] 目标预制体实例为空，无法处理。");
                return "Image/Text资源复制失败：目标实例为空";
            }

            HashSet<string> sourceMatIDs = new HashSet<string>();
            Dictionary<string, Material> copiedMatCache = new Dictionary<string, Material>();
            List<string> excludePaths = BuildExcludePaths("ImageText资源");
            string targetCopyPath = !string.IsNullOrEmpty(imageTextCopyPath) ? imageTextCopyPath : materialsCopyPath;

            if (string.IsNullOrEmpty(targetCopyPath))
            {
                Debug.LogError("[ImageText资源] 输出目录为空，无法复制。");
                return "Image/Text资源复制失败：输出目录为空";
            }

            EditorUtility.DisplayProgressBar("处理中", "正在复制Image/Text资源...", 0.45f);
            try
            {
                bool hasTargets = CopyImageAndTextMaterialsAndAssets(instance, targetCopyPath, sourceMatIDs, copiedMatCache, excludePaths);
                if (!hasTargets)
                {
                    Debug.LogWarning($"[ImageText资源] 未在预制体中检测到 Image/Text 组件: {instance.name}");
                    return "Image/Text资源处理完成：未检测到Image或Text组件";
                }
                return "Image/Text资源复制完成";
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                return "Image/Text资源复制失败";
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Image/Text专项：复制UI材质、贴图、字体并替换引用
        /// 覆盖范围：
        /// 1. Image.material / Image.sprite / Image.overrideSprite
        /// 2. Text.material / Text.font（字体文件）及其字体材质
        /// </summary>
        private bool CopyImageAndTextMaterialsAndAssets(GameObject instance, string copyPath, HashSet<string> sourceMatIDs,
            Dictionary<string, Material> copiedMatCache, List<string> excludePaths)
        {
            if (instance == null) return false;

            var images = instance.GetComponentsInChildren<Image>(true);
            var texts = instance.GetComponentsInChildren<Text>(true);
            if (images.Length == 0 && texts.Length == 0)
            {
                return false;
            }

            Dictionary<string, string> sourceSpriteIDs = new Dictionary<string, string>();
            Dictionary<string, string> sourceFontIDs = new Dictionary<string, string>();
            
            foreach (var image in images)
            {
                if (image == null) continue;

                Sprite sourceImageSprite = image.sprite;
                Sprite sourceOverrideSprite = image.overrideSprite;

                Material copiedImageMaterial = CopySingleMaterialIfNeeded(
                    image.material, copyPath, sourceMatIDs, copiedMatCache, excludePaths, "Image材质");
                if (copiedImageMaterial != null && copiedImageMaterial != image.material)
                {
                    image.material = copiedImageMaterial;
                    EditorUtility.SetDirty(image);
                }

                Sprite copiedSprite = CopySpriteIfNeeded(
                    sourceImageSprite, copyPath, sourceSpriteIDs, excludePaths, "Image贴图");
                if (copiedSprite != null && copiedSprite != sourceImageSprite)
                {
                    image.sprite = copiedSprite;
                    EditorUtility.SetDirty(image);
                }

                if (sourceOverrideSprite != null && sourceOverrideSprite != sourceImageSprite)
                {
                    Sprite copiedOverrideSprite = CopySpriteIfNeeded(
                        sourceOverrideSprite, copyPath, sourceSpriteIDs, excludePaths, "Image贴图");
                    if (copiedOverrideSprite != null && copiedOverrideSprite != sourceOverrideSprite)
                    {
                        image.overrideSprite = copiedOverrideSprite;
                        EditorUtility.SetDirty(image);
                    }
                }
            }
            
            foreach (var text in texts)
            {
                if (text == null) continue;

                Material originalTextMaterial = text.material;
                Material copiedTextMaterial = CopySingleMaterialIfNeeded(
                    originalTextMaterial, copyPath, sourceMatIDs, copiedMatCache, excludePaths, "Text材质");
                if (copiedTextMaterial != null && copiedTextMaterial != originalTextMaterial)
                {
                    text.material = copiedTextMaterial;
                    EditorUtility.SetDirty(text);
                }

                Font sourceFont = text.font;
                Material sourceFontMaterial = sourceFont != null ? sourceFont.material : null;
                string copiedFontAssetPath;

                Font copiedFont = CopyFontIfNeeded(
                    sourceFont, copyPath, sourceFontIDs, excludePaths, "Text字体", out copiedFontAssetPath);
                if (copiedFont != null && copiedFont != sourceFont)
                {
                    text.font = copiedFont;
                    EditorUtility.SetDirty(text);
                }

                Material copiedFontMaterial = CopySingleMaterialIfNeeded(
                    copiedFont != null ? copiedFont.material : sourceFontMaterial,
                    copyPath,
                    sourceMatIDs,
                    copiedMatCache,
                    excludePaths,
                    "Text字体材质");

                RelinkCopiedFontMaterialReference(copiedFontAssetPath, sourceFontMaterial, copiedFontMaterial);

                if (copiedFontMaterial != null &&
                    (text.material == null || text.material == sourceFontMaterial))
                {
                    text.material = copiedFontMaterial;
                    EditorUtility.SetDirty(text);
                }
            }

            AssetDatabase.Refresh();
            LogVerbose($"[ImageText资源] Image 数量: {images.Length}, Text 数量: {texts.Length}");
            return true;
        }

        /// <summary>
        /// 从指定目录中的材质收集并复制贴图到目标目录，同时回写材质贴图引用
        /// </summary>
        private string CopyTexturesFromMaterials(string sourceMaterialsPath, string targetTexturesPath)
        {
            if (string.IsNullOrEmpty(sourceMaterialsPath) || !AssetDatabase.IsValidFolder(sourceMaterialsPath))
            {
                Debug.LogError($"[材质贴图] 无效的材质输入目录: {sourceMaterialsPath}");
                return "材质贴图复制失败：材质输入目录无效";
            }

            if (string.IsNullOrEmpty(targetTexturesPath) || !AssetDatabase.IsValidFolder(targetTexturesPath))
            {
                Debug.LogError($"[材质贴图] 无效的贴图输出目录: {targetTexturesPath}");
                return "材质贴图复制失败：贴图输出目录无效";
            }

            List<string> excludePaths = BuildExcludePaths("材质贴图");
            List<string> materialPaths = ResolveMaterialPathsForTextureCopy(sourceMaterialsPath);
            if (materialPaths.Count == 0)
            {
                return "材质贴图处理完成：未检测到材质资源";
            }

            Dictionary<string, string> textureGuidToCopiedPath = new Dictionary<string, string>();
            Dictionary<string, Texture> copiedTextureCache = new Dictionary<string, Texture>();
            List<TextureCopyTask> textureCopyTasks = new List<TextureCopyTask>();
            HashSet<string> occupiedTexturePaths = new HashSet<string>();
            Dictionary<string, string> occupiedPathToGuid = new Dictionary<string, string>();
            Dictionary<string, int> fileNameNextIndex = new Dictionary<string, int>();
            int copiedTextureCount = 0;
            int updatedMaterialCount = 0;
            int skippedTextureCount = 0;
            int failedTextureCount = 0;

            try
            {
                string targetTexturesFullPath = GetFullAssetPath(targetTexturesPath);
                if (Directory.Exists(targetTexturesFullPath))
                {
                    string[] existingFiles = Directory.GetFiles(targetTexturesFullPath, "*", SearchOption.TopDirectoryOnly);
                    foreach (string existingFile in existingFiles)
                    {
                        if (existingFile.EndsWith(".meta")) continue;
                        string fileName = Path.GetFileName(existingFile);
                        string assetPath = $"{targetTexturesPath}/{fileName}".Replace("\\", "/");
                        occupiedTexturePaths.Add(assetPath);

                        string existingGuid = AssetDatabase.AssetPathToGUID(assetPath);
                        if (!string.IsNullOrEmpty(existingGuid))
                        {
                            occupiedPathToGuid[assetPath] = existingGuid;
                        }
                    }
                }

                // 阶段1：扫描材质，收集需要复制的贴图任务与GUID映射
                for (int i = 0; i < materialPaths.Count; i++)
                {
                    string materialPath = materialPaths[i];
                    if (string.IsNullOrEmpty(materialPath) || IsAssetPathExcluded(materialPath, excludePaths))
                    {
                        continue;
                    }

                    Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (material == null)
                    {
                        continue;
                    }

                    if (i == 0 || i == materialPaths.Count - 1 || i % 10 == 0)
                    {
                        EditorUtility.DisplayProgressBar(
                            "处理中",
                            $"[阶段1/3] 分析材质贴图: {material.name}",
                            materialPaths.Count <= 1 ? 0f : (float)i / (materialPaths.Count - 1));
                    }

                    CollectTextureCopyTasksFromMaterial(
                        material,
                        targetTexturesPath,
                        excludePaths,
                        textureGuidToCopiedPath,
                        textureCopyTasks,
                        occupiedTexturePaths,
                        occupiedPathToGuid,
                        fileNameNextIndex,
                        ref skippedTextureCount,
                        ref failedTextureCount);
                }

                // 阶段2：批量复制贴图（只做复制，不做引用回写）
                if (textureCopyTasks.Count > 0)
                {
                    CopyTextureTasksInBatches(textureCopyTasks, ref copiedTextureCount, ref failedTextureCount);
                }

                AssetDatabase.Refresh();

                // 阶段3：回写材质贴图引用
                for (int i = 0; i < materialPaths.Count; i++)
                {
                    string materialPath = materialPaths[i];
                    if (string.IsNullOrEmpty(materialPath) || IsAssetPathExcluded(materialPath, excludePaths))
                    {
                        continue;
                    }

                    Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (material == null)
                    {
                        continue;
                    }

                    if (i == 0 || i == materialPaths.Count - 1 || i % 10 == 0)
                    {
                        EditorUtility.DisplayProgressBar(
                            "处理中",
                            $"[阶段3/3] 回写材质贴图: {material.name}",
                            materialPaths.Count <= 1 ? 1f : (float)i / (materialPaths.Count - 1));
                    }

                    bool materialUpdated = RelinkMaterialTexturesWithCopiedMap(
                        material,
                        targetTexturesPath,
                        excludePaths,
                        textureGuidToCopiedPath,
                        copiedTextureCache,
                        ref failedTextureCount);
                    if (materialUpdated)
                    {
                        updatedMaterialCount++;
                    }
                }
            }
            finally
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.ClearProgressBar();
            }

            string status = $"材质贴图处理完成：更新材质 {updatedMaterialCount} 个，复制贴图 {copiedTextureCount} 张，跳过 {skippedTextureCount} 张，失败 {failedTextureCount} 张";
            LogVerbose($"[材质贴图] 输入目录: {sourceMaterialsPath}，输出目录: {targetTexturesPath}");
            LogVerbose($"[材质贴图] {status}");
            return status;
        }

        /// <summary>
        /// 收集单个材质中的贴图复制任务（阶段1）
        /// </summary>
        private void CollectTextureCopyTasksFromMaterial(
            Material material,
            string targetTexturesPath,
            List<string> excludePaths,
            Dictionary<string, string> textureGuidToCopiedPath,
            List<TextureCopyTask> textureCopyTasks,
            HashSet<string> occupiedTexturePaths,
            Dictionary<string, string> occupiedPathToGuid,
            Dictionary<string, int> fileNameNextIndex,
            ref int skippedTextureCount,
            ref int failedTextureCount)
        {
            if (material == null)
            {
                return;
            }

            SerializedObject serializedMaterial = new SerializedObject(material);
            SerializedProperty texEnvs = serializedMaterial.FindProperty("m_SavedProperties.m_TexEnvs");
            if (texEnvs == null || texEnvs.arraySize == 0)
            {
                return;
            }

            for (int i = 0; i < texEnvs.arraySize; i++)
            {
                SerializedProperty textureProperty = texEnvs.GetArrayElementAtIndex(i);
                SerializedProperty textureValue = textureProperty.FindPropertyRelative("second.m_Texture");
                if (textureValue == null || textureValue.objectReferenceValue == null)
                {
                    continue;
                }

                Texture sourceTexture = textureValue.objectReferenceValue as Texture;
                if (sourceTexture == null)
                {
                    continue;
                }

                string sourceTexturePath = AssetDatabase.GetAssetPath(sourceTexture);
                if (string.IsNullOrEmpty(sourceTexturePath) || !sourceTexturePath.StartsWith("Assets/"))
                {
                    skippedTextureCount++;
                    continue;
                }

                if (IsAssetPathExcluded(sourceTexturePath, excludePaths))
                {
                    skippedTextureCount++;
                    continue;
                }

                if (sourceTexturePath.StartsWith(targetTexturesPath + "/"))
                {
                    skippedTextureCount++;
                    continue;
                }

                string sourceTextureGuid = AssetDatabase.AssetPathToGUID(sourceTexturePath);
                if (string.IsNullOrEmpty(sourceTextureGuid))
                {
                    skippedTextureCount++;
                    continue;
                }

                if (!textureGuidToCopiedPath.TryGetValue(sourceTextureGuid, out string copiedTexturePath))
                {
                    bool needCopy;
                    copiedTexturePath = BuildUniqueTextureCopyPath(
                        sourceTexturePath,
                        targetTexturesPath,
                        sourceTextureGuid,
                        occupiedTexturePaths,
                        occupiedPathToGuid,
                        fileNameNextIndex,
                        out needCopy);
                    if (string.IsNullOrEmpty(copiedTexturePath))
                    {
                        failedTextureCount++;
                        continue;
                    }

                    textureGuidToCopiedPath[sourceTextureGuid] = copiedTexturePath;
                    if (needCopy)
                    {
                        textureCopyTasks.Add(new TextureCopyTask
                        {
                            SourcePath = sourceTexturePath,
                            TargetPath = copiedTexturePath
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 根据已复制贴图映射回写材质引用（阶段3）
        /// </summary>
        private bool RelinkMaterialTexturesWithCopiedMap(
            Material material,
            string targetTexturesPath,
            List<string> excludePaths,
            Dictionary<string, string> textureGuidToCopiedPath,
            Dictionary<string, Texture> copiedTextureCache,
            ref int failedTextureCount)
        {
            if (material == null)
            {
                return false;
            }

            bool materialModified = false;
            SerializedObject serializedMaterial = new SerializedObject(material);
            SerializedProperty texEnvs = serializedMaterial.FindProperty("m_SavedProperties.m_TexEnvs");
            if (texEnvs == null || texEnvs.arraySize == 0)
            {
                return false;
            }

            for (int i = 0; i < texEnvs.arraySize; i++)
            {
                SerializedProperty textureProperty = texEnvs.GetArrayElementAtIndex(i);
                SerializedProperty textureValue = textureProperty.FindPropertyRelative("second.m_Texture");
                if (textureValue == null || textureValue.objectReferenceValue == null)
                {
                    continue;
                }

                Texture sourceTexture = textureValue.objectReferenceValue as Texture;
                if (sourceTexture == null)
                {
                    continue;
                }

                string sourceTexturePath = AssetDatabase.GetAssetPath(sourceTexture);
                if (string.IsNullOrEmpty(sourceTexturePath) || !sourceTexturePath.StartsWith("Assets/"))
                {
                    continue;
                }

                if (IsAssetPathExcluded(sourceTexturePath, excludePaths))
                {
                    continue;
                }

                if (sourceTexturePath.StartsWith(targetTexturesPath + "/"))
                {
                    continue;
                }

                string sourceTextureGuid = AssetDatabase.AssetPathToGUID(sourceTexturePath);
                if (string.IsNullOrEmpty(sourceTextureGuid))
                {
                    continue;
                }

                if (!textureGuidToCopiedPath.TryGetValue(sourceTextureGuid, out string copiedTexturePath))
                {
                    continue;
                }

                if (!copiedTextureCache.TryGetValue(copiedTexturePath, out Texture copiedTexture) || copiedTexture == null)
                {
                    copiedTexture = AssetDatabase.LoadAssetAtPath<Texture>(copiedTexturePath);
                    if (copiedTexture == null)
                    {
                        AssetDatabase.ImportAsset(copiedTexturePath, ImportAssetOptions.ForceSynchronousImport);
                        copiedTexture = AssetDatabase.LoadAssetAtPath<Texture>(copiedTexturePath);
                    }

                    copiedTextureCache[copiedTexturePath] = copiedTexture;
                }

                if (copiedTexture == null)
                {
                    failedTextureCount++;
                    continue;
                }

                if (textureValue.objectReferenceValue != copiedTexture)
                {
                    textureValue.objectReferenceValue = copiedTexture;
                    materialModified = true;
                }
            }

            if (materialModified)
            {
                serializedMaterial.ApplyModifiedProperties();
                EditorUtility.SetDirty(material);
            }

            return materialModified;
        }

        /// <summary>
        /// 为贴图生成可用的目标复制路径；同名不同资源会自动追加序号
        /// </summary>
        private string BuildUniqueTextureCopyPath(
            string sourceTexturePath,
            string targetTexturesPath,
            string sourceTextureGuid,
            HashSet<string> occupiedTexturePaths,
            Dictionary<string, string> occupiedPathToGuid,
            Dictionary<string, int> fileNameNextIndex,
            out bool needCopy)
        {
            needCopy = false;
            if (string.IsNullOrEmpty(sourceTexturePath) || string.IsNullOrEmpty(targetTexturesPath))
            {
                return null;
            }

            string fileName = Path.GetFileName(sourceTexturePath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            string candidatePath = $"{targetTexturesPath}/{fileName}";
            string fileKey = fileNameWithoutExtension + extension;
            if (!fileNameNextIndex.TryGetValue(fileKey, out int index))
            {
                index = 1;
            }

            while (true)
            {
                if (!occupiedTexturePaths.Contains(candidatePath))
                {
                    occupiedTexturePaths.Add(candidatePath);
                    occupiedPathToGuid[candidatePath] = sourceTextureGuid;
                    needCopy = true;
                    fileNameNextIndex[fileKey] = index;
                    return candidatePath;
                }

                if (occupiedPathToGuid.TryGetValue(candidatePath, out string existingGuid) &&
                    !string.IsNullOrEmpty(existingGuid) &&
                    existingGuid == sourceTextureGuid)
                {
                    needCopy = !File.Exists(GetFullAssetPath(candidatePath));
                    return candidatePath;
                }

                candidatePath = $"{targetTexturesPath}/{fileNameWithoutExtension}_{index}{extension}";
                index++;
            }
        }

        /// <summary>
        /// 批量复制贴图任务（阶段2），分批使用 Start/StopAssetEditing 降低导入开销
        /// </summary>
        private void CopyTextureTasksInBatches(
            List<TextureCopyTask> textureCopyTasks,
            ref int copiedTextureCount,
            ref int failedTextureCount)
        {
            if (textureCopyTasks == null || textureCopyTasks.Count == 0)
            {
                return;
            }

            const int batchSize = 200;
            int totalCount = textureCopyTasks.Count;

            for (int batchStart = 0; batchStart < totalCount; batchStart += batchSize)
            {
                int batchEnd = Mathf.Min(batchStart + batchSize, totalCount);
                bool startedAssetEditing = false;

                try
                {
                    AssetDatabase.StartAssetEditing();
                    startedAssetEditing = true;

                    for (int i = batchStart; i < batchEnd; i++)
                    {
                        TextureCopyTask task = textureCopyTasks[i];
                        if (i == 0 || i == totalCount - 1 || i % 20 == 0)
                        {
                            EditorUtility.DisplayProgressBar(
                                "处理中",
                                $"[阶段2/3] 批量复制贴图: {Path.GetFileName(task.SourcePath)}",
                                totalCount <= 1 ? 0.5f : (float)i / (totalCount - 1));
                        }

                        bool copyResult = AssetDatabase.CopyAsset(task.SourcePath, task.TargetPath);
                        if (copyResult)
                        {
                            copiedTextureCount++;
                            LogVerbose($"[材质贴图] 已复制贴图: {task.SourcePath} -> {task.TargetPath}");
                        }
                        else
                        {
                            failedTextureCount++;
                            Debug.LogError($"[材质贴图] 复制失败: {task.SourcePath} -> {task.TargetPath}");
                        }
                    }
                }
                finally
                {
                    if (startedAssetEditing)
                    {
                        AssetDatabase.StopAssetEditing();
                    }
                }
            }
        }

        /// <summary>
        /// 解析材质扫描范围：
        /// 优先扫描分类目录（Materials/FxMaterials/ImageText）；
        /// 若检测到分类目录外存在材质，则回退为扫描根目录，确保不漏材质。
        /// </summary>
        private List<string> ResolveMaterialPathsForTextureCopy(string sourceMaterialsPath)
        {
            HashSet<string> materialPaths = new HashSet<string>();
            List<string> preferredFolders = new List<string>();
            string[] preferredSubDirs = { SubDirMaterials, SubDirFxMaterials, SubDirImageText };
            foreach (string subDir in preferredSubDirs)
            {
                string folderPath = $"{sourceMaterialsPath}/{subDir}";
                if (AssetDatabase.IsValidFolder(folderPath))
                {
                    preferredFolders.Add(folderPath);
                }
            }

            bool canUsePreferredOnly = preferredFolders.Count > 0 &&
                                       AreAllMaterialsInsidePreferredFolders(sourceMaterialsPath, preferredFolders);
            if (canUsePreferredOnly)
            {
                foreach (string folderPath in preferredFolders)
                {
                    string[] guids = AssetDatabase.FindAssets("t:Material", new[] { folderPath });
                    foreach (string guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(path))
                        {
                            materialPaths.Add(path);
                        }
                    }
                }
            }
            else
            {
                string[] guids = AssetDatabase.FindAssets("t:Material", new[] { sourceMaterialsPath });
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        materialPaths.Add(path);
                    }
                }
            }

            return materialPaths.ToList();
        }

        /// <summary>
        /// 判断 sourceMaterialsPath 下所有材质是否都位于指定优先目录内
        /// </summary>
        private bool AreAllMaterialsInsidePreferredFolders(string sourceMaterialsPath, List<string> preferredFolders)
        {
            if (string.IsNullOrEmpty(sourceMaterialsPath) || preferredFolders == null || preferredFolders.Count == 0)
            {
                return false;
            }

            string sourceFullPath = GetFullAssetPath(sourceMaterialsPath).Replace("\\", "/").TrimEnd('/');
            if (!Directory.Exists(sourceFullPath))
            {
                return false;
            }

            string[] materialFiles = Directory.GetFiles(sourceFullPath, "*.mat", SearchOption.AllDirectories);
            foreach (string filePath in materialFiles)
            {
                string normalizedFilePath = filePath.Replace("\\", "/");
                if (!normalizedFilePath.StartsWith(sourceFullPath + "/"))
                {
                    continue;
                }

                string relativePath = normalizedFilePath.Substring(sourceFullPath.Length).TrimStart('/');
                string assetPath = $"{sourceMaterialsPath}/{relativePath}".Replace("\\", "/");
                if (!IsPathInsideAnyFolder(assetPath, preferredFolders))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 判断路径是否位于任意目录中（严格目录边界）
        /// </summary>
        private bool IsPathInsideAnyFolder(string assetPath, List<string> folders)
        {
            if (string.IsNullOrEmpty(assetPath) || folders == null)
            {
                return false;
            }

            foreach (string folder in folders)
            {
                if (IsPathInFolderOrSubFolder(assetPath, folder))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 贴图复制任务（阶段2）
        /// </summary>
        private class TextureCopyTask
        {
            public string SourcePath;
            public string TargetPath;
        }
        
        /// <summary>
        /// 从粒子相关Renderer复制材质（统一输出到 FxMaterialsCopyPath）
        /// 处理内容：
        /// 1. ParticleSystemRenderer 的主材质（sharedMaterial）
        /// 2. 粒子系统的拖尾材质（trails 模块的 trailMaterial）
        /// 3. TrailRenderer 组件的材质（独立的拖尾组件）
        /// 
        /// 注意：MeshRenderer / SkinnedMeshRenderer 的材质统一由 "复制材质" 按钮
        ///      （CopyMaterialsFromMeshRender）处理，本方法不再涵盖，避免重复复制。
        /// 
        /// 所有Renderer共用一份 sourceMatIDs，确保跨类型共享的材质只被复制一次
        /// </summary>
        /// <param name="instance">场景中的预制体实例</param>
        private void CopyMaterialsFromParticle(GameObject instance)
        {
            // 跨所有 Renderer 共享 GUID 去重表，确保同一材质只复制一次
            HashSet<string> sourceMatIDs = new HashSet<string>();
            Dictionary<string, Material> copiedMatCache = new Dictionary<string, Material>();
            List<string> excludePaths = BuildExcludePaths("粒子材质");

            EditorUtility.DisplayProgressBar("处理中", "正在复制粒子材质...", 0.6f);

            try
            {
                // ===== 1. 处理粒子系统主材质 与 内置拖尾(trails)材质 =====
                var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
                foreach (ParticleSystem particle in particles)
                {
                    ParticleSystemRenderer renderer = particle.GetComponent<ParticleSystemRenderer>();
                    if (renderer == null) continue;

                    // 主材质（仅在渲染模式不为 None 时处理）
                    if (renderer.renderMode != ParticleSystemRenderMode.None)
                    {
                        renderer.sharedMaterial = CopySingleMaterialIfNeeded(
                            renderer.sharedMaterial, FxMaterialsCopyPath, sourceMatIDs, copiedMatCache, excludePaths, "粒子材质");
                    }

                    // 粒子系统内置拖尾(trails 模块)材质
                    if (particle.trails.enabled)
                    {
                        renderer.trailMaterial = CopySingleMaterialIfNeeded(
                            renderer.trailMaterial, FxMaterialsCopyPath, sourceMatIDs, copiedMatCache, excludePaths, "粒子材质");
                    }

                }

                // ===== 2. 处理独立 TrailRenderer 组件的材质 =====
                // 注意：这里是独立 TrailRenderer 组件，与上面 ParticleSystem.trails 内置拖尾不同
                var trailRenderers = instance.GetComponentsInChildren<TrailRenderer>(true);
                LogVerbose($"[粒子材质] 检测到 TrailRenderer 数量: {trailRenderers.Length}");
                foreach (TrailRenderer trailRenderer in trailRenderers)
                {
                    CopyAndReplaceRendererMaterials(trailRenderer, FxMaterialsCopyPath, sourceMatIDs, copiedMatCache, excludePaths, "Trail材质");
                }

                AssetDatabase.Refresh();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 通用：复制单个材质到目标目录，并返回副本引用（核心材质复制逻辑）
        /// 处理流程：
        /// 1. 跳过空材质、非 .mat 的内置材质 → 原样返回
        /// 2. 命中排除目录 → 原样返回
        /// 3. 已复制过（命中 GUID 去重表）→ 加载并返回已存在的副本
        /// 4. 否则复制材质到目标目录，记录 GUID，返回副本
        /// 
        /// 适用于：MeshRenderer.sharedMaterials / ParticleSystemRenderer.sharedMaterial
        ///        / ParticleSystemRenderer.trailMaterial / TrailRenderer.sharedMaterials 等任意材质字段
        /// </summary>
        /// <param name="sourceMat">源材质</param>
        /// <param name="copyPath">复制目标目录</param>
        /// <param name="sourceMatIDs">已复制材质的 GUID 列表（外部共享，跨调用去重）</param>
        /// <param name="excludePaths">排除目录路径列表</param>
        /// <param name="logTag">日志标签，便于区分调用来源</param>
        /// <returns>替换后的材质引用（可能是副本，也可能是原材质）</returns>
        private Material CopySingleMaterialIfNeeded(Material sourceMat, string copyPath, HashSet<string> sourceMatIDs,
            Dictionary<string, Material> copiedMatCache, List<string> excludePaths, string logTag)
        {
            if (sourceMat == null) return null;

            string path = AssetDatabase.GetAssetPath(sourceMat);

            // 仅处理外部 .mat 材质，内置材质保持原引用
            if (Path.GetExtension(path) != ".mat")
            {
                return sourceMat;
            }

            // 已在目标目录中的材质不再继续复制，避免出现 *_Copy_Copy
            if (!string.IsNullOrEmpty(copyPath) && path.StartsWith(copyPath + "/"))
            {
                return sourceMat;
            }

            // 排除目录检查
            if (IsAssetPathExcluded(path, excludePaths))
            {
                return sourceMat;
            }

            string id = AssetDatabase.AssetPathToGUID(path);
            string copyFilePath;
            if (!string.IsNullOrEmpty(id) && copiedMatCache.TryGetValue(id, out Material cachedMat) && cachedMat != null)
            {
                return cachedMat;
            }

            if (sourceMatIDs.Contains(id))
            {
                // 已经复制过该材质，直接复用副本
                copyFilePath = GenerateCopyPath(sourceMat, copyPath);
                if (!string.IsNullOrEmpty(copyFilePath))
                {
                    Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                    if (copiedMat != null && !string.IsNullOrEmpty(id))
                    {
                        copiedMatCache[id] = copiedMat;
                    }
                    return copiedMat != null ? copiedMat : sourceMat;
                }
                return sourceMat;
            }

            sourceMatIDs.Add(id);
            copyFilePath = CopyAssets(sourceMat, copyPath);

            if (!string.IsNullOrEmpty(copyFilePath))
            {
                Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                if (copiedMat != null && !string.IsNullOrEmpty(id))
                {
                    copiedMatCache[id] = copiedMat;
                }
                LogVerbose($"[{logTag}] 已复制材质: {sourceMat.name} -> {copyFilePath}");
                return copiedMat != null ? copiedMat : sourceMat;
            }

            return sourceMat;
        }

        /// <summary>
        /// 通用：处理 Renderer 的 sharedMaterials 数组，逐槽复制材质到目标目录并更新引用
        /// 适用于：MeshRenderer / SkinnedMeshRenderer / TrailRenderer 等带 sharedMaterials 数组的 Renderer
        /// （ParticleSystemRenderer 的主材质和拖尾材质字段独立，建议直接使用 CopySingleMaterialIfNeeded）
        /// </summary>
        /// <param name="renderer">需要处理的 Renderer 组件</param>
        /// <param name="copyPath">复制目标目录</param>
        /// <param name="sourceMatIDs">已复制材质的 GUID 列表（外部共享，跨调用去重）</param>
        /// <param name="excludePaths">排除目录路径列表</param>
        /// <param name="logTag">日志标签</param>
        private void CopyAndReplaceRendererMaterials(Renderer renderer, string copyPath, HashSet<string> sourceMatIDs,
            Dictionary<string, Material> copiedMatCache, List<string> excludePaths, string logTag)
        {
            if (renderer == null) return;

            Material[] sharedMats = renderer.sharedMaterials;
            if (sharedMats == null || sharedMats.Length == 0) return;

            Material[] updateList = new Material[sharedMats.Length];

            for (int i = 0; i < sharedMats.Length; i++)
            {
                updateList[i] = CopySingleMaterialIfNeeded(sharedMats[i], copyPath, sourceMatIDs, copiedMatCache, excludePaths, logTag);
            }

            renderer.sharedMaterials = updateList;
            LogVerbose($"[{logTag}] 已处理 Renderer: {renderer.name}, 材质槽数: {sharedMats.Length}");
        }

        /// <summary>
        /// 复制字体资源并返回副本引用
        /// </summary>
        private Font CopyFontIfNeeded(Font sourceFont, string copyPath, Dictionary<string, string> sourceFontIDs, List<string> excludePaths, string logTag, out string copiedFontPath)
        {
            copiedFontPath = null;
            if (sourceFont == null) return null;

            string copiedPath = CopyAssetPathIfNeeded(
                sourceFont, copyPath, sourceFontIDs, excludePaths, logTag);
            if (string.IsNullOrEmpty(copiedPath))
            {
                return sourceFont;
            }

            copiedFontPath = copiedPath;
            Font copiedFont = AssetDatabase.LoadAssetAtPath<Font>(copiedPath);
            return copiedFont != null ? copiedFont : sourceFont;
        }

        /// <summary>
        /// 将复制后的字体资源中的材质引用从旧材质 GUID 重定向到新材质 GUID
        /// 仅处理可文本替换的 .fontsettings 资源
        /// </summary>
        private void RelinkCopiedFontMaterialReference(string copiedFontPath, Material sourceFontMaterial, Material copiedFontMaterial)
        {
            if (string.IsNullOrEmpty(copiedFontPath) || sourceFontMaterial == null || copiedFontMaterial == null)
            {
                return;
            }

            string sourceMatPath = AssetDatabase.GetAssetPath(sourceFontMaterial);
            string copiedMatPath = AssetDatabase.GetAssetPath(copiedFontMaterial);
            if (string.IsNullOrEmpty(sourceMatPath) || string.IsNullOrEmpty(copiedMatPath) || sourceMatPath == copiedMatPath)
            {
                return;
            }

            // ttf/otf 为二进制文件，不做文本 GUID 替换
            string fontExtension = Path.GetExtension(copiedFontPath).ToLowerInvariant();
            if (fontExtension != ".fontsettings" && fontExtension != ".asset")
            {
                return;
            }

            string oldMatGuid = AssetDatabase.AssetPathToGUID(sourceMatPath);
            string newMatGuid = AssetDatabase.AssetPathToGUID(copiedMatPath);
            if (string.IsNullOrEmpty(oldMatGuid) || string.IsNullOrEmpty(newMatGuid) || oldMatGuid == newMatGuid)
            {
                return;
            }

            string copiedFontFullPath = GetFullAssetPath(copiedFontPath);
            if (!File.Exists(copiedFontFullPath))
            {
                return;
            }

            string content = File.ReadAllText(copiedFontFullPath);
            if (!content.Contains(oldMatGuid))
            {
                return;
            }

            content = content.Replace(oldMatGuid, newMatGuid);
            File.WriteAllText(copiedFontFullPath, content);
            AssetDatabase.ImportAsset(copiedFontPath, ImportAssetOptions.ForceUpdate);
            LogVerbose($"[Text字体材质] 已重定向字体材质引用: {copiedFontPath}");
        }

        /// <summary>
        /// 复制Sprite资源并返回副本Sprite（支持多Sprite图集按名称回查）
        /// </summary>
        private Sprite CopySpriteIfNeeded(Sprite sourceSprite, string copyPath, Dictionary<string, string> sourceSpriteIDs, List<string> excludePaths, string logTag)
        {
            if (sourceSprite == null) return null;

            string copiedPath = CopyAssetPathIfNeeded(
                sourceSprite, copyPath, sourceSpriteIDs, excludePaths, logTag);
            if (string.IsNullOrEmpty(copiedPath))
            {
                return sourceSprite;
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(copiedPath);
            foreach (Object subAsset in subAssets)
            {
                if (subAsset is Sprite copiedSprite && copiedSprite.name == sourceSprite.name)
                {
                    return copiedSprite;
                }
            }

            Sprite fallback = AssetDatabase.LoadAssetAtPath<Sprite>(copiedPath);
            return fallback != null ? fallback : sourceSprite;
        }

        /// <summary>
        /// 通用：按GUID去重复制资源，返回副本路径；若无需复制返回null
        /// </summary>
        private string CopyAssetPathIfNeeded(Object sourceAsset, string copyPath, Dictionary<string, string> sourceIDs, List<string> excludePaths, string logTag)
        {
            if (sourceAsset == null) return null;

            string sourcePath = AssetDatabase.GetAssetPath(sourceAsset);
            if (string.IsNullOrEmpty(sourcePath))
            {
                return null;
            }

            // 已在目标目录中的资源不再继续复制，避免出现 *_Copy_Copy
            if (!string.IsNullOrEmpty(copyPath) && sourcePath.StartsWith(copyPath + "/"))
            {
                return sourcePath;
            }

            if (IsAssetPathExcluded(sourcePath, excludePaths))
            {
                return null;
            }

            string sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            if (string.IsNullOrEmpty(sourceGuid))
            {
                return null;
            }

            if (sourceIDs.TryGetValue(sourceGuid, out string copiedPath))
            {
                return copiedPath;
            }

            string newCopiedPath = CopyAssets(sourceAsset, copyPath);
            if (string.IsNullOrEmpty(newCopiedPath))
            {
                return null;
            }

            sourceIDs[sourceGuid] = newCopiedPath;
            LogVerbose($"[{logTag}] 已复制资源: {sourcePath} -> {newCopiedPath}");
            return newCopiedPath;
        }
        
        #endregion
        
        #region 粒子网格复制方法
        
        /// <summary>
        /// 从粒子系统复制网格资源
        /// 仅处理渲染模式为Mesh的粒子系统
        /// 复制网格资源并更新粒子渲染器的网格引用
        /// </summary>
        /// <param name="instance">场景中的预制体实例</param>
        private void CopyMeshFromParticle(GameObject instance)
        {
            // 获取所有粒子系统组件
            var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
            // 记录已复制的网格GUID，避免重复复制
            HashSet<string> sourceMeshIDs = new HashSet<string>();
            List<string> excludePaths = BuildExcludePaths("粒子网格");

            string copyFilePath = "";
            EditorUtility.DisplayProgressBar("处理中", "正在复制粒子网格...", 0.9f);
    
            try
            {
                foreach (ParticleSystem particle in particles)
                {
                    string meshPath = FindMeshInParticleSystem(particle);
    
                    if (string.IsNullOrEmpty(meshPath))
                        continue;
                    if (IsAssetPathExcluded(meshPath, excludePaths))
                    {
                        continue; // 跳过排除目录中的网格
                    }
    
                    string id = AssetDatabase.AssetPathToGUID(meshPath);
    
                    if (sourceMeshIDs.Contains(id))
                    {
                        copyFilePath = GenerateCopyPath(AssetDatabase.LoadAssetAtPath(meshPath, typeof(Object)), FxModelCopyPath);
                        ReplaceParticleMesh(particle, copyFilePath);
                        
    
                    }   
                    else if (meshPath != null && id != null)
                    {
                        copyFilePath = CopyAssets(AssetDatabase.LoadAssetAtPath(meshPath, typeof(Object)), FxModelCopyPath);
    
                        if (!string.IsNullOrEmpty(copyFilePath))
                        {
                            string copyId = AssetDatabase.AssetPathToGUID(copyFilePath);
                            sourceMeshIDs.Add(id);
                            ReplaceParticleMesh(particle, copyFilePath);
                        }
                       
    
                    }
                  
                }
                
                AssetDatabase.Refresh();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 从 MeshFilter 复制 mesh 资源（与 CopyMeshFromParticle 行为对齐）
        /// 处理对象：
        ///   - 根预制体自身散落节点的 MeshFilter
        ///   - 嵌套普通 prefab 实例内部节点的 MeshFilter（保存时 ApplyPrefabInstance 会写回子 prefab）
        ///   支持 mesh 类型：独立 .mesh / .asset 资源 与 模型文件(.fbx 等)中的子 mesh
        /// 
        /// 安全跳过：嵌套模型类型预制体（PrefabAssetType.Model）实例的内部节点
        ///   原因：mesh 引用已在"复制预制体"阶段通过整体复制 + GUID 替换正确指向新 .fbx
        ///        若再处理会被替换为另一个独立副本，破坏模型整体复制的引用链
        /// 
        /// 模型文件 mesh 复制逻辑参考 ReplaceParticleMesh：
        ///   - 副本是独立 mesh 资源 → 直接加载赋值
        ///   - 副本是模型文件 → 按原 mesh 名称匹配模型内对应子 mesh
        /// </summary>
        /// <param name="instance">场景中的预制体实例（根预制体）</param>
        private void CopyMeshFromMeshFilter(GameObject instance)
        {
            var meshFilters = instance.GetComponentsInChildren<MeshFilter>(true);
            HashSet<string> sourceMeshIDs = new HashSet<string>();
            List<string> excludePaths = BuildExcludePaths("MeshFilter网格");

            EditorUtility.DisplayProgressBar("处理中", "正在复制 MeshFilter 网格...", 0.5f);

            int skippedModelPrefab = 0;

            try
            {
                foreach (MeshFilter filter in meshFilters)
                {
                    if (filter == null || filter.sharedMesh == null) continue;

                    // 关键安全过滤：仅跳过嵌套模型预制体内部节点（普通子 prefab 内部节点正常处理）
                    if (IsInsideModelPrefabInstance(filter.gameObject, instance))
                    {
                        skippedModelPrefab++;
                        continue;
                    }

                    string meshPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                    if (string.IsNullOrEmpty(meshPath)) continue; // 运行时生成的 mesh

                    // 排除目录检查
                    if (IsAssetPathExcluded(meshPath, excludePaths))
                    {
                        continue;
                    }

                    string id = AssetDatabase.AssetPathToGUID(meshPath);
                    string copyFilePath;
                    Object sourceAsset = AssetDatabase.LoadAssetAtPath(meshPath, typeof(Object));

                    if (sourceMeshIDs.Contains(id))
                    {
                        // 已复制过，直接复用副本
                        copyFilePath = GenerateCopyPath(sourceAsset, FxModelCopyPath);
                    }
                    else
                    {
                        sourceMeshIDs.Add(id);
                        copyFilePath = CopyAssets(sourceAsset, FxModelCopyPath);
                    }

                    if (!string.IsNullOrEmpty(copyFilePath))
                    {
                        ReplaceMeshFilterMesh(filter, copyFilePath);
                    }

                }

                AssetDatabase.Refresh();

                if (skippedModelPrefab > 0)
                {
                    LogVerbose($"[MeshFilter网格] 已安全跳过嵌套模型预制体内部节点 {skippedModelPrefab} 个" +
                              "（这些 mesh 引用已由复制预制体阶段通过 GUID 替换正确处理）");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 替换 MeshFilter 的 mesh 引用（处理逻辑参考 ReplaceParticleMesh）
        /// 兼容两种副本形态：
        ///   1. 副本是独立的 .mesh / .asset 文件 → 直接加载并赋值
        ///   2. 副本是模型文件（.fbx 等） → 从中按原 mesh 名称匹配对应子 mesh
        /// </summary>
        /// <param name="filter">需要更新引用的 MeshFilter</param>
        /// <param name="meshPath">复制后资源的路径</param>
        private void ReplaceMeshFilterMesh(MeshFilter filter, string meshPath)
        {
            var loaded = AssetDatabase.LoadAssetAtPath(meshPath, typeof(Object));
            if (loaded == null)
            {
                Debug.LogError($"[MeshFilter网格] 副本加载失败: {meshPath}");
                return;
            }

            // 情况 1：独立 mesh 资源
            if (loaded is Mesh meshAsset)
            {
                filter.sharedMesh = meshAsset;
                LogVerbose($"[MeshFilter网格] 网格替换成功: {filter.name} -> {meshPath}");
                return;
            }

            // 情况 2：模型文件（GameObject 根），按原 mesh 名匹配子 mesh
            if (loaded is GameObject modelGo)
            {
                Mesh originalMesh = filter.sharedMesh;
                var filtersInModel = modelGo.GetComponentsInChildren<MeshFilter>(true);
                foreach (var f in filtersInModel)
                {
                    if (f.sharedMesh != null && originalMesh != null
                        && f.sharedMesh.name == originalMesh.name)
                    {
                        filter.sharedMesh = f.sharedMesh;
                        LogVerbose($"[MeshFilter网格] 从模型中匹配网格成功: {filter.name} -> {f.sharedMesh.name}");
                        return;
                    }
                }
                Debug.LogWarning($"[MeshFilter网格] 模型 {meshPath} 中未找到与 '{originalMesh?.name}' 同名的子 mesh");
            }
        }

        #endregion

        #region 动画 Timeline 复制方法

        /// <summary>
        /// 复制预制体中 PlayableDirector / Animator 使用的 Timeline、Controller 及引用的动画资源
        /// 流程：
        /// 1. 收集所有 Animator / PlayableDirector 引用的 Controller、Timeline 与 AnimationClip
        /// 2. 先复制动画依赖（独立 .anim 或整个模型文件 .fbx 等），建立 GUID 映射
        /// 3. 复制 Controller / Timeline 资源，并替换其 YAML 内的动画 GUID 引用
        /// 4. 更新组件上的 Controller / Timeline 引用
        /// 跨组件共享 copiedAssetIDs，确保重复引用的资源只复制一次
        /// </summary>
        /// <param name="instance">场景中的预制体实例</param>
        private void CopyAnimationAndTimeline(GameObject instance)
        {
            List<string> copiedAssetIDs = new List<string>();
            Dictionary<string, string> guidMap = new Dictionary<string, string>();
            Dictionary<string, string> sourcePathToCopyPath = new Dictionary<string, string>();
            List<string> excludePaths = BuildExcludePaths("动画Timeline");

            HashSet<AnimationClip> animationClips = new HashSet<AnimationClip>();
            Dictionary<string, RuntimeAnimatorController> animatorControllers = new Dictionary<string, RuntimeAnimatorController>();
            Dictionary<string, PlayableAsset> playableAssets = new Dictionary<string, PlayableAsset>();

            var animators = instance.GetComponentsInChildren<Animator>(true);
            var directors = instance.GetComponentsInChildren<PlayableDirector>(true);

            EditorUtility.DisplayProgressBar("处理中", "正在收集动画与 Timeline 资源...", 0.2f);

            try
            {
                foreach (Animator animator in animators)
                {
                    CollectAssetsFromAnimator(animator, animationClips, animatorControllers);
                }

                foreach (PlayableDirector director in directors)
                {
                    CollectAssetsFromPlayableDirector(director, animationClips, playableAssets);
                }

                EditorUtility.DisplayProgressBar("处理中", "正在复制动画片段...", 0.4f);
                foreach (AnimationClip clip in animationClips)
                {
                    CopyAnimationClipIfNeeded(clip, animationCopyPath, copiedAssetIDs, excludePaths, guidMap, sourcePathToCopyPath);
                }

                EditorUtility.DisplayProgressBar("处理中", "正在复制 Timeline 资源...", 0.6f);
                List<string> copiedPlayablePaths = new List<string>();
                foreach (PlayableAsset playableAsset in playableAssets.Values)
                {
                    string copyPath = CopyMainAssetIfNeeded(playableAsset, animationCopyPath, copiedAssetIDs, excludePaths,
                        guidMap, sourcePathToCopyPath, "动画Timeline");
                    if (!string.IsNullOrEmpty(copyPath))
                    {
                        copiedPlayablePaths.Add(copyPath);
                    }
                }

                EditorUtility.DisplayProgressBar("处理中", "正在复制 Animator Controller...", 0.75f);
                List<string> copiedControllerPaths = new List<string>();
                foreach (RuntimeAnimatorController controller in animatorControllers.Values)
                {
                    string copyPath = CopyMainAssetIfNeeded(controller, animationCopyPath, copiedAssetIDs, excludePaths,
                        guidMap, sourcePathToCopyPath, "动画Timeline");
                    if (!string.IsNullOrEmpty(copyPath))
                    {
                        copiedControllerPaths.Add(copyPath);
                    }
                }

                List<string> copiedYamlPaths = new List<string>();
                copiedYamlPaths.AddRange(copiedPlayablePaths);
                copiedYamlPaths.AddRange(copiedControllerPaths);
                foreach (string copiedYamlPath in copiedYamlPaths)
                {
                    ReplaceGuidReferencesInFile(copiedYamlPath, guidMap);
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayProgressBar("处理中", "正在更新组件引用...", 0.95f);
                foreach (Animator animator in animators)
                {
                    ReplaceAnimatorControllerReference(animator, sourcePathToCopyPath);
                }

                foreach (PlayableDirector director in directors)
                {
                    ReplacePlayableDirectorReference(director, sourcePathToCopyPath, instance);
                }

                AssetDatabase.Refresh();
                LogVerbose($"[动画Timeline] 处理完成: Animator {animators.Length} 个, PlayableDirector {directors.Length} 个, " +
                          $"Controller {animatorControllers.Count} 个, Timeline/Playable {playableAssets.Count} 个, " +
                          $"复制资源 {copiedAssetIDs.Count} 个");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 从 Animator 收集 Controller 及其引用的 AnimationClip（含 CollectDependencies 递归依赖）
        /// </summary>
        private void CollectAssetsFromAnimator(Animator animator, HashSet<AnimationClip> clips,
            Dictionary<string, RuntimeAnimatorController> controllers)
        {
            if (animator == null) return;

            RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            if (controller == null) return;

            RegisterAnimatorController(controllers, controller);

            if (controller is AnimatorOverrideController overrideController)
            {
                RegisterAnimatorController(controllers, overrideController.runtimeAnimatorController);
            }

            foreach (AnimationClip clip in controller.animationClips)
            {
                AddAnimationClip(clips, clip);
            }

            CollectDependenciesIntoSets(new Object[] { controller }, clips, controllers, null);
        }

        /// <summary>
        /// 从 PlayableDirector 收集 Timeline 及其引用的 AnimationClip / 嵌套 PlayableAsset
        /// </summary>
        private void CollectAssetsFromPlayableDirector(PlayableDirector director, HashSet<AnimationClip> clips,
            Dictionary<string, PlayableAsset> playableAssets)
        {
            if (director == null) return;

            PlayableAsset playableAsset = director.playableAsset;
            if (playableAsset == null) return;

            RegisterPlayableAsset(playableAssets, playableAsset);

            if (playableAsset is TimelineAsset timeline)
            {
                CollectAssetsFromTimelineTracks(timeline, clips, playableAssets);
            }

            CollectDependenciesIntoSets(new Object[] { playableAsset }, clips, null, playableAssets);
        }

        /// <summary>
        /// 递归遍历 Timeline 全部轨道（含 GroupTrack 子轨道），收集 Clip 与 PlayableAsset
        /// </summary>
        private void CollectAssetsFromTimelineTracks(TimelineAsset timeline, HashSet<AnimationClip> clips,
            Dictionary<string, PlayableAsset> playableAssets)
        {
            foreach (TrackAsset rootTrack in timeline.GetRootTracks())
            {
                CollectAssetsFromTrackRecursive(rootTrack, clips, playableAssets);
            }
        }

        private void CollectAssetsFromTrackRecursive(TrackAsset track, HashSet<AnimationClip> clips,
            Dictionary<string, PlayableAsset> playableAssets)
        {
            if (track == null) return;

            if (track is AnimationTrack animationTrack && animationTrack.infiniteClip != null)
            {
                AddAnimationClip(clips, animationTrack.infiniteClip);
            }

            foreach (TimelineClip timelineClip in track.GetClips())
            {
                CollectAssetsFromTimelineClip(timelineClip, clips, playableAssets);
            }

            foreach (TrackAsset childTrack in track.GetChildTracks())
            {
                CollectAssetsFromTrackRecursive(childTrack, clips, playableAssets);
            }
        }

        private void CollectAssetsFromTimelineClip(TimelineClip timelineClip, HashSet<AnimationClip> clips,
            Dictionary<string, PlayableAsset> playableAssets)
        {
            if (timelineClip == null || timelineClip.asset == null) return;

            if (timelineClip.asset is AnimationPlayableAsset animationPlayable && animationPlayable.clip != null)
            {
                AddAnimationClip(clips, animationPlayable.clip);
            }

            if (timelineClip.asset is PlayableAsset clipPlayableAsset)
            {
                RegisterPlayableAsset(playableAssets, clipPlayableAsset);
                if (clipPlayableAsset is TimelineAsset nestedTimeline)
                {
                    CollectAssetsFromTimelineTracks(nestedTimeline, clips, playableAssets);
                }
            }

            CollectDependenciesIntoSets(new Object[] { timelineClip.asset }, clips, null, playableAssets);
        }

        /// <summary>
        /// 使用 EditorUtility.CollectDependencies 补充收集动画 / Controller / Playable 依赖
        /// </summary>
        private void CollectDependenciesIntoSets(Object[] roots, HashSet<AnimationClip> clips,
            Dictionary<string, RuntimeAnimatorController> controllers, Dictionary<string, PlayableAsset> playableAssets)
        {
            Object[] dependencies = EditorUtility.CollectDependencies(roots);
            foreach (Object dependency in dependencies)
            {
                if (dependency == null) continue;

                if (dependency is AnimationClip clip)
                {
                    AddAnimationClip(clips, clip);
                }
                else if (dependency is RuntimeAnimatorController controller && controllers != null)
                {
                    RegisterAnimatorController(controllers, controller);
                }
                else if (dependency is PlayableAsset playableAsset && playableAssets != null)
                {
                    RegisterPlayableAsset(playableAssets, playableAsset);
                }
            }
        }

        private void RegisterAnimatorController(Dictionary<string, RuntimeAnimatorController> controllers,
            RuntimeAnimatorController controller)
        {
            if (controller == null) return;

            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return;

            controllers[path] = controller;
        }

        private void RegisterPlayableAsset(Dictionary<string, PlayableAsset> playableAssets, PlayableAsset playableAsset)
        {
            if (playableAsset == null) return;

            string path = AssetDatabase.GetAssetPath(playableAsset);
            if (string.IsNullOrEmpty(path)) return;

            playableAssets[path] = playableAsset;
        }

        /// <summary>
        /// 过滤无效 / 预览用 AnimationClip 后加入集合
        /// </summary>
        private void AddAnimationClip(HashSet<AnimationClip> clips, AnimationClip clip)
        {
            if (clip == null) return;
            if (clip.name.StartsWith("__preview__")) return;

            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path)) return;

            clips.Add(clip);
        }

        /// <summary>
        /// 复制单个 AnimationClip；若片段来自模型文件则复制整个模型（与网格复制逻辑一致）
        /// </summary>
        private void CopyAnimationClipIfNeeded(AnimationClip clip, string copyPath, List<string> copiedAssetIDs,
            List<string> excludePaths, Dictionary<string, string> guidMap, Dictionary<string, string> sourcePathToCopyPath)
        {
            if (clip == null) return;

            string assetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(assetPath)) return;

            if (IsAssetPathExcluded(assetPath, excludePaths)) return;

            Object assetToCopy = IsModelAssetPath(assetPath)
                ? AssetDatabase.LoadMainAssetAtPath(assetPath)
                : (Object)clip;

            CopyMainAssetIfNeeded(assetToCopy, copyPath, copiedAssetIDs, excludePaths, guidMap, sourcePathToCopyPath, "动画Timeline");
        }

        /// <summary>
        /// 通用：复制主资源到目标目录（GUID 去重），并记录路径 / GUID 映射
        /// </summary>
        private string CopyMainAssetIfNeeded(Object sourceAsset, string copyPath, List<string> copiedAssetIDs,
            List<string> excludePaths, Dictionary<string, string> guidMap, Dictionary<string, string> sourcePathToCopyPath,
            string logTag)
        {
            if (sourceAsset == null) return null;

            string assetPath = AssetDatabase.GetAssetPath(sourceAsset);
            if (string.IsNullOrEmpty(assetPath)) return null;

            if (IsAssetPathExcluded(assetPath, excludePaths)) return null;

            string id = AssetDatabase.AssetPathToGUID(assetPath);
            string copyFilePath = GenerateCopyPathFromAssetPath(assetPath, copyPath);

            if (copiedAssetIDs.Contains(id))
            {
                if (sourcePathToCopyPath.TryGetValue(assetPath, out string existingCopyPath))
                {
                    return existingCopyPath;
                }

                if (!string.IsNullOrEmpty(copyFilePath) && File.Exists(GetFullAssetPath(copyFilePath)))
                {
                    sourcePathToCopyPath[assetPath] = copyFilePath;
                    return copyFilePath;
                }

                return null;
            }

            copiedAssetIDs.Add(id);
            copyFilePath = CopyAssetAtPath(assetPath, copyPath);

            if (string.IsNullOrEmpty(copyFilePath)) return null;

            sourcePathToCopyPath[assetPath] = copyFilePath;

            string newId = AssetDatabase.AssetPathToGUID(copyFilePath);
            if (!string.IsNullOrEmpty(newId))
            {
                guidMap[id] = newId;
            }

            LogVerbose($"[{logTag}] 已复制资源: {Path.GetFileName(assetPath)} -> {copyFilePath}");
            return copyFilePath;
        }

        /// <summary>
        /// 按源文件路径复制资源（统一使用源文件名生成副本，避免 FBX 子资源命名不一致）
        /// </summary>
        private string CopyAssetAtPath(string assetPath, string parentFolder)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            string targetPath = GenerateCopyPathFromAssetPath(assetPath, parentFolder);
            if (string.IsNullOrEmpty(targetPath)) return null;

            string fullPath = GetFullAssetPath(targetPath);
            if (File.Exists(fullPath))
            {
                LogVerbose($"[资源复制] 文件已存在，跳过: {targetPath}");
                return targetPath;
            }

            bool success = AssetDatabase.CopyAsset(assetPath, targetPath);
            if (success)
            {
                LogVerbose($"[资源复制] 复制成功: {targetPath}");
                return targetPath;
            }

            Debug.LogError($"[资源复制] 复制失败: {assetPath} -> {targetPath}");
            return null;
        }

        /// <summary>
        /// 替换资源 YAML 文件中的 GUID 引用（按 GUID 长度降序，避免部分匹配；每行替换全部命中项）
        /// </summary>
        private void ReplaceGuidReferencesInFile(string assetPath, Dictionary<string, string> guidMap)
        {
            if (string.IsNullOrEmpty(assetPath) || guidMap == null || guidMap.Count == 0) return;

            string fullPath = GetFullAssetPath(assetPath);
            if (!File.Exists(fullPath)) return;

            KeyValuePair<string, string>[] sortedPairs = guidMap
                .OrderByDescending(pair => pair.Key.Length)
                .ToArray();

            string[] content = File.ReadAllLines(fullPath);
            List<string> replaceContent = new List<string>(content.Length);

            foreach (string line in content)
            {
                string current = line;
                foreach (KeyValuePair<string, string> pair in sortedPairs)
                {
                    if (current.Contains(pair.Key))
                    {
                        current = current.Replace(pair.Key, pair.Value);
                    }
                }
                replaceContent.Add(current);
            }

            File.WriteAllLines(fullPath, replaceContent);
        }

        /// <summary>
        /// 将 Animator 的 Controller 引用更新为复制后的资源（支持 FBX 内嵌 Controller 子资源）
        /// </summary>
        private void ReplaceAnimatorControllerReference(Animator animator, Dictionary<string, string> sourcePathToCopyPath)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;

            RuntimeAnimatorController sourceController = animator.runtimeAnimatorController;
            string sourcePath = AssetDatabase.GetAssetPath(sourceController);
            if (string.IsNullOrEmpty(sourcePath)) return;

            if (!sourcePathToCopyPath.TryGetValue(sourcePath, out string copiedPath))
            {
                Debug.LogWarning($"[动画Timeline] Controller 未复制，保持原引用: {sourcePath}");
                return;
            }

            RuntimeAnimatorController copiedController =
                LoadMatchingAssetFromCopy(sourceController, copiedPath) as RuntimeAnimatorController;

            if (copiedController != null)
            {
                animator.runtimeAnimatorController = copiedController;
                LogVerbose($"[动画Timeline] Animator 引用已更新: {animator.name} -> {copiedPath}");
            }
            else
            {
                Debug.LogWarning($"[动画Timeline] 未找到 Controller 副本: {copiedPath}");
            }
        }

        /// <summary>
        /// Timeline 轨道绑定快照（按轨道层级键保存，用于 Timeline 复制后恢复绑定）
        /// </summary>
        private struct TimelineBindingSnapshot
        {
            public string TrackKey;
            public string BoundObjectPath;
            public Object Binding;
        }

        /// <summary>
        /// 将 PlayableDirector 的 Timeline 引用更新为复制后的资源，并恢复轨道 / ExposedReference 绑定
        /// </summary>
        private void ReplacePlayableDirectorReference(PlayableDirector director, Dictionary<string, string> sourcePathToCopyPath,
            GameObject prefabInstanceRoot)
        {
            if (director == null || director.playableAsset == null) return;

            PlayableAsset sourceAsset = director.playableAsset;
            string sourcePath = AssetDatabase.GetAssetPath(sourceAsset);
            if (string.IsNullOrEmpty(sourcePath)) return;

            if (!sourcePathToCopyPath.TryGetValue(sourcePath, out string copiedPath))
            {
                Debug.LogWarning($"[动画Timeline] Timeline 未复制，保持原引用: {sourcePath}");
                return;
            }

            PlayableAsset copiedAsset = LoadMatchingAssetFromCopy(sourceAsset, copiedPath) as PlayableAsset;
            if (copiedAsset == null)
            {
                Debug.LogWarning($"[动画Timeline] 未找到 Timeline 副本: {copiedPath}");
                return;
            }

            GameObject bindingRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(director.gameObject);
            if (bindingRoot == null)
            {
                bindingRoot = prefabInstanceRoot != null ? prefabInstanceRoot : director.gameObject;
            }

            List<TimelineBindingSnapshot> bindingSnapshots = CaptureTimelineBindings(director, bindingRoot);
            Dictionary<string, Object> exposedReferences = CaptureExposedReferences(director);
            TimelineAsset oldTimeline = sourceAsset as TimelineAsset;

            // 先清理旧 Timeline 的轨道绑定，避免 m_SceneBindings 残留旧轨道引用（隐式依赖旧 Timeline）
            if (oldTimeline != null)
            {
                ClearTimelineBindings(director, oldTimeline);
            }

            director.playableAsset = copiedAsset;

            if (oldTimeline != null && copiedAsset is TimelineAsset newTimeline)
            {
                RestoreTimelineBindings(director, newTimeline, bindingSnapshots, bindingRoot);
            }

            RestoreExposedReferences(director, exposedReferences);
            director.RebuildGraph();
            EditorUtility.SetDirty(director);
            PrefabUtility.RecordPrefabInstancePropertyModifications(director);

            LogVerbose($"[动画Timeline] PlayableDirector 引用已更新: {director.name} -> {copiedPath}, " +
                      $"恢复绑定 {bindingSnapshots.Count} 项, ExposedReference {exposedReferences.Count} 项");
        }

        /// <summary>
        /// 清理指定 Timeline 的全部轨道绑定，防止 PlayableDirector 序列化中残留旧轨道引用
        /// </summary>
        private void ClearTimelineBindings(PlayableDirector director, TimelineAsset timeline)
        {
            if (director == null || timeline == null) return;

            int clearedCount = 0;
            foreach (TrackAsset track in EnumerateAllTimelineTracks(timeline))
            {
                if (track == null) continue;
                if (director.GetGenericBinding(track) == null) continue;

                director.ClearGenericBinding(track);
                clearedCount++;
            }

            if (clearedCount > 0)
            {
                LogVerbose($"[动画Timeline] 已清理旧轨道绑定: {clearedCount} 项");
            }
        }

        /// <summary>
        /// 捕获 PlayableDirector 当前 Timeline 的全部轨道绑定
        /// </summary>
        private List<TimelineBindingSnapshot> CaptureTimelineBindings(PlayableDirector director, GameObject bindingRoot)
        {
            List<TimelineBindingSnapshot> snapshots = new List<TimelineBindingSnapshot>();
            if (director == null || director.playableAsset == null) return snapshots;

            if (director.playableAsset is TimelineAsset timeline)
            {
                foreach (TrackAsset track in EnumerateAllTimelineTracks(timeline))
                {
                    Object binding = director.GetGenericBinding(track);
                    if (binding == null) continue;

                    snapshots.Add(new TimelineBindingSnapshot
                    {
                        TrackKey = GetTimelineTrackBindingKey(track),
                        BoundObjectPath = GetTransformPathFromRoot(GetBindingTransform(binding), bindingRoot.transform),
                        Binding = binding
                    });
                }

                return snapshots;
            }

            foreach (PlayableBinding output in director.playableAsset.outputs)
            {
                Object binding = director.GetGenericBinding(output.sourceObject);
                if (binding == null) continue;

                snapshots.Add(new TimelineBindingSnapshot
                {
                    TrackKey = output.sourceObject != null ? output.sourceObject.name : output.streamName,
                    BoundObjectPath = GetTransformPathFromRoot(GetBindingTransform(binding), bindingRoot.transform),
                    Binding = binding
                });
            }

            return snapshots;
        }

        /// <summary>
        /// 将捕获的绑定恢复到复制后的 Timeline 轨道上（优先按轨道键匹配，失败则按节点路径兜底）
        /// </summary>
        private void RestoreTimelineBindings(PlayableDirector director, TimelineAsset newTimeline,
            List<TimelineBindingSnapshot> snapshots, GameObject bindingRoot)
        {
            if (director == null || newTimeline == null || snapshots == null || snapshots.Count == 0) return;

            Dictionary<string, TrackAsset> trackMap = BuildTimelineTrackBindingMap(newTimeline);
            Transform rootTransform = bindingRoot != null ? bindingRoot.transform : director.transform;
            int restoredCount = 0;

            foreach (TimelineBindingSnapshot snapshot in snapshots)
            {
                if (snapshot.Binding == null) continue;

                TrackAsset targetTrack = null;
                if (!string.IsNullOrEmpty(snapshot.TrackKey))
                {
                    trackMap.TryGetValue(snapshot.TrackKey, out targetTrack);
                }

                if (targetTrack == null && !string.IsNullOrEmpty(snapshot.TrackKey))
                {
                    targetTrack = FindTrackByBindingKeyFallback(newTimeline, snapshot.TrackKey);
                }

                if (targetTrack == null)
                {
                    Debug.LogWarning($"[动画Timeline] 未能匹配轨道绑定: trackKey={snapshot.TrackKey}, objectPath={snapshot.BoundObjectPath}");
                    continue;
                }

                Object resolvedBinding = ResolveBindingOnInstance(snapshot.Binding, rootTransform);
                if (resolvedBinding == null)
                {
                    Debug.LogWarning($"[动画Timeline] 绑定对象在预制体中未找到: {snapshot.BoundObjectPath}");
                    continue;
                }

                director.SetGenericBinding(targetTrack, resolvedBinding);
                restoredCount++;
            }

            LogVerbose($"[动画Timeline] 轨道绑定恢复 {restoredCount}/{snapshots.Count}");
        }

        /// <summary>
        /// 捕获 PlayableDirector 上的 ExposedReference 绑定（ControlTrack 等 clip 内场景引用）
        /// </summary>
        private Dictionary<string, Object> CaptureExposedReferences(PlayableDirector director)
        {
            Dictionary<string, Object> exposedReferences = new Dictionary<string, Object>();
            if (director == null) return exposedReferences;

            SerializedObject serializedDirector = new SerializedObject(director);
            SerializedProperty references = serializedDirector.FindProperty("m_ExposedReferences.m_References");
            if (references == null || !references.isArray) return exposedReferences;

            for (int i = 0; i < references.arraySize; i++)
            {
                SerializedProperty element = references.GetArrayElementAtIndex(i);
                SerializedProperty nameProperty = element.FindPropertyRelative("exposedName");
                SerializedProperty valueProperty = element.FindPropertyRelative("exposedValue");
                if (nameProperty == null || valueProperty == null) continue;

                string exposedName = nameProperty.stringValue;
                Object exposedValue = valueProperty.objectReferenceValue;
                if (string.IsNullOrEmpty(exposedName) || exposedValue == null) continue;

                exposedReferences[exposedName] = exposedValue;
            }

            return exposedReferences;
        }

        /// <summary>
        /// 恢复 PlayableDirector 上的 ExposedReference 绑定
        /// </summary>
        private void RestoreExposedReferences(PlayableDirector director, Dictionary<string, Object> exposedReferences)
        {
            if (director == null || exposedReferences == null || exposedReferences.Count == 0) return;

            Transform rootTransform = director.transform;
            GameObject prefabRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(director.gameObject);
            if (prefabRoot != null)
            {
                rootTransform = prefabRoot.transform;
            }

            foreach (KeyValuePair<string, Object> pair in exposedReferences)
            {
                Object resolved = ResolveBindingOnInstance(pair.Value, rootTransform);
                if (resolved == null)
                {
                    resolved = pair.Value;
                }

                director.SetReferenceValue(new PropertyName(pair.Key), resolved);
            }
        }

        /// <summary>
        /// 构建 Timeline 轨道绑定键 -> 轨道 的映射表
        /// </summary>
        private Dictionary<string, TrackAsset> BuildTimelineTrackBindingMap(TimelineAsset timeline)
        {
            Dictionary<string, TrackAsset> trackMap = new Dictionary<string, TrackAsset>();
            foreach (TrackAsset track in EnumerateAllTimelineTracks(timeline))
            {
                string key = GetTimelineTrackBindingKey(track);
                if (string.IsNullOrEmpty(key) || trackMap.ContainsKey(key)) continue;
                trackMap.Add(key, track);
            }

            return trackMap;
        }

        /// <summary>
        /// 递归枚举 Timeline 中的全部轨道（含 GroupTrack 子轨道）
        /// </summary>
        private IEnumerable<TrackAsset> EnumerateAllTimelineTracks(TimelineAsset timeline)
        {
            if (timeline == null) yield break;

            foreach (TrackAsset rootTrack in timeline.GetRootTracks())
            {
                foreach (TrackAsset track in EnumerateTrackHierarchy(rootTrack))
                {
                    yield return track;
                }
            }
        }

        private IEnumerable<TrackAsset> EnumerateTrackHierarchy(TrackAsset track)
        {
            if (track == null) yield break;

            yield return track;
            foreach (TrackAsset childTrack in track.GetChildTracks())
            {
                foreach (TrackAsset child in EnumerateTrackHierarchy(childTrack))
                {
                    yield return child;
                }
            }
        }

        /// <summary>
        /// 生成 Timeline 轨道唯一绑定键：轨道类型 + 层级路径 + 同级同名序号
        /// </summary>
        private string GetTimelineTrackBindingKey(TrackAsset track)
        {
            if (track == null) return string.Empty;

            List<string> segments = new List<string>();
            TrackAsset current = track;
            while (current != null)
            {
                segments.Insert(0, FormatTimelineTrackSegment(current));
                current = current.parent as TrackAsset;
            }

            return track.GetType().Name + "::" + string.Join("/", segments);
        }

        private string FormatTimelineTrackSegment(TrackAsset track)
        {
            IEnumerable<TrackAsset> siblings;
            if (track.parent is TrackAsset parentTrack)
            {
                siblings = parentTrack.GetChildTracks();
            }
            else
            {
                siblings = track.timelineAsset.GetRootTracks();
            }

            int index = 0;
            foreach (TrackAsset sibling in siblings)
            {
                if (sibling == track) break;
                if (sibling.GetType() == track.GetType() && sibling.name == track.name)
                {
                    index++;
                }
            }

            return track.name + "#" + index;
        }

        /// <summary>
        /// 绑定键精确匹配失败时，按轨道类型 + 层级路径后缀兜底查找
        /// </summary>
        private TrackAsset FindTrackByBindingKeyFallback(TimelineAsset timeline, string trackKey)
        {
            if (timeline == null || string.IsNullOrEmpty(trackKey)) return null;

            int splitIndex = trackKey.IndexOf("::", System.StringComparison.Ordinal);
            if (splitIndex <= 0) return null;

            string trackTypeName = trackKey.Substring(0, splitIndex);
            string trackPath = trackKey.Substring(splitIndex + 2);

            foreach (TrackAsset track in EnumerateAllTimelineTracks(timeline))
            {
                if (track.GetType().Name != trackTypeName) continue;

                string candidateKey = GetTimelineTrackBindingKey(track);
                int candidateSplit = candidateKey.IndexOf("::", System.StringComparison.Ordinal);
                if (candidateSplit <= 0) continue;

                if (candidateKey.Substring(candidateSplit + 2) == trackPath)
                {
                    return track;
                }
            }

            return null;
        }
        private Object ResolveBindingOnInstance(Object binding, Transform rootTransform)
        {
            if (binding == null || rootTransform == null) return binding;

            Transform sourceTransform = GetBindingTransform(binding);
            if (sourceTransform == null) return binding;

            string relativePath = GetTransformPathFromRoot(sourceTransform, rootTransform);
            if (string.IsNullOrEmpty(relativePath))
            {
                return binding;
            }

            Transform targetTransform = rootTransform;
            if (!string.IsNullOrEmpty(relativePath))
            {
                targetTransform = rootTransform.Find(relativePath);
                if (targetTransform == null)
                {
                    targetTransform = FindTransformByRelativePath(rootTransform, relativePath);
                }
            }

            if (targetTransform == null)
            {
                return null;
            }

            if (binding is GameObject)
            {
                return targetTransform.gameObject;
            }

            if (binding is Component sourceComponent)
            {
                Component targetComponent = targetTransform.GetComponent(sourceComponent.GetType());
                return targetComponent != null ? targetComponent : binding;
            }

            return binding;
        }

        private Transform GetBindingTransform(Object binding)
        {
            if (binding == null) return null;

            if (binding is GameObject gameObject)
            {
                return gameObject.transform;
            }

            if (binding is Component component)
            {
                return component.transform;
            }

            return null;
        }

        /// <summary>
        /// 获取 target 相对 root 的层级路径（root 本身返回空字符串）
        /// </summary>
        private string GetTransformPathFromRoot(Transform target, Transform root)
        {
            if (target == null || root == null) return string.Empty;
            if (target == root) return string.Empty;

            List<string> segments = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                segments.Insert(0, current.name);
                current = current.parent;
            }

            if (current != root) return string.Empty;
            return string.Join("/", segments);
        }

        /// <summary>
        /// Transform.Find 失败时，按相对路径分段逐级查找节点
        /// </summary>
        private Transform FindTransformByRelativePath(Transform root, string relativePath)
        {
            if (root == null || string.IsNullOrEmpty(relativePath)) return root;

            string[] parts = relativePath.Split('/');
            Transform current = root;
            foreach (string part in parts)
            {
                if (current == null) return null;

                Transform next = current.Find(part);
                if (next == null)
                {
                    next = FindDirectChildByName(current, part);
                }

                current = next;
            }

            return current;
        }

        private Transform FindDirectChildByName(Transform parent, string childName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == childName)
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>
        /// 从复制后的资源文件中加载与源对象匹配的子资源（兼容 .fbx / .asset 内嵌资源）
        /// </summary>
        private Object LoadMatchingAssetFromCopy(Object sourceAsset, string copiedAssetPath)
        {
            if (sourceAsset == null || string.IsNullOrEmpty(copiedAssetPath)) return null;

            Object copiedMainAsset = AssetDatabase.LoadMainAssetAtPath(copiedAssetPath);
            if (copiedMainAsset != null
                && copiedMainAsset.GetType() == sourceAsset.GetType()
                && copiedMainAsset.name == sourceAsset.name)
            {
                return copiedMainAsset;
            }

            Object[] copiedSubAssets = AssetDatabase.LoadAllAssetsAtPath(copiedAssetPath);
            foreach (Object copiedSubAsset in copiedSubAssets)
            {
                if (copiedSubAsset == null) continue;
                if (copiedSubAsset.GetType() != sourceAsset.GetType()) continue;
                if (copiedSubAsset.name != sourceAsset.name) continue;

                return copiedSubAsset;
            }

            return copiedMainAsset;
        }

        /// <summary>
        /// 基于源资源路径生成副本路径（使用源文件名，确保同一文件的所有子资源指向同一副本）
        /// </summary>
        private string GenerateCopyPathFromAssetPath(string assetPath, string parentFolder)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            string fileName = Path.GetFileNameWithoutExtension(assetPath);
            string extension = Path.GetExtension(assetPath);
            return parentFolder + "/" + fileName + "_Copy" + extension;
        }

        /// <summary>
        /// 判断资源路径是否位于排除目录中
        /// </summary>
        private bool IsAssetPathExcluded(string assetPath, List<string> excludePaths)
        {
            if (string.IsNullOrEmpty(assetPath) || excludePaths == null || excludePaths.Count == 0)
            {
                return false;
            }

            foreach (string excludePath in excludePaths)
            {
                if (IsPathInFolderOrSubFolder(assetPath, excludePath))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 判断资源路径是否为指定目录本身或其子目录内容（严格目录边界匹配）
        /// </summary>
        private bool IsPathInFolderOrSubFolder(string assetPath, string folderPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folderPath))
            {
                return false;
            }

            string normalizedAssetPath = assetPath.Replace("\\", "/").TrimEnd('/');
            string normalizedFolderPath = folderPath.Replace("\\", "/").TrimEnd('/');
            if (normalizedAssetPath == normalizedFolderPath)
            {
                return true;
            }

            return normalizedAssetPath.StartsWith(normalizedFolderPath + "/");
        }

        /// <summary>
        /// 判断资源路径是否为模型文件（.fbx / .obj 等）
        /// </summary>
        private bool IsModelAssetPath(string assetPath)
        {
            return AssetImporter.GetAtPath(assetPath) is ModelImporter;
        }

        /// <summary>
        /// 将 Assets 相对路径转换为系统绝对路径
        /// </summary>
        private string GetFullAssetPath(string assetPath)
        {
            return Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + assetPath;
        }

        #endregion
        
        #region 资源复制工具方法
        
        /// <summary>
        /// 生成资源复制后的目标路径
        /// 命名规则：原资源名 + "_Copy" + 原扩展名
        /// 例如：Material.mat -> Material_Copy.mat
        /// </summary>
        /// <param name="asset">源资源对象</param>
        /// <param name="parentFolder">目标父目录</param>
        /// <returns>复制后的资源路径，失败返回null</returns>
        private string GenerateCopyPath(Object asset, string parentFolder)
        {
            if (asset == null)
            {
                Debug.LogError("[路径生成] 资源对象为空！");
                return null;
            }

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError($"[路径生成] 无法获取资源路径: {asset.name}");
                return null;
            }

            // 生成格式：目标目录/源文件名_Copy.扩展名（使用源文件路径，避免 FBX 子资源命名不一致）
            string copyPath = GenerateCopyPathFromAssetPath(assetPath, parentFolder);
            return copyPath;
        }
        
        /// <summary>
        /// 复制资源到指定目录
        /// 功能特点：
        /// 1. 如果目标位置已存在同名文件，则跳过复制（避免覆盖）
        /// 2. 使用 AssetDatabase.CopyAsset 进行复制，保持资源完整性
        /// </summary>
        /// <param name="asset">源资源对象</param>
        /// <param name="parentFolder">目标父目录</param>
        /// <returns>复制后的资源路径，失败返回null</returns>
        private string CopyAssets(Object asset, string parentFolder)
        {
            if (asset == null)
            {
                Debug.LogError("[资源复制] 资源对象为空！");
                return null;
            }
    
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError($"[资源复制] 无法获取资源路径: {asset.name}");
                return null;
            }
    
            // 定义新资源的路径
            string targetPath = GenerateCopyPath(asset, parentFolder);
            if (string.IsNullOrEmpty(targetPath))
            {
                return null;
            }
    
            string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + targetPath;
    
            // 检查目标路径是否已存在 - 如果存在则跳过复制
            if (File.Exists(fullPath))
            {
                LogVerbose($"[资源复制] 文件已存在，跳过: {targetPath}");
                return targetPath;
            }
    
            // 复制资源
            bool success = AssetDatabase.CopyAsset(assetPath, targetPath);
            if (success)
            {
                LogVerbose($"[资源复制] 复制成功: {targetPath}");
                return targetPath;
            }
            else
            {
                Debug.LogError($"[资源复制] 复制失败: {assetPath} -> {targetPath}");
                return null;
            }
        }
        
        /// <summary>
        /// 替换预制体文件中的GUID引用
        /// 核心功能：
        /// 1. 读取预制体的原始文件内容（.prefab是YAML格式的文本文件）
        /// 2. 将文件中的旧GUID替换为新GUID（指向复制后的资源）
        /// 3. 将修改后的内容写入新的预制体文件
        /// 
        /// 这样可以确保复制后的预制体引用的是复制后的子预制体，而不是原始资源
        /// </summary>
        /// <param name="prefab">源预制体对象</param>
        /// <param name="idDictionary">旧GUID到新GUID的映射字典</param>
        /// <returns>复制后的预制体路径</returns>
        private string ReplaceUID(GameObject prefab, Dictionary<string, string> idDictionary)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[GUID替换] 无法获取预制体路径");
                return null;
            }

            // 将Assets相对路径转换为系统绝对路径
            string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + path;
            LogVerbose($"[GUID替换] 处理文件: {fullPath}");
    
            if (!File.Exists(fullPath))
            {
                Debug.LogError($"[GUID替换] 文件不存在: {fullPath}");
                return null;
            }
    
            string[] content = File.ReadAllLines(fullPath);
    
            string copyFilePath = GenerateCopyPath(prefab, newFolderPath);
            if (string.IsNullOrEmpty(copyFilePath))
            {
                return null;
            }
            
            //如果不存在子预制体，则直接将原文件内容写入新文件
            if (idDictionary == null || idDictionary.Count == 0)
            {
                
                File.WriteAllLines(copyFilePath, content);
                AssetDatabase.Refresh();
                return copyFilePath;
            }
            else
            {
                List<string> replaceContent = new List<string>();
                foreach (string line in content)
                {
                    string current = line;
                    foreach (var ids in idDictionary)
                    {
                        if (line.Contains(ids.Key))
                        {
                            current = line.Replace(ids.Key, ids.Value);
                            break;
                        }
                    }
    
                    replaceContent.Add(current);
                }
    
                File.WriteAllLines(copyFilePath, replaceContent);
            }
    
            AssetDatabase.Refresh();
            return copyFilePath;
        }
        
        /// <summary>
        /// 根据 excludeFolders 字段构建排除目录路径列表
        /// 多个材质/网格复制方法共用此方法，避免重复代码
        /// </summary>
        /// <param name="logTag">日志标签，便于区分调用来源</param>
        /// <returns>有效排除目录的路径列表（相对 Assets 路径）</returns>
        private List<string> BuildExcludePaths(string logTag)
        {
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder == null) continue;

                string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                if (AssetDatabase.IsValidFolder(excludePath))
                {
                    excludePaths.Add(excludePath);
                    LogVerbose($"[{logTag}] 已添加排除目录: {excludePath}");
                }
            }
            return excludePaths;
        }

        /// <summary>
        /// 获取资源的GUID
        /// </summary>
        /// <param name="obj">资源对象</param>
        /// <returns>资源的GUID字符串，失败返回空字符串</returns>
        private string GetAssetGuid(Object obj)
        {
            if (obj == null) return "";

            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return "";
            return AssetDatabase.AssetPathToGUID(path);
        }
        
        #endregion
        
        #region 粒子网格处理方法
        
        /// <summary>
        /// 在粒子系统中查找使用的网格
        /// 仅当粒子渲染模式为Mesh时才有效
        /// </summary>
        /// <param name="particleSystem">粒子系统组件</param>
        /// <returns>网格资源的路径，未找到返回null</returns>
        private string FindMeshInParticleSystem(ParticleSystem particleSystem)
        {
            Mesh foundMesh = null;
            string sourceAssetPath = null;
    
            var renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
            {
                // 此粒子系统没有渲染器组件，静默跳过
                return null;
            }
    
            // 获取Renderer使用的Mesh
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh)
            {
                foundMesh = renderer.mesh;
            }
    
            if (foundMesh == null)
            {
                // 渲染模式非Mesh或未设置网格，静默跳过
                return null;
            }
    
            // 查找Mesh的源文件路径
            sourceAssetPath = AssetDatabase.GetAssetPath(foundMesh);

           
            if (string.IsNullOrEmpty(sourceAssetPath))
            {
                Debug.LogWarning($"[粒子网格] 无法获取网格资源路径: {foundMesh.name}");
                return null;
            }
    
            return sourceAssetPath;
        }
        
        /// <summary>
        /// 替换粒子系统的网格引用
        /// 处理两种情况：
        /// 1. 网格是独立的.mesh文件 - 直接加载并替换
        /// 2. 网格是模型文件（如.fbx）的子资源 - 遍历模型中的MeshFilter查找同名网格
        /// </summary>
        /// <param name="particle">粒子系统组件</param>
        /// <param name="meshPath">新网格资源的路径</param>
        private void ReplaceParticleMesh(ParticleSystem particle, string meshPath)
        {
            string type = Path.GetExtension(meshPath);
            var renderer = particle.GetComponent<ParticleSystemRenderer>();
           
            if (renderer == null)
            {
                Debug.LogError($"[粒子网格] 渲染器为空: {particle.name}");
                return;
            }
            
            // 仅处理Mesh渲染模式的粒子
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh)
            {
                var mesh = AssetDatabase.LoadAssetAtPath(meshPath, typeof(Object));
                var foundMesh = renderer.mesh; // 当前使用的网格，用于名称匹配
                
                // 情况1：资源本身就是Mesh类型
                if (mesh is Mesh)
                {
                    renderer.mesh = (Mesh)mesh;
                    LogVerbose($"[粒子网格] 网格替换成功: {particle.name}");
                }
                else
                {
                    // 情况2：资源是模型文件（如FBX），需要从中提取网格
                    var model = mesh as GameObject;
                    var filters = model.GetComponentsInChildren<MeshFilter>();
                    if (filters == null)
                    {
                        Debug.LogError($"[粒子网格] 模型中无MeshFilter: {meshPath}");
                        return;
                    }
                    
                    // 遍历模型中的所有MeshFilter，找到同名网格
                    foreach (var filter in filters)
                    {
                        var filterMesh = filter.sharedMesh;
                        if (filterMesh.name == foundMesh.name)
                        {
                            renderer.mesh = filterMesh;
                            LogVerbose($"[粒子网格] 从模型中匹配网格成功: {filterMesh.name}");
                        }
                    }
                }
            }
        }
        
        #endregion
        
        #region 预制体保存方法
        
        /// <summary>
        /// 递归应用预制体实例的修改
        /// 处理流程：
        /// 1. 对当前预制体实例应用修改（ApplyPrefabInstance）
        /// 2. 解包当前预制体（UnpackPrefabInstance），以便处理嵌套的子预制体
        /// 3. 递归处理所有嵌套的子预制体
        /// 
        /// 注意：此操作会解包预制体，修改后无法恢复预制体链接
        /// </summary>
        /// <param name="parent">预制体实例的Transform</param>
        private void ApplyModificationsRecursively(Transform parent)
        {
            if(PrefabUtility.IsAnyPrefabInstanceRoot(parent.gameObject))
            {
                PrefabUtility.ApplyPrefabInstance(parent.gameObject, InteractionMode.AutomatedAction);
                PrefabUtility.UnpackPrefabInstance(parent.gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                LogVerbose($"[预制体保存] 已应用并解包: {parent.name}");
            }
            else
            {
                Debug.LogWarning($"[预制体保存] '{parent.name}' 不是预制体实例，跳过处理");
                return;
            }
            
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject == parent.gameObject)
                    continue;
                
                if(PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                {
                    GameObject prefabRoot = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);
                    string childPath = AssetDatabase.GetAssetPath(prefabRoot);
                    
                    if (Path.GetExtension(childPath) == ".prefab")
                    {   
                        LogVerbose($"[预制体保存] 递归处理子预制体: {child.name}");
                        ApplyModificationsRecursively(child.transform);
                    }
                }
            }
        }
        
        #endregion
        
        #region 工具辅助方法
        
        /// <summary>
        /// 判定 GameObject 是否处于"嵌套模型类型预制体实例"的内部（含其根节点本身）
        /// 
        /// 实现策略：基于资源路径 + AssetImporter 类型判定
        ///   1. 用 PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) 取 go 所属的最近 prefab 实例资源路径
        ///      该 API 天然包含 go 自身节点（若 go 自己就是 prefab 实例根，返回的就是它对应的资源路径），
        ///      不依赖 IsAnyPrefabInstanceRoot 在嵌套场景下的细微差异
        ///   2. 与 rootInstance 的 prefab 路径比对，相同则属于"非嵌套（同属当前根 prefab）"，不跳过
        ///   3. 用 AssetImporter is ModelImporter 判定该路径是否为模型文件（.fbx / .obj / .blend 等）
        ///      —— 这是判定"模型 prefab"最权威的方式，不依赖 PrefabAssetType.Model
        ///         （注意：从 .fbx 中拖出后另存为 .prefab 的资源，PrefabAssetType 是 Regular 而非 Model，
        ///         但其原始网格仍由模型 prefab 提供。本方法只关心"嵌套实例的资源本身是否为模型文件"）
        /// 
        /// 用途：mesh 替换流程中安全跳过模型预制体内部节点，避免破坏"复制预制体"阶段
        ///       已通过整体复制 + GUID 替换建立的引用链
        /// 
        /// 注意：仅过滤模型预制体；嵌套的普通子 prefab 内部节点不会被跳过
        /// </summary>
        /// <param name="go">待判定的 GameObject</param>
        /// <param name="rootInstance">工具当前处理的根预制体实例</param>
        /// <returns>true-属于嵌套模型预制体内部；false-属于根预制体或非模型类嵌套节点</returns>
        private bool IsInsideModelPrefabInstance(GameObject go, GameObject rootInstance)
        {
            if (go == null || rootInstance == null) return false;
            if (go == rootInstance) return false;
            if (!PrefabUtility.IsPartOfPrefabInstance(go)) return false;

            // 直接拿"go 所属的最近 prefab 实例"对应的资源路径（API 已天然包含 go 自身节点）
            string nearestPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            if (string.IsNullOrEmpty(nearestPath)) return false;

            // 同属当前 rootInstance（即不是嵌套），不跳过
            string rootPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(rootInstance);
            if (nearestPath == rootPath) return false;

            // 用 AssetImporter 判定：模型文件(.fbx/.obj/.blend 等)的 importer 必为 ModelImporter
            var importer = AssetImporter.GetAtPath(nearestPath);
            return importer is ModelImporter;
        }

        /// <summary>
        /// 判断GameObject是否在场景中
        /// 用于区分：
        /// - 场景中的实例（返回true）：可以进行材质和网格替换操作
        /// - Assets中的预制体（返回false）：只能进行预制体复制操作
        /// </summary>
        /// <param name="gameObject">要检查的GameObject</param>
        /// <returns>true-在场景中；false-不在场景中（Assets中的预制体）</returns>
        private bool IsGameObjectInScene(GameObject gameObject)
        {
            try
            {
                // 尝试获取GameObject所属的Scene
                Scene scene = gameObject.scene;
            
                // 如果没有抛出异常，并且scene是有效的，则说明这个GameObject是在场景中的实例
                return scene.IsValid();
            }
            catch 
            {
                // GameObject不是场景的一部分（例如，它是预制件）
                return false;
            }
        }
        
        #endregion
    }
}
