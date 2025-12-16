using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace ToolSet
{
    public class PrefabCopyTool : EditorWindow,IToolGUI
    {
        private GameObject rootPrefab;
        private GameObject rootPrefabCopyIns;
        private GameObject rootPrefabCopy;
        private GameObject matPrefab;
        private GameObject fxMeshPrefab;
        private GameObject fxMatPrefab;
        
            
        private string rootPrefabCopyPath;
        private List<DefaultAsset> excludeFolders = new List<DefaultAsset>();
        private Vector2 excludeScrollPosition;
        private DefaultAsset outputFolder;
        
    
        private string newFolderPath = "Assets/PrefabCopies";
        private string materialsCopyPath = "Assets/PrefabCopies/Materials";
        private string texturesCopyPath = "Assets/PrefabCopies/Textures";
        private string FxCopyPath = "Assets/PrefabCopies/FX";
        private string FxMaterialsCopyPath = "Assets/PrefabCopies/FX/Materials";
        private string FxTexturesCopyPath = "Assets/PrefabCopies/FX/Textures";
        private string FxModelCopyPath = "Assets/PrefabCopies/FX/Models";
        
    
        // 添加进度显示
        private string currentStatus = "等待开始";
        private Vector2 scrollPosition;
    
        // [MenuItem("BjTools/预制体复制工具")]
        // public static void ShowWindow()
        // {
        //     GetWindow<PrefabCopyTool>("PrefabCopyTool");
        // }
        // private void OnGUI()
        // {
        //     DrawGUI();
        // }

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
            excludeScrollPosition = EditorGUILayout.BeginScrollView(excludeScrollPosition, GUILayout.Height(100));
            if (GUILayout.Button("添加排除目录"))
            {
                excludeFolders.Add(null);
            }
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
        private string CopyChildPrefabs(GameObject prefabRoot)
        {
            
            Dictionary<string, string> prefabUIDs = new Dictionary<string, string>();
            AssetDatabase.Refresh();
            //添加过滤目录
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log("Added exclude path: " + excludePath);
                    }
                }
            }
            // 显示进度条
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
                            Debug.LogWarning($"无法获取 {child.name} 的原始预制体");
                            continue;
                        }
    
                        Debug.Log("SrcObj name is :" + srcObj.name);
    
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
        private void CopyMaterialsFromMeshRender(GameObject instance)
        {
           
            var meshRenderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            var skinnedMeshRenderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            // 显示进度条
            EditorUtility.DisplayProgressBar("处理中", "正在复制材质...", 0.3f);
    
            try
            {
                CopyMaterialFromRenderer(meshRenderers, materialsCopyPath);
                CopyMaterialFromRenderer(skinnedMeshRenderers, materialsCopyPath);
    
                //应用预制体中的修改
                //ApplyModificationsRecursively (instance.transform);
                //ApplyRootPrefab(rootPrefabCopy);
                //PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
    
                Debug.Log(AssetDatabase.GetAssetPath(rootPrefabCopy));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
        private void CopyMaterialFromRenderer(Renderer[] renderers, string copyPath)
        {
            List<string> sourceMatIDs = new List<string>();
            string copyFilePath = "";
            //添加过滤目录
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log("Added exclude path: " + excludePath);
                    }
                }
            }
    
            foreach (var render in renderers)
            {
                if (render == null) continue;
    
                Debug.Log(">>>Mesh: " + render.name);
                Debug.Log(">>>SharedMaterials Count: " + render.sharedMaterials.Length);
                Material[] updateList = new Material[render.sharedMaterials.Length];
    
                for (int i = 0; i < render.sharedMaterials.Length; i++)
                {
                    if (render.sharedMaterials[i] == null)
                    {
                        updateList[i] = null;
                        continue;
                    }
    
                    string path = AssetDatabase.GetAssetPath(render.sharedMaterials[i]);
                    Debug.Log(">>>SharedMaterial path : " + path);
                    
                    if (Path.GetExtension(path) == ".mat")//判断是否是内置材质
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
                render.sharedMaterials = updateList;
            }
        }
        private void CopyMaterialsFromParticle(GameObject instance)
        {
            
            var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
            List<string> sourceMatIDs = new List<string>();
            string copyFilePath = "";
    
            // 显示进度条
            EditorUtility.DisplayProgressBar("处理中", "正在复制粒子材质...", 0.6f);
            //添加过滤目录
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log("Added exclude path: " + excludePath);
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
        private void CopyMeshFromParticle(GameObject instance)
        {
            
            var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
    
            List<string> sourceMeshIDs = new List<string>();
            //添加过滤目录
            List<string> excludePaths = new List<string>();
            foreach (var excludeFolder in excludeFolders)
            {
                if (excludeFolder != null)
                {
                    string excludePath = AssetDatabase.GetAssetPath(excludeFolder);
                    if (AssetDatabase.IsValidFolder(excludePath))
                    {
                        excludePaths.Add(excludePath);
                        Debug.Log("Added exclude path: " + excludePath);
                    }
                }
            }
    
            string copyFilePath = "";
            // 显示进度条
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
        private string GenerateCopyPath(Object asset, string parentFolder)
        {
            if (asset == null)
            {
                Debug.LogError("GenerateCopyPath: Asset is null!");
                return null;
            }
    
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError($"GenerateCopyPath: Cannot get path for asset {asset.name}");
                return null;
            }
    
            string copyPath = parentFolder + "/" + asset.name + "_Copy" + Path.GetExtension(assetPath);
            return copyPath;
        }
        private string CopyAssets(Object asset, string parentFolder)
        {
            if (asset == null)
            {
                Debug.LogError("CopyAssets: MeshAsset is null!");
                return null;
            }
    
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError($"CopyAssets: Cannot get path for asset {asset.name}");
                return null;
            }
    
            // 定义新资源的路径
            string targetPath = GenerateCopyPath(asset, parentFolder);
            if (string.IsNullOrEmpty(targetPath))
            {
                return null;
            }
    
            string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + targetPath;
            Debug.Log("The fullpath is :" + fullPath);
    
            // 检查目标路径是否已存在 - 如果存在则跳过复制
            if (File.Exists(fullPath))
            {
                Debug.Log($"文件已存在，跳过复制: {targetPath}");
                return targetPath;
            }
    
            // 复制资源
            bool success = AssetDatabase.CopyAsset(assetPath, targetPath);
            if (success)
            {
                Debug.Log($"Successfully copied asset to {targetPath}");
                return targetPath;
            }
            else
            {
                Debug.LogError($"Failed to copy asset from {assetPath} to {targetPath}");
                return null;
            }
        }
        private string ReplaceUID(GameObject prefab, Dictionary<string, string> idDictionary)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("ReplaceUID: Cannot get path for prefab");
                return null;
            }
    
            string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + path;
            Debug.Log(fullPath);
    
            if (!File.Exists(fullPath))
            {
                Debug.LogError($"ReplaceUID: File does not exist at {fullPath}");
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
        private string GetAssetGuid(Object obj)
        {
            if (obj == null) return "";
    
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return "";
            return AssetDatabase.AssetPathToGUID(path);
        }
        private string FindMeshInParticleSystem(ParticleSystem particleSystem)
        {
            Mesh foundMesh = null;
            string sourceAssetPath = null;
    
            var renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
            {
                Debug.LogError("No ParticleSystemRenderer found on the selected ParticleSystem");
                return null;
            }
    
            // 获取Renderer使用的Mesh
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh)
            {
                foundMesh = renderer.mesh;
            }
    
            if (foundMesh == null)
            {
                Debug.LogWarning("No mesh found in ParticleSystemRenderer");
                return null;
            }
    
            // 查找Mesh的源文件路径
            sourceAssetPath = AssetDatabase.GetAssetPath(foundMesh);
    
           
            if (string.IsNullOrEmpty(sourceAssetPath))
            {
                Debug.LogWarning("Get mesh asset path failed");
                return null;
            }
    
            return sourceAssetPath;
        }
        private void ReplaceParticleMesh(ParticleSystem particle, string meshPath)
        {
            string type = Path.GetExtension(meshPath);
            var renderer = particle.GetComponent<ParticleSystemRenderer>();
           
            if (renderer == null)
            {
                Debug.LogError("Renderer is null!");
                return;
            }
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh)
            {
                var mesh = AssetDatabase.LoadAssetAtPath(meshPath, typeof(Object));
                var foundMesh = renderer.mesh;
                if(mesh is Mesh)
                {
                    renderer.mesh = (Mesh)mesh;
                    Debug.Log("Sucess");
                }
                else
                {
                    var model = mesh as GameObject;
                    var filters =  model.GetComponentsInChildren<MeshFilter>();
                    if (filters == null)
                    {
                        Debug.LogError("fliters is null!");
                        return;
                    }  
                    foreach (var filter in filters)
                    {
                        var filterMesh = filter.sharedMesh;
                        Debug.Log($"Mesh name is : {filterMesh.name}");
                        if (filterMesh.name == foundMesh.name)
                        {
                            renderer.mesh = filterMesh;
                            Debug.Log("Sucess");
                        }
                    }
    
                }
               
            }
    
        }
        private void ApplyModificationsRecursively(Transform parent)
        {
            if(PrefabUtility.IsAnyPrefabInstanceRoot(parent.gameObject))
            {
                PrefabUtility.ApplyPrefabInstance(parent.gameObject, InteractionMode.AutomatedAction);
                PrefabUtility.UnpackPrefabInstance(parent.gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                Debug.LogWarning("Unpacked");
            }
            else
            {
                Debug.LogWarning($"***{parent.name}*** is not a prefab.");
                return;
            }
            
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject == parent.gameObject)
                    continue;
                
                Debug.Log($"Child Name : {child.name}");
                if(PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                {
                    
                    GameObject prefabRoot = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);
                    string childPath = AssetDatabase.GetAssetPath(prefabRoot);
                    Debug.Log($"Child path is : {childPath}");
                    if (Path.GetExtension(childPath) == ".prefab")
                    {   
                        
                        Debug.Log($"Prefab Name : {child.name}");
                        ApplyModificationsRecursively(child.transform);
                        
                    }
                }
                
                    
            }
        }
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
        
    }
}
//using UnityEditor.SearchService;
