using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    public class BatchChangeModelImport : ToolPanel
    {
        ImportMode importMode = ImportMode.Standard;
        AvatarDefinition avatarDefinition = AvatarDefinition.NoAvatar;
        Avatar sourceAvatar;
        float animationRotationError = 0.5f, animationPositionError = 0.5f, animationScaleError = 0.5f;
        bool applyMaterialImportMode = true, applyAnimationError, applyAvatarDefinition;
        List<string> selected;
        public override void Invalidate() => selected = null;
        List<string> Selected => selected ?? (selected = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => ToolPaths.Contains("Assets", p) && AssetImporter.GetAtPath(p) is ModelImporter).Distinct().OrderBy(p => p).ToList());
        public override void DrawGUI()
        {
            ToolUi.DrawToolHeader("模型导入设置", "只应用勾选的参数组；每个模型独立导入，文件备份包含 .meta。");
            EditorGUILayout.LabelField("选中模型：" + Selected.Count, EditorStyles.boldLabel);
            foreach (string path in Selected.Take(20)) EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
            if (Selected.Count > 20) EditorGUILayout.LabelField("完整列表将在预览中显示。");
            applyMaterialImportMode = EditorGUILayout.ToggleLeft("修改材质导入方式", applyMaterialImportMode);
            if (applyMaterialImportMode) importMode = (ImportMode)EditorGUILayout.EnumPopup("材质导入", importMode);
            applyAnimationError = EditorGUILayout.ToggleLeft("修改动画压缩误差", applyAnimationError);
            if (applyAnimationError)
            {
                animationRotationError = EditorGUILayout.FloatField("旋转误差", animationRotationError);
                animationPositionError = EditorGUILayout.FloatField("位置误差", animationPositionError);
                animationScaleError = EditorGUILayout.FloatField("缩放误差", animationScaleError);
            }
            applyAvatarDefinition = EditorGUILayout.ToggleLeft("修改 Avatar", applyAvatarDefinition);
            if (applyAvatarDefinition)
            {
                avatarDefinition = (AvatarDefinition)EditorGUILayout.EnumPopup("Avatar 定义", avatarDefinition);
                if (avatarDefinition == AvatarDefinition.CopyFromOtherAvatar)
                    sourceAvatar = (Avatar)EditorGUILayout.ObjectField("来源 Avatar", sourceAvatar, typeof(Avatar), false);
            }
            if (GUILayout.Button("预览导入设置更改", GUILayout.Height(30))) ToolWorkflow.Preview(BuildPlan);
        }
        internal ToolPlan BuildPlan()
        {
            Invalidate();
            var plan = new ToolPlan { title = "模型导入设置" };
            if (!applyMaterialImportMode && !applyAnimationError && !applyAvatarDefinition) plan.errors.Add("至少选择一组参数。");
            if (applyAnimationError && new[] { animationRotationError, animationPositionError, animationScaleError }.Any(v => float.IsNaN(v) || float.IsInfinity(v) || v < 0))
                plan.errors.Add("动画误差必须为有限的非负数。");
            if (applyAvatarDefinition && avatarDefinition == AvatarDefinition.CopyFromOtherAvatar && (sourceAvatar == null || !sourceAvatar.isValid))
                plan.errors.Add("请选择有效的来源 Avatar。");
            var materialMode = (ModelImporterMaterialImportMode)importMode;
            var setup = (ModelImporterAvatarSetup)avatarDefinition;
            float rotation = animationRotationError, position = animationPositionError, scale = animationScaleError;
            bool material = applyMaterialImportMode, animation = applyAnimationError, avatar = applyAvatarDefinition;
            Avatar avatarSource = sourceAvatar;
            if (avatarSource != null) plan.Watch(AssetDatabase.GetAssetPath(avatarSource));
            foreach (string path in Selected)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                if (avatar && setup == ModelImporterAvatarSetup.CopyFromOther && AssetDatabase.GetAssetPath(avatarSource) == path)
                { plan.errors.Add("来源 Avatar 不能来自目标模型自身：" + path); continue; }
                var changes = new List<string>();
                if (material && importer.materialImportMode != materialMode) changes.Add("材质 " + importer.materialImportMode + " → " + materialMode);
                if (animation && (!Mathf.Approximately(importer.animationRotationError, rotation) || !Mathf.Approximately(importer.animationPositionError, position) || !Mathf.Approximately(importer.animationScaleError, scale)))
                    changes.Add($"动画误差 ({importer.animationRotationError}, {importer.animationPositionError}, {importer.animationScaleError}) → ({rotation}, {position}, {scale})");
                if (avatar && (importer.avatarSetup != setup || setup == ModelImporterAvatarSetup.CopyFromOther && importer.sourceAvatar != avatarSource))
                    changes.Add("Avatar " + importer.avatarSetup + " → " + setup);
                if (changes.Count == 0) continue;
                try { ToolPaths.RequireWritableAsset(path + ".meta"); } catch (Exception e) { plan.errors.Add(e.Message); }
                plan.Watch(path);
                plan.Add(path + " · " + string.Join("；", changes), transaction =>
                {
                    transaction.Capture(path + ".meta", false);
                    var current = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (current == null) throw new InvalidOperationException("模型导入器丢失：" + path);
                    if (material) current.materialImportMode = materialMode;
                    if (animation) { current.animationRotationError = rotation; current.animationPositionError = position; current.animationScaleError = scale; }
                    if (avatar) { current.avatarSetup = setup; current.sourceAvatar = setup == ModelImporterAvatarSetup.CopyFromOther ? avatarSource : null; }
                    current.SaveAndReimport();
                });
            }
            return plan;
        }
    }
    public enum ImportMode { None = 0, Standard = 1, ImportviaMaterialDescription = 2 }
    public enum AvatarDefinition { NoAvatar = 0, CreateFromThisModel = 1, CopyFromOtherAvatar = 2 }
}
