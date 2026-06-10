using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    public class BatchChangeModelImport : EditorWindow, IToolGUI
    {
        private List<string> selectedFBXGUIDs = new List<string>();
        private ImportMode importMode = ImportMode.Standard;
        private AvatarDefinition avatarDefinition = AvatarDefinition.NoAvatar;
        private float animationRotationError = 0.5f;
        private float animationPositionError = 0.5f;
        private float animationScaleError = 0.5f;
        private bool applyMaterialImportMode = true;
        private bool applyAnimationError;
        private bool applyAvatarDefinition;
        private string statusMessage = "等待操作";
        private Vector2 scrollPosition;

        public void DrawGUI()
        {
            ToolUi.DrawToolHeader("批量修改模型导入设置", "分组开关控制，每组参数独立应用。");

            selectedFBXGUIDs = GetSelectedModelGuids();
            ToolUi.BeginCard("1) 查询目标");
            DrawSelectionArea();
            ToolUi.EndCard();

            ToolUi.BeginCard("2) 材质导入");
            DrawMaterialImportSection();
            ToolUi.EndCard();
            ToolUi.BeginCard("3) 动画误差");
            DrawAnimationErrorSection();
            ToolUi.EndCard();
            ToolUi.BeginCard("4) Avatar");
            DrawAvatarSection();
            ToolUi.EndCard();

            ToolUi.BeginCard("5) 执行");
            if (GUILayout.Button("应用"))
            {
                ChangeImportMode();
            }
            ToolUi.EndCard();
            ToolUi.DrawStatus(statusMessage);
        }

        private void DrawSelectionArea()
        {
            EditorGUILayout.BeginVertical("box");
            GUILayout.Label($"当前选中模型资源（{selectedFBXGUIDs.Count}）", EditorStyles.boldLabel);
            if (selectedFBXGUIDs.Count == 0)
            {
                EditorGUILayout.HelpBox("请在 Project 视图中选择至少一个模型资源（如 FBX）。", MessageType.Info);
            }
            else
            {
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(120));
                foreach (string guid in selectedFBXGUIDs)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                    {
                        GUILayout.Label(path);
                    }
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawMaterialImportSection()
        {
            EditorGUILayout.BeginVertical("box");
            applyMaterialImportMode = EditorGUILayout.ToggleLeft("材质导入设置", applyMaterialImportMode, EditorStyles.boldLabel);
            if (applyMaterialImportMode)
            {
                importMode = (ImportMode)EditorGUILayout.EnumPopup("Material Import Mode", importMode);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAnimationErrorSection()
        {
            EditorGUILayout.BeginVertical("box");
            applyAnimationError = EditorGUILayout.ToggleLeft("动画误差设置", applyAnimationError, EditorStyles.boldLabel);
            if (applyAnimationError)
            {
                animationRotationError = EditorGUILayout.FloatField("Animation Rotation Error", animationRotationError);
                animationPositionError = EditorGUILayout.FloatField("Animation Position Error", animationPositionError);
                animationScaleError = EditorGUILayout.FloatField("Animation Scale Error", animationScaleError);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAvatarSection()
        {
            EditorGUILayout.BeginVertical("box");
            applyAvatarDefinition = EditorGUILayout.ToggleLeft("Avatar 设置", applyAvatarDefinition, EditorStyles.boldLabel);
            if (applyAvatarDefinition)
            {
                avatarDefinition = (AvatarDefinition)EditorGUILayout.EnumPopup("Avatar Definition", avatarDefinition);
            }

            EditorGUILayout.EndVertical();
        }

        private List<string> GetSelectedModelGuids()
        {
            var result = new List<string>();
            foreach (string guid in Selection.assetGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (AssetImporter.GetAtPath(path) is ModelImporter)
                {
                    result.Add(guid);
                }
            }

            return result;
        }

        private void ChangeImportMode()
        {
            if (selectedFBXGUIDs.Count == 0)
            {
                Debug.LogWarning("未检测到可修改的模型资源。");
                statusMessage = "未检测到可修改的模型资源";
                return;
            }

            if (!applyMaterialImportMode && !applyAnimationError && !applyAvatarDefinition)
            {
                Debug.LogWarning("请至少启用一个修改项。");
                statusMessage = "请至少启用一个修改项";
                return;
            }

            int changedCount = 0;
            int skippedCount = 0;
            foreach (string id in selectedFBXGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(id);
                ModelImporter modelImporter = AssetImporter.GetAtPath(path) as ModelImporter;
                if (modelImporter != null)
                {
                    SerializedObject so = new SerializedObject(modelImporter);
                    SerializedProperty s_ImportMode = so.FindProperty("m_MaterialImportMode");
                    SerializedProperty s_RotationError = so.FindProperty("m_AnimationRotationError");
                    SerializedProperty s_PositionError = so.FindProperty("m_AnimationPositionError");
                    SerializedProperty s_ScaleError = so.FindProperty("m_AnimationScaleError");
                    SerializedProperty s_AvatarSetup = so.FindProperty("m_AvatarSetup");

                    bool hasChanged = false;
                    if (applyMaterialImportMode && s_ImportMode != null && s_ImportMode.intValue != (int)importMode)
                    {
                        s_ImportMode.intValue = (int)importMode;
                        hasChanged = true;
                    }

                    if (applyAnimationError)
                    {
                        if (s_RotationError != null && !Mathf.Approximately(s_RotationError.floatValue, animationRotationError))
                        {
                            s_RotationError.floatValue = animationRotationError;
                            hasChanged = true;
                        }

                        if (s_PositionError != null && !Mathf.Approximately(s_PositionError.floatValue, animationPositionError))
                        {
                            s_PositionError.floatValue = animationPositionError;
                            hasChanged = true;
                        }

                        if (s_ScaleError != null && !Mathf.Approximately(s_ScaleError.floatValue, animationScaleError))
                        {
                            s_ScaleError.floatValue = animationScaleError;
                            hasChanged = true;
                        }
                    }

                    if (applyAvatarDefinition && s_AvatarSetup != null && s_AvatarSetup.intValue != (int)avatarDefinition)
                    {
                        s_AvatarSetup.intValue = (int)avatarDefinition;
                        hasChanged = true;
                    }

                    if (hasChanged)
                    {
                        so.ApplyModifiedPropertiesWithoutUndo();
                        modelImporter.SaveAndReimport();
                        changedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                else
                {
                    skippedCount++;
                }
            }

            Debug.Log($"模型导入设置应用完成：已修改 {changedCount} 个，跳过 {skippedCount} 个。");
            statusMessage = $"应用完成：已修改 {changedCount} 个，跳过 {skippedCount} 个";
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



