using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    public class BatchChangeModelImport : EditorWindow,IToolGUI
    {
        private List<string> selectedFBXGUIDs = new List<string>();
        private ImportMode importMode;
        private AvatarDefinition avatarDefinition;
        private float animationRotationError = 0.0f;
        private float animationPositionError = 0.0f;
        private float animationScaleError = 0.0f;
        private Vector2 scrollPosition;
    
        // [MenuItem("BjTools/Batch Change Model Import")]
        // public static void ShowWindow()
        // {
        //     GetWindow<BatchChangeModelImport>("Batch Change Model Import");
        // }
    
        public void DrawGUI()
        {
            EditorGUILayout.Space(10);
            GUILayout.Label("批量修改模型导入设置", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);
            selectedFBXGUIDs = new List<string>(Selection.assetGUIDs);
            //scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(300));
            if (selectedFBXGUIDs.Count > 0)
            {
                foreach (string obj in selectedFBXGUIDs)
                {
                    string name = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(obj)).name;
    
                    if (!string.IsNullOrEmpty(name))
                    {
                        GUILayout.Label(name);
                    }
                }
            }
           
            //EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();
            GUILayout.Label("Material Import Mode :");
            importMode = (ImportMode)EditorGUILayout.EnumPopup(importMode);
    
            animationRotationError = EditorGUILayout.FloatField("Animation Rotation Error:", animationRotationError);
            animationPositionError = EditorGUILayout.FloatField("Animation Position Error:", animationPositionError);
            animationScaleError = EditorGUILayout.FloatField("Animation Scale Error:", animationScaleError);
    
            avatarDefinition = (AvatarDefinition)EditorGUILayout.EnumPopup(avatarDefinition);
    
            EditorGUILayout.Space();
            if (GUILayout.Button("应用"))
            {
                ChangeImportMode();
            }
    
    
        }
    
        private void ChangeImportMode()
        {
    
            foreach (string id in selectedFBXGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(id);
                ModelImporter modelImporter = AssetImporter.GetAtPath(path) as ModelImporter;
                //GameObject obj = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(o));
                if (modelImporter != null)
                {
                    SerializedObject so = new SerializedObject(modelImporter);
                    SerializedProperty s_ImportMode = so.FindProperty("m_MaterialImportMode");
                    SerializedProperty s_RotationError = so.FindProperty("m_AnimationRotationError");
                    SerializedProperty s_PositionError = so.FindProperty("m_AnimationPositionError");
                    SerializedProperty s_ScaleError = so.FindProperty("m_AnimationScaleError");
                    SerializedProperty s_AvatarSetup = so.FindProperty("m_AvatarSetup");
                    
                    s_ImportMode.intValue = (int)importMode;
                    s_RotationError.floatValue = animationRotationError;
                    s_PositionError.floatValue = animationPositionError;
                    s_ScaleError.floatValue = animationScaleError;
                    s_AvatarSetup.intValue = (int)avatarDefinition;
                    so.ApplyModifiedProperties();
                    modelImporter.SaveAndReimport();
                    Debug.Log("Material Import Mode: " + s_ImportMode.intValue);
                }
                else
                {
                    Debug.LogError("Model Importer is null!");
                }
            }
        }
    
    }
    public enum ImportMode
    {
        None = 0, Standard = 1, ImportviaMaterialDescription = 2,
    }
    public enum AvatarDefinition
    {
        NoAvatar = 0,CreateFromThisModel = 1,CopyFromOtherAvatar = 2,
    }
}



