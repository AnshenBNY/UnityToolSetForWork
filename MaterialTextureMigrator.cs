using System;
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ToolSet
{
    public class MaterialTextureMigrator : EditorWindow,IToolGUI
    {
        private DefaultAsset sourceMaterialsFolder;
        private DefaultAsset targetTexturesFolder;
        private List<DefaultAsset> excludeFolders = new List<DefaultAsset>();
        private Vector2 scrollPosition;
        private Vector2 excludeScrollPosition;
        private List<string> processedMaterials = new List<string>();
        private List<string> copiedTextures = new List<string>();
        private List<string> skippedTextures = new List<string>();
        private List<string> failedMaterials = new List<string>();
        
        // 用于跟踪已处理的贴图UID和对应的复制路径
        private Dictionary<string, string> textureGuidToCopiedPath = new Dictionary<string, string>();
    
        // [MenuItem("BjTools/Material Texture Migrator")]
        // public static void ShowWindow()
        // {
        //     GetWindow<MaterialTextureMigrator>("Material Texture Migrator");
        // }
    
        // private void OnGUI()
        // {
        //     DrawGUI();
        // }
    
        public void DrawGUI()
        {
            EditorGUILayout.Space(10);
            GUILayout.Label("材质贴图复制迁移工具", EditorStyles.boldLabel);
            
            EditorGUILayout.Space(10);
            sourceMaterialsFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                "目标材质文件夹", 
                sourceMaterialsFolder, 
                typeof(DefaultAsset), 
                false
            );
            
            targetTexturesFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                "贴图输出文件夹", 
                targetTexturesFolder, 
                typeof(DefaultAsset), 
                false
            );
            if (GUILayout.Button("添加排除目录"))
            {
                excludeFolders.Add(null);
            }
            EditorGUILayout.Space();
            GUILayout.Label("添加排除目录 （以下文件夹中的贴图不会被复制）:", EditorStyles.boldLabel);
            
            // 显示排除目录列表
            excludeScrollPosition = EditorGUILayout.BeginScrollView(excludeScrollPosition, GUILayout.Height(100));
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
            
            if (GUILayout.Button("复制并迁移贴图"))
            {
                MigrateTextures();
            }
            
            EditorGUILayout.Space();
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            if (processedMaterials.Count > 0 || skippedTextures.Count > 0 || failedMaterials.Count > 0)
            {
                GUILayout.Label("Process Results:", EditorStyles.boldLabel);
                
                GUILayout.Label($"Processed Materials ({processedMaterials.Count}):");
                foreach (var material in processedMaterials)
                {
                    EditorGUILayout.LabelField(material);
                }
                
                EditorGUILayout.Space();
                GUILayout.Label($"Copied Textures ({copiedTextures.Count}):");
                foreach (var texture in copiedTextures)
                {
                    EditorGUILayout.LabelField(texture);
                }
                
                EditorGUILayout.Space();
                GUILayout.Label($"Skipped Textures ({skippedTextures.Count}):");
                foreach (var texture in skippedTextures)
                {
                    EditorGUILayout.LabelField(texture);
                }
                
                EditorGUILayout.Space();
                GUILayout.Label($"Failed Materials ({failedMaterials.Count}):");
                foreach (var material in failedMaterials)
                {
                    EditorGUILayout.LabelField(material);
                }
                
                EditorGUILayout.Space();
                if (GUILayout.Button("Clear Results"))
                {
                    processedMaterials.Clear();
                    copiedTextures.Clear();
                    skippedTextures.Clear();
                    failedMaterials.Clear();
                }
            }
            
            EditorGUILayout.EndScrollView();
        }
    
        private void MigrateTextures()
        {
            processedMaterials.Clear();
            copiedTextures.Clear();
            skippedTextures.Clear();
            failedMaterials.Clear();
            textureGuidToCopiedPath.Clear(); // 清空UID映射
            
            string sourcePath = AssetDatabase.GetAssetPath(sourceMaterialsFolder);
            string targetPath = AssetDatabase.GetAssetPath(targetTexturesFolder);
            
            if (!AssetDatabase.IsValidFolder(sourcePath))
            {
                Debug.LogError("Source is not a valid folder: " + sourcePath);
                EditorUtility.DisplayDialog("Error", "Source is not a valid folder", "OK");
                return;
            }
            
            if (!AssetDatabase.IsValidFolder(targetPath))
            {
                Debug.LogError("Target is not a valid folder: " + targetPath);
                EditorUtility.DisplayDialog("Error", "Target is not a valid folder", "OK");
                return;
            }
            
            // 获取所有排除目录的路径
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
            
            // 获取所有材质文件
            string[] materialFiles = Directory.GetFiles(sourcePath, "*.mat", SearchOption.AllDirectories);
            
            if (materialFiles.Length == 0)
            {
                Debug.LogWarning("No material files found in source folder");
                EditorUtility.DisplayDialog("Info", "No material files found in source folder", "OK");
                return;
            }
            
            int processedCount = 0;
            int errorCount = 0;
            
            // 显示进度条
            EditorUtility.DisplayProgressBar("Processing", "Migrating textures...", 0);
            
            try
            {
                // 第一步：收集所有需要复制的贴图信息
                List<TextureCopyInfo> texturesToCopy = new List<TextureCopyInfo>();
                Dictionary<string, Material> materialsToProcess = new Dictionary<string, Material>();
                
                for (int i = 0; i < materialFiles.Length; i++)
                {
                    string materialFile = materialFiles[i];
                    string relativePath = materialFile.Replace(Application.dataPath, "Assets");
                    Material material = AssetDatabase.LoadAssetAtPath<Material>(relativePath);
                    
                    EditorUtility.DisplayProgressBar("Collecting Textures", $"Processing material: {material.name}", (float)i / materialFiles.Length);
                    
                    if (material == null)
                    {
                        Debug.LogWarning("Failed to load material: " + relativePath);
                        errorCount++;
                        continue;
                    }
                    
                    // 收集材质中的贴图信息
                    CollectTexturesFromMaterial(material, targetPath, excludePaths, texturesToCopy);
                    materialsToProcess[relativePath] = material;
                }
                
                // 第二步：批量复制所有贴图
                AssetDatabase.StartAssetEditing();
                
                foreach (var textureInfo in texturesToCopy)
                {
                    EditorUtility.DisplayProgressBar("Copying Textures", $"Copying texture: {textureInfo.originalTexture.name}", 0.5f);
                    
                    if (CopyTexture(textureInfo))
                    {
                        copiedTextures.Add($"{textureInfo.originalPath} -> {textureInfo.targetPath}");
                    }
                }
                
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh(); // 确保所有复制的贴图都被导入
                
                // 第三步：更新材质引用
                AssetDatabase.StartAssetEditing();
                
                int materialIndex = 0;
                foreach (var kvp in materialsToProcess)
                {
                    string relativePath = kvp.Key;
                    Material material = kvp.Value;
                    
                    EditorUtility.DisplayProgressBar("Updating Materials", $"Updating material: {material.name}", 0.5f + (float)materialIndex / materialsToProcess.Count * 0.5f);
                    
                    if (UpdateMaterialTextures(material, targetPath, excludePaths))
                    {
                        processedMaterials.Add(relativePath);
                        processedCount++;
                    }
                    else
                    {
                        failedMaterials.Add($"{relativePath} ({material.name})");
                        errorCount++;
                    }
                    
                    materialIndex++;
                }
                
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }
            finally
            {
                AssetDatabase.Refresh();
                EditorUtility.ClearProgressBar();
            }
            
            Debug.Log($"Migration completed. Processed: {processedCount}, Errors: {errorCount}");
            EditorUtility.DisplayDialog("Complete", $"Migration completed. Processed: {processedCount}, Errors: {errorCount}", "OK");
        }
    
        private void CollectTexturesFromMaterial(Material material, string targetFolderPath, List<string> excludePaths, List<TextureCopyInfo> texturesToCopy)
        {
            Shader shader = material.shader;
            
            if (shader == null)
            {
                Debug.LogWarning($"Material {material.name} has no shader assigned");
                return;
            }
            
            // 使用SerializedObject获取所有纹理属性
            SerializedObject so = new SerializedObject(material);
            SerializedProperty texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            
            if (texEnvs != null && texEnvs.arraySize > 0)
            {
                for (int i = 0; i < texEnvs.arraySize; i++)
                {
                    SerializedProperty textureProperty = texEnvs.GetArrayElementAtIndex(i);
                    SerializedProperty textureName = textureProperty.FindPropertyRelative("first");
                    Debug.Log($"Texture Name is : {textureName}");
                    SerializedProperty textureValue = textureProperty.FindPropertyRelative("second.m_Texture");
                    
                    if (textureValue != null && textureValue.objectReferenceValue != null)
                    {
                        Texture texture = textureValue.objectReferenceValue as Texture;
                        
                        if (texture != null)
                        {
                            string texturePath = AssetDatabase.GetAssetPath(texture);
                            
                            // 检查贴图是否在排除目录中
                            bool isExcluded = false;
                            foreach (string excludePath in excludePaths)
                            {
                                if (texturePath.StartsWith(excludePath))
                                {
                                    skippedTextures.Add($"{texture.name} (in exclude folder: {excludePath})");
                                    isExcluded = true;
                                    break;
                                }
                            }
                            
                            if (isExcluded)
                            {
                                continue; // 跳过排除目录中的贴图
                            }
                            
                            // 获取贴图的GUID
                            string textureGuid = AssetDatabase.AssetPathToGUID(texturePath);
                            
                            // 如果贴图不在目标文件夹中，则添加到复制列表
                            if (!texturePath.StartsWith(targetFolderPath))
                            {
                                // 检查是否已经处理过相同GUID的贴图
                                if (!textureGuidToCopiedPath.ContainsKey(textureGuid))
                                {
                                    string fileName = Path.GetFileName(texturePath);
                                    string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                                    string fileExtension = Path.GetExtension(fileName);
                                    string targetPath = Path.Combine(targetFolderPath, fileName).Replace("\\", "/");
                                    
                                    // 检查目标路径是否已存在相同文件
                                    Texture existingTexture = AssetDatabase.LoadAssetAtPath<Texture>(targetPath);
                                    
                                    // 如果存在同名文件，检查是否是我们需要的贴图
                                    if (existingTexture != null)
                                    {
                                        string existingTexturePath = AssetDatabase.GetAssetPath(existingTexture);
                                        string existingTextureGuid = AssetDatabase.AssetPathToGUID(existingTexturePath);
                                        
                                        // 如果是不同的贴图（不同GUID），需要重命名
                                        if (existingTextureGuid != textureGuid)
                                        {
                                            // 尝试添加后缀直到找到不冲突的文件名
                                            int counter = 1;
                                            string newFileName;
                                            string newTargetPath;
                                            
                                            do
                                            {
                                                newFileName = $"{fileNameWithoutExt}_{counter}{fileExtension}";
                                                newTargetPath = Path.Combine(targetFolderPath, newFileName).Replace("\\", "/");
                                                counter++;
                                            }
                                            while (File.Exists(newTargetPath) || AssetDatabase.LoadAssetAtPath<Texture>(newTargetPath) != null);
                                            
                                            targetPath = newTargetPath;
                                        }
                                    }
                                    
                                    // 添加到复制列表
                                    texturesToCopy.Add(new TextureCopyInfo
                                    {
                                        originalTexture = texture,
                                        originalPath = texturePath,
                                        targetPath = targetPath,
                                        textureGuid = textureGuid
                                    });
                                    
                                    // 记录GUID到复制路径的映射
                                    textureGuidToCopiedPath[textureGuid] = targetPath;
                                }
                            }
                            else
                            {
                                Debug.Log($"Texture {texture.name} is already in target folder, skipping");
                                skippedTextures.Add($"{texture.name} (already in target folder)");
                            }
                        }
                    }
                }
            }
        }
    
        private bool CopyTexture(TextureCopyInfo textureInfo)
        {
            // 检查目标路径是否已存在相同文件
            if (File.Exists(textureInfo.targetPath))
            {
                Debug.Log($"Texture already exists at target location, skipping: {textureInfo.targetPath}");
                return true;
            }
            
            // 复制文件
            bool success = AssetDatabase.CopyAsset(textureInfo.originalPath, textureInfo.targetPath);
            
            if (!success)
            {
                Debug.LogError($"Failed to copy texture from {textureInfo.originalPath} to {textureInfo.targetPath}");
                return false;
            }
            
            return true;
        }
    
        private bool UpdateMaterialTextures(Material material, string targetFolderPath, List<string> excludePaths)
        {
            bool materialModified = false;
            Shader shader = material.shader;
            
            if (shader == null)
            {
                Debug.LogWarning($"Material {material.name} has no shader assigned");
                return false;
            }
            
            // 使用SerializedObject获取所有纹理属性
            SerializedObject so = new SerializedObject(material);
            SerializedProperty texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            
            if (texEnvs != null && texEnvs.arraySize > 0)
            {
                for (int i = 0; i < texEnvs.arraySize; i++)
                {
                    SerializedProperty textureProperty = texEnvs.GetArrayElementAtIndex(i);
                    SerializedProperty textureName = textureProperty.FindPropertyRelative("first");
                    SerializedProperty textureValue = textureProperty.FindPropertyRelative("second.m_Texture");
                    
                    if (textureValue != null && textureValue.objectReferenceValue != null)
                    {
                        Texture texture = textureValue.objectReferenceValue as Texture;
                        
                        if (texture != null)
                        {
                            string texturePath = AssetDatabase.GetAssetPath(texture);
                            
                            // 检查贴图是否在排除目录中
                            bool isExcluded = false;
                            foreach (string excludePath in excludePaths)
                            {
                                if (texturePath.StartsWith(excludePath))
                                {
                                    isExcluded = true;
                                    break;
                                }
                            }
                            
                            if (isExcluded)
                            {
                                continue; // 跳过排除目录中的贴图
                            }
                            
                            // 获取贴图的GUID
                            string textureGuid = AssetDatabase.AssetPathToGUID(texturePath);
                            
                            // 如果贴图不在目标文件夹中，则进行替换
                            if (!texturePath.StartsWith(targetFolderPath))
                            {
                                // 检查是否已经处理过相同GUID的贴图
                                if (textureGuidToCopiedPath.ContainsKey(textureGuid))
                                {
                                    // 加载已复制的贴图
                                    Texture copiedTexture = AssetDatabase.LoadAssetAtPath<Texture>(textureGuidToCopiedPath[textureGuid]);
                                    
                                    if (copiedTexture != null)
                                    {
                                        textureValue.objectReferenceValue = copiedTexture;
                                        materialModified = true;
                                        Debug.Log($"Using copied texture for {texture.name} in material {material.name}");
                                    }
                                    else
                                    {
                                        Debug.LogError($"Failed to load copied texture at {textureGuidToCopiedPath[textureGuid]}");
                                    }
                                }
                            }
                        }
                    }
                }
                
                if (materialModified)
                {
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(material);
                }
            }
            
            return materialModified;
        }
    
        // 辅助类：存储贴图复制信息
        private class TextureCopyInfo
        {
            public Texture originalTexture;
            public string originalPath;
            public string targetPath;
            public string textureGuid;
        }
    }
}
