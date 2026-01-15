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
    /// 2. 复制 MeshRenderer/SkinnedMeshRenderer 使用的材质
    /// 3. 复制粒子系统使用的材质（包括主材质和拖尾材质）
    /// 4. 复制粒子系统使用的网格资源
    /// 5. 支持排除指定目录的资源不被复制
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
        /// <summary>复制后的预制体实例（场景中）</summary>
        private GameObject rootPrefabCopyIns;
        /// <summary>复制后的预制体资源（Assets中）</summary>
        private GameObject rootPrefabCopy;
        /// <summary>材质预制体引用（暂未使用）</summary>
        private GameObject matPrefab;
        /// <summary>特效网格预制体引用（暂未使用）</summary>
        private GameObject fxMeshPrefab;
        /// <summary>特效材质预制体引用（暂未使用）</summary>
        private GameObject fxMatPrefab;

        // ========== 路径配置 ==========
        /// <summary>复制后的根预制体路径</summary>
        private string rootPrefabCopyPath;
        /// <summary>需要排除的文件夹列表（这些目录中的资源不会被复制）</summary>
        private List<DefaultAsset> excludeFolders = new List<DefaultAsset>();
        /// <summary>排除目录列表的滚动位置</summary>
        private Vector2 excludeScrollPosition;
        /// <summary>输出文件夹</summary>
        private DefaultAsset outputFolder;

        // ========== 默认输出路径 ==========
        /// <summary>预制体复制的默认根目录</summary>
        private string newFolderPath = "Assets/PrefabCopies";
        /// <summary>材质复制的默认目录</summary>
        private string materialsCopyPath = "Assets/PrefabCopies/Materials";
        /// <summary>贴图复制的默认目录（暂未使用）</summary>
        private string texturesCopyPath = "Assets/PrefabCopies/Textures";
        /// <summary>特效复制的默认根目录</summary>
        private string FxCopyPath = "Assets/PrefabCopies/FX";
        /// <summary>特效材质复制的默认目录</summary>
        private string FxMaterialsCopyPath = "Assets/PrefabCopies/FX/Materials";
        /// <summary>特效贴图复制的默认目录（暂未使用）</summary>
        private string FxTexturesCopyPath = "Assets/PrefabCopies/FX/Textures";
        /// <summary>特效模型/网格复制的默认目录</summary>
        private string FxModelCopyPath = "Assets/PrefabCopies/FX/Models";

        // ========== UI状态 ==========
        /// <summary>当前操作状态信息</summary>
        private string currentStatus = "等待开始";
        /// <summary>主界面滚动位置</summary>
        private Vector2 scrollPosition;
        
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
            
            // 构建排除路径列表
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log($"[预制体复制] 已添加排除目录: {excludePath}");
                    }
                }
            }
            
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
        /// 从渲染器数组复制材质的具体实现
        /// 处理逻辑：
        /// 1. 遍历每个渲染器的所有材质槽
        /// 2. 检查材质是否为.mat文件（排除内置材质）
        /// 3. 检查材质是否在排除目录中
        /// 4. 如果材质已复制过，直接使用已复制的版本
        /// 5. 否则复制材质并更新引用
        /// </summary>
        /// <param name="renderers">渲染器数组（MeshRenderer或SkinnedMeshRenderer）</param>
        /// <param name="copyPath">材质复制的目标目录</param>
        private void CopyMaterialFromRenderer(Renderer[] renderers, string copyPath)
        {
            // 记录已复制的材质GUID，避免重复复制
            List<string> sourceMatIDs = new List<string>();
            string copyFilePath = "";
            
            // 构建排除路径列表
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log($"[材质复制] 已添加排除目录: {excludePath}");
                    }
                }
            }
    
            foreach (var render in renderers)
            {
                if (render == null) continue;
    
                Debug.Log($"[材质复制] 处理渲染器: {render.name}, 材质数量: {render.sharedMaterials.Length}");
                Material[] updateList = new Material[render.sharedMaterials.Length];
    
                for (int i = 0; i < render.sharedMaterials.Length; i++)
                {
                    if (render.sharedMaterials[i] == null)
                    {
                        updateList[i] = null;
                        continue;
                    }
    
                    string path = AssetDatabase.GetAssetPath(render.sharedMaterials[i]);
                    Debug.Log($"[材质复制] 材质路径: {path}");
                    
                    if (Path.GetExtension(path) == ".mat") // 判断是否是外部材质文件（排除内置材质）
                    {
                        string id = AssetDatabase.AssetPathToGUID(path);
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
                            updateList[i] = render.sharedMaterials[i];
                            continue; // 跳过排除目录
                        }
    
                        if (sourceMatIDs.Contains(id))
                        {
                            copyFilePath = GenerateCopyPath(render.sharedMaterials[i], copyPath);
                            Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                            updateList[i] = copiedMat;
                            continue;
                        }
    
                        sourceMatIDs.Add(id);
                        copyFilePath = CopyAssets(render.sharedMaterials[i], copyPath);
                        AssetDatabase.Refresh();
    
                        if (!string.IsNullOrEmpty(copyFilePath))
                        {
                            Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                            updateList[i] = copiedMat;
                        }
                        else
                        {
                            updateList[i] = render.sharedMaterials[i];
                        }
                    }
                    else
                    {
                        updateList[i] = render.sharedMaterials[i];
                    }
                }
    
                
    
                AssetDatabase.Refresh();
                // 更新渲染器的材质引用
                render.sharedMaterials = updateList;
            }
        }
        
        /// <summary>
        /// 从粒子系统复制材质
        /// 处理内容：
        /// 1. ParticleSystemRenderer的主材质（sharedMaterial）
        /// 2. 粒子拖尾材质（trailMaterial）
        /// </summary>
        /// <param name="instance">场景中的预制体实例</param>
        private void CopyMaterialsFromParticle(GameObject instance)
        {
            // 获取所有粒子系统组件（包括子物体）
            var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
            // 记录已复制的材质GUID，避免重复复制
            List<string> sourceMatIDs = new List<string>();
            string copyFilePath = "";

            EditorUtility.DisplayProgressBar("处理中", "正在复制粒子材质...", 0.6f);
            
            // 构建排除路径列表
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log($"[粒子材质] 已添加排除目录: {excludePath}");
                    }
                }
            }
            try
            {
                foreach (ParticleSystem particle in particles)
                {
                    ParticleSystemRenderer renderer = particle.GetComponent<ParticleSystemRenderer>();
    
                    if (renderer != null)
                    {
                        // 处理主材质
                        if (renderer.sharedMaterial != null && renderer.renderMode != ParticleSystemRenderMode.None)
                        {
                            string path = AssetDatabase.GetAssetPath(renderer.sharedMaterial);
    
                            bool isExcluded = false;
                            foreach (string excludePath in excludePaths)
                            {
                                if (path.StartsWith(excludePath))
                                {
                                    isExcluded = true;
                                    break;
                                }
                            }
                            
                            if (!isExcluded)
                            {
                                string id = AssetDatabase.AssetPathToGUID(path);
    
                                if (sourceMatIDs.Contains(id))
                                {
    
                                    copyFilePath = GenerateCopyPath(renderer.sharedMaterial, FxMaterialsCopyPath);
                                    if (!string.IsNullOrEmpty(copyFilePath))
                                    {
                                        Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                                        renderer.sharedMaterial = copiedMat;
                                    }
                                    
                                    
                                }
                                else
                                {
                                    sourceMatIDs.Add(id);
                                    copyFilePath = CopyAssets(renderer.sharedMaterial, FxMaterialsCopyPath);
                                    AssetDatabase.Refresh();
    
                                    if (!string.IsNullOrEmpty(copyFilePath))
                                    {
                                        Material copiedMat = (Material)AssetDatabase.LoadAssetAtPath(copyFilePath, typeof(Material));
                                        renderer.sharedMaterial = copiedMat;
                                    }
                                }
                            }
    
                           
    
                           
                        }
    
                        // 处理Trail材质（如果有）
                        var trailModule = particle.trails;
                        if (trailModule.enabled && renderer is ParticleSystemRenderer psRenderer)
                        {
                            if (psRenderer.trailMaterial != null)
                            {
                                string trailPath = AssetDatabase.GetAssetPath(psRenderer.trailMaterial);
    
                                bool isExcluded = false;
                                foreach (string excludePath in excludePaths)
                                {
                                    if (trailPath.StartsWith(excludePath))
                                    {
                                        isExcluded = true;
                                        break;
                                    }
                                }
                                
                                if (!isExcluded)
                                {
                                    string trailId = AssetDatabase.AssetPathToGUID(trailPath);
    
                                    if (sourceMatIDs.Contains(trailId))
                                    {
                                        string trailCopyPath = GenerateCopyPath(psRenderer.trailMaterial, FxMaterialsCopyPath);
                                        if (!string.IsNullOrEmpty(trailCopyPath))
                                        {
                                            Material copiedTrailMat = (Material)AssetDatabase.LoadAssetAtPath(trailCopyPath, typeof(Material));
                                            psRenderer.trailMaterial = copiedTrailMat;
    
                                        }
    
                                    }
                                    else
                                    {
                                        sourceMatIDs.Add(trailId);
                                        string trailCopyPath = CopyAssets(psRenderer.trailMaterial, FxMaterialsCopyPath);
                                        AssetDatabase.Refresh();
    
                                        if (!string.IsNullOrEmpty(trailCopyPath))
                                        {
                                            Material copiedTrailMat = (Material)AssetDatabase.LoadAssetAtPath(trailCopyPath, typeof(Material));
                                            psRenderer.trailMaterial = copiedTrailMat;
                                            
                                        }
    
                                    } 
                                }
    
                                
                            }
                        }
                    }
    
                    AssetDatabase.Refresh();
                }
                
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
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
            
            // 构建排除路径列表
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log($"[粒子网格] 已添加排除目录: {excludePath}");
                    }
                }
            }
    
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
