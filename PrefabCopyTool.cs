using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

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
    /// 6. 支持排除指定目录的资源不被复制
    /// 
    /// 职责分工：
    ///   "复制材质" 与 "复制粒子材质" 两个按钮分工互斥，针对同一 prefab 选其一即可：
    ///     - 模型类 prefab → 用 "复制材质"
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

        // ========== UI状态 ==========
        /// <summary>当前操作状态信息</summary>
        private string currentStatus = "等待开始";

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
    
            rootPrefab = (GameObject)EditorGUILayout.ObjectField("当前处理预制体:", rootPrefab, typeof(GameObject), true);
            outputFolder = EditorGUILayout.ObjectField("输出文件夹：", outputFolder, typeof(DefaultAsset), false) as DefaultAsset;
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
            EditorGUILayout.Space();
           
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
                newFolderPath = AssetDatabase.GetAssetPath(outputFolder);
                //CreateNecessaryFolders();
                AssetDatabase.Refresh();
    
                rootPrefab = CopyRootPrefab();
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
                materialsCopyPath = AssetDatabase.GetAssetPath(outputFolder);
                CopyMaterialsFromMeshRender(rootPrefab);
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
                FxMaterialsCopyPath = AssetDatabase.GetAssetPath(outputFolder);
                CopyMaterialsFromParticle(rootPrefab);
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
                FxModelCopyPath = AssetDatabase.GetAssetPath(outputFolder);
                CopyMeshFromParticle(rootPrefab);
                currentStatus = "粒子网格复制完成";
            }

            EditorGUILayout.Space();

            // 步骤5: 处理 MeshFilter 网格
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
                FxModelCopyPath = AssetDatabase.GetAssetPath(outputFolder);
                CopyMeshFromMeshFilter(rootPrefab);
                currentStatus = "MeshFilter 网格复制完成";
            }

            EditorGUILayout.EndVertical();
    
            EditorGUILayout.Space();
    
            // 显示复制后的预制体引用
            
            EditorGUILayout.BeginVertical("box");
            
            //EditorGUILayout.LabelField("复制后的预制体:");
            rootPrefabCopy = (GameObject)EditorGUILayout.ObjectField("复制后的预制体:", rootPrefabCopy, typeof(GameObject), false);
            
            if (GUILayout.Button("保存当前处理预制体"))
            {
                if (rootPrefab != null)
                {
                    if(PrefabUtility.IsAnyPrefabInstanceRoot(rootPrefab))
                    {
                        ApplyModificationsRecursively(rootPrefab.transform);
                        currentStatus = "保存成功";
                        EditorUtility.DisplayDialog("", "保存成功!", "确定");
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("错误", "当前预制体已解包！保存失败", "确定");
                        
                    }
                       
                }
                else
                {
                    EditorUtility.DisplayDialog("错误", "预制体实例丢失！保存失败", "确定");
                    
                }
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
            EditorGUILayout.LabelField("当前状态", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(currentStatus, MessageType.Info);
            EditorGUILayout.EndVertical();
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
                        bool isExcluded = false;
                        foreach (string excludePath in excludePaths)
                        {
                            if (path.StartsWith(excludePath))
                            {
                                isExcluded = true;
                                break;
                            }
                        }
                        
                        if (isExcluded)
                        {
                            continue; // 跳过排除目录
                        }
                        if (srcObj == null)
                        {
                            Debug.LogWarning($"[预制体复制] 无法获取 '{child.name}' 的原始预制体");
                            continue;
                        }
    
                        Debug.Log($"[预制体复制] 正在处理子预制体: {srcObj.name}");
    
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
            
            EditorUtility.DisplayProgressBar("处理中", "正在复制材质...", 0.3f);
    
            try
            {
                CopyMaterialFromRenderer(meshRenderers, materialsCopyPath);
                CopyMaterialFromRenderer(skinnedMeshRenderers, materialsCopyPath);
    
                //应用预制体中的修改
                //ApplyModificationsRecursively (instance.transform);
                //ApplyRootPrefab(rootPrefabCopy);
                //PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
    
                Debug.Log($"[材质复制] 预制体路径: {AssetDatabase.GetAssetPath(rootPrefabCopy)}");
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
        private void CopyMaterialFromRenderer(Renderer[] renderers, string copyPath)
        {
            // 跨渲染器共享 GUID 去重表，确保同一材质只复制一次
            List<string> sourceMatIDs = new List<string>();
            List<string> excludePaths = BuildExcludePaths("材质复制");

            foreach (var render in renderers)
            {
                if (render == null) continue;

                CopyAndReplaceRendererMaterials(render, copyPath, sourceMatIDs, excludePaths, "材质复制");
                AssetDatabase.Refresh();
            }
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
            List<string> sourceMatIDs = new List<string>();
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
                            renderer.sharedMaterial, FxMaterialsCopyPath, sourceMatIDs, excludePaths, "粒子材质");
                    }

                    // 粒子系统内置拖尾(trails 模块)材质
                    if (particle.trails.enabled)
                    {
                        renderer.trailMaterial = CopySingleMaterialIfNeeded(
                            renderer.trailMaterial, FxMaterialsCopyPath, sourceMatIDs, excludePaths, "粒子材质");
                    }

                    AssetDatabase.Refresh();
                }

                // ===== 2. 处理独立 TrailRenderer 组件的材质 =====
                // 注意：这里是独立 TrailRenderer 组件，与上面 ParticleSystem.trails 内置拖尾不同
                var trailRenderers = instance.GetComponentsInChildren<TrailRenderer>(true);
                Debug.Log($"[粒子材质] 检测到 TrailRenderer 数量: {trailRenderers.Length}");
                foreach (TrailRenderer trailRenderer in trailRenderers)
                {
                    CopyAndReplaceRendererMaterials(trailRenderer, FxMaterialsCopyPath, sourceMatIDs, excludePaths, "Trail材质");
                    AssetDatabase.Refresh();
                }
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
        private Material CopySingleMaterialIfNeeded(Material sourceMat, string copyPath, List<string> sourceMatIDs, List<string> excludePaths, string logTag)
        {
            if (sourceMat == null) return null;

            string path = AssetDatabase.GetAssetPath(sourceMat);

            // 仅处理外部 .mat 材质，内置材质保持原引用
            if (Path.GetExtension(path) != ".mat")
            {
                return sourceMat;
            }

            // 排除目录检查
            foreach (string excludePath in excludePaths)
            {
                if (path.StartsWith(excludePath))
                {
                    return sourceMat;
                }
            }

            string id = AssetDatabase.AssetPathToGUID(path);
            string copyFilePath;

            if (sourceMatIDs.Contains(id))
            {
                // 已经复制过该材质，直接复用副本
                copyFilePath = GenerateCopyPath(sourceMat, copyPath);
                if (!string.IsNullOrEmpty(copyFilePath))
                {
                    Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                    return copiedMat != null ? copiedMat : sourceMat;
                }
                return sourceMat;
            }

            sourceMatIDs.Add(id);
            copyFilePath = CopyAssets(sourceMat, copyPath);
            AssetDatabase.Refresh();

            if (!string.IsNullOrEmpty(copyFilePath))
            {
                Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                Debug.Log($"[{logTag}] 已复制材质: {sourceMat.name} -> {copyFilePath}");
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
        private void CopyAndReplaceRendererMaterials(Renderer renderer, string copyPath, List<string> sourceMatIDs, List<string> excludePaths, string logTag)
        {
            if (renderer == null) return;

            Material[] sharedMats = renderer.sharedMaterials;
            if (sharedMats == null || sharedMats.Length == 0) return;

            Material[] updateList = new Material[sharedMats.Length];

            for (int i = 0; i < sharedMats.Length; i++)
            {
                updateList[i] = CopySingleMaterialIfNeeded(sharedMats[i], copyPath, sourceMatIDs, excludePaths, logTag);
            }

            renderer.sharedMaterials = updateList;
            Debug.Log($"[{logTag}] 已处理 Renderer: {renderer.name}, 材质槽数: {sharedMats.Length}");
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
            List<string> sourceMeshIDs = new List<string>();
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
                    bool isExcluded = false;
                    foreach (string excludePath in excludePaths)
                    {
                        if (meshPath.StartsWith(excludePath))
                        {
                            isExcluded = true;
                            break;
                        }
                    }
                    
                    if (isExcluded)
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
                  
                    AssetDatabase.Refresh();
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
            List<string> sourceMeshIDs = new List<string>();
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
                    bool isExcluded = false;
                    foreach (string excludePath in excludePaths)
                    {
                        if (meshPath.StartsWith(excludePath))
                        {
                            isExcluded = true;
                            break;
                        }
                    }
                    if (isExcluded) continue;

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
                        AssetDatabase.Refresh();
                    }

                    if (!string.IsNullOrEmpty(copyFilePath))
                    {
                        ReplaceMeshFilterMesh(filter, copyFilePath);
                    }

                    AssetDatabase.Refresh();
                }

                if (skippedModelPrefab > 0)
                {
                    Debug.Log($"[MeshFilter网格] 已安全跳过嵌套模型预制体内部节点 {skippedModelPrefab} 个" +
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
                Debug.Log($"[MeshFilter网格] 网格替换成功: {filter.name} -> {meshPath}");
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
                        Debug.Log($"[MeshFilter网格] 从模型中匹配网格成功: {filter.name} -> {f.sharedMesh.name}");
                        return;
                    }
                }
                Debug.LogWarning($"[MeshFilter网格] 模型 {meshPath} 中未找到与 '{originalMesh?.name}' 同名的子 mesh");
            }
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

            // 生成格式：目标目录/资源名_Copy.扩展名
            string copyPath = parentFolder + "/" + asset.name + "_Copy" + Path.GetExtension(assetPath);
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
                Debug.Log($"[资源复制] 文件已存在，跳过: {targetPath}");
                return targetPath;
            }
    
            // 复制资源
            bool success = AssetDatabase.CopyAsset(assetPath, targetPath);
            if (success)
            {
                Debug.Log($"[资源复制] 复制成功: {targetPath}");
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
            Debug.Log($"[GUID替换] 处理文件: {fullPath}");
    
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
                    Debug.Log($"[{logTag}] 已添加排除目录: {excludePath}");
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
                    Debug.Log($"[粒子网格] 网格替换成功: {particle.name}");
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
                            Debug.Log($"[粒子网格] 从模型中匹配网格成功: {filterMesh.name}");
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
                Debug.Log($"[预制体保存] 已应用并解包: {parent.name}");
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
                        Debug.Log($"[预制体保存] 递归处理子预制体: {child.name}");
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
