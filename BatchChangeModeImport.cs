using System;
using System.Collections.Generic;
using System.Globalization;
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
        public override void Invalidate()
        {
            selected = null;
            ToolWorkflow.DiscardPreparedPlan(nameof(BatchChangeModelImport));
        }
        List<string> Selected => selected ?? (selected = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => ToolPaths.Contains("Assets", p) && AssetImporter.GetAtPath(p) is ModelImporter).Distinct().OrderBy(p => p).ToList());
        string InputSignature() => string.Join("|", new[]
        {
            importMode.ToString(), avatarDefinition.ToString(), sourceAvatar == null ? "" : sourceAvatar.GetInstanceID().ToString(),
            animationRotationError.ToString("R", CultureInfo.InvariantCulture), animationPositionError.ToString("R", CultureInfo.InvariantCulture),
            animationScaleError.ToString("R", CultureInfo.InvariantCulture), applyMaterialImportMode.ToString(), applyAnimationError.ToString(),
            applyAvatarDefinition.ToString(), string.Join(",", Selection.assetGUIDs.OrderBy(guid => guid))
        });
        public override void DrawGUI()
        {
            string previousInput = InputSignature();
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
            if (previousInput != InputSignature()) ToolWorkflow.DiscardPreparedPlan(nameof(BatchChangeModelImport));
        }
        internal ToolPlan BuildPlan()
        {
            Invalidate();
            var plan = new ToolPlan { title = "模型导入设置" };
            string input = InputSignature();
            if (!applyMaterialImportMode && !applyAnimationError && !applyAvatarDefinition) plan.errors.Add("至少选择一组参数。");
            bool animationValid = !applyAnimationError || !new[] { animationRotationError, animationPositionError, animationScaleError }.Any(v => float.IsNaN(v) || float.IsInfinity(v) || v < 0);
            if (!animationValid)
                plan.errors.Add("动画误差必须为有限的非负数。");
            bool avatarValid = !applyAvatarDefinition || avatarDefinition != AvatarDefinition.CopyFromOtherAvatar || sourceAvatar != null && sourceAvatar.isValid;
            if (!avatarValid)
                plan.errors.Add("请选择有效的来源 Avatar。");
            var materialMode = (ModelImporterMaterialImportMode)importMode;
            var setup = (ModelImporterAvatarSetup)avatarDefinition;
            float rotation = animationRotationError, position = animationPositionError, scale = animationScaleError;
            bool material = applyMaterialImportMode, animation = applyAnimationError && animationValid, avatar = applyAvatarDefinition && avatarValid;
            Avatar avatarSource = sourceAvatar;
            if (avatar && avatarSource != null) plan.Watch(AssetDatabase.GetAssetPath(avatarSource));
            foreach (string path in Selected)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                bool applyAvatarToCurrent = avatar;
                if (avatar && setup == ModelImporterAvatarSetup.CopyFromOther && AssetDatabase.GetAssetPath(avatarSource) == path)
                { plan.errors.Add("已跳过该模型的 Avatar 设置，来源 Avatar 不能来自目标模型自身：" + path); applyAvatarToCurrent = false; }
                var changes = new List<string>();
                if (material && importer.materialImportMode != materialMode) changes.Add("材质 " + importer.materialImportMode + " → " + materialMode);
                if (animation && (!Mathf.Approximately(importer.animationRotationError, rotation) || !Mathf.Approximately(importer.animationPositionError, position) || !Mathf.Approximately(importer.animationScaleError, scale)))
                    changes.Add($"动画误差 ({importer.animationRotationError}, {importer.animationPositionError}, {importer.animationScaleError}) → ({rotation}, {position}, {scale})");
                if (applyAvatarToCurrent && (importer.avatarSetup != setup || setup == ModelImporterAvatarSetup.CopyFromOther && importer.sourceAvatar != avatarSource))
                    changes.Add("Avatar " + importer.avatarSetup + " → " + setup);
                if (changes.Count == 0) continue;
                try { ToolPaths.RequireWritableAsset(path + ".meta"); } catch (Exception e) { plan.errors.Add(e.Message); continue; }
                bool setAvatar = applyAvatarToCurrent;
                plan.Watch(path);
                plan.Add(path + " · " + string.Join("；", changes), transaction =>
                {
                    transaction.Capture(path + ".meta", false);
                    var current = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (current == null) throw new InvalidOperationException("模型导入器丢失：" + path);
                    if (material) current.materialImportMode = materialMode;
                    if (animation) { current.animationRotationError = rotation; current.animationPositionError = position; current.animationScaleError = scale; }
                    if (setAvatar) { current.avatarSetup = setup; current.sourceAvatar = setup == ModelImporterAvatarSetup.CopyFromOther ? avatarSource : null; }
                    current.SaveAndReimport();
                });
            }
            plan.validate = () => InputSignature() != input ? "选择或导入设置已变化，请重新预览。" : null;
            plan.emptyMessage = Selected.Count == 0 ? "当前没有选中可修改的模型资源。" : "所选模型已经符合当前导入设置。";
            return plan;
        }
    }
    public enum ImportMode { None = 0, Standard = 1, ImportviaMaterialDescription = 2 }
    public enum AvatarDefinition { NoAvatar = 0, CreateFromThisModel = 1, CopyFromOtherAvatar = 2 }
}
