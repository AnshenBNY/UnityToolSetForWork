using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    public class MaterialTextureMigrator : ToolPanel
    {
        DefaultAsset sourceMaterialsFolder, targetTexturesFolder;
        readonly List<DefaultAsset> excludeFolders = new List<DefaultAsset>();
        public override void DrawGUI()
        {
            ToolUi.DrawToolHeader("材质贴图迁移", "复制材质引用的贴图，并更新所选目录中的材质。纹理坐标和缩放保持原值。");
            sourceMaterialsFolder = (DefaultAsset)EditorGUILayout.ObjectField("待修改的材质目录", sourceMaterialsFolder, typeof(DefaultAsset), false);
            targetTexturesFolder = (DefaultAsset)EditorGUILayout.ObjectField("贴图输出目录", targetTexturesFolder, typeof(DefaultAsset), false);
            ToolUi.BeginCard("排除目录");
            for (int i = 0; i < excludeFolders.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    excludeFolders[i] = (DefaultAsset)EditorGUILayout.ObjectField(excludeFolders[i], typeof(DefaultAsset), false);
                    if (GUILayout.Button("移除", GUILayout.Width(50))) excludeFolders.RemoveAt(i--);
                }
            if (GUILayout.Button("添加排除目录")) excludeFolders.Add(null);
            ToolUi.EndCard();
            if (GUILayout.Button("预览贴图迁移", GUILayout.Height(30))) ToolWorkflow.Preview(BuildPlan);
        }
        internal ToolPlan BuildPlan()
        {
            string source = AssetDatabase.GetAssetPath(sourceMaterialsFolder), target = AssetDatabase.GetAssetPath(targetTexturesFolder);
            if (!AssetDatabase.IsValidFolder(source) || !AssetDatabase.IsValidFolder(target) || !ToolPaths.Contains("Assets", target))
                throw new InvalidOperationException("请选择有效的材质目录和 Assets 内的输出目录。");
            var plan = new ToolPlan { title = "材质贴图迁移" };
            var excluded = excludeFolders.Where(f => f != null).Select(AssetDatabase.GetAssetPath).ToArray();
            var paths = new CopyPathAllocator();
            var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var edits = new List<(string path, Dictionary<string, Texture> slots)>();
            var materials = AssetDatabase.FindAssets("t:Material", new[] { source }).Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p).ToArray();
            foreach (string path in materials)
            {
                if (EditorUtility.DisplayCancelableProgressBar("预览贴图迁移", path, (float)edits.Count / Math.Max(1, materials.Length))) throw new OperationCanceledException();
                if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) { plan.errors.Add("无法加载材质：" + path); continue; }
                var slots = new Dictionary<string, Texture>();
                var so = new SerializedObject(material);
                var textures = so.FindProperty("m_SavedProperties.m_TexEnvs");
                if (textures == null) continue;
                for (int i = 0; i < textures.arraySize; i++)
                {
                    var slot = textures.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Texture");
                    var texture = slot?.objectReferenceValue as Texture;
                    if (texture == null) continue;
                    string original = AssetDatabase.GetAssetPath(texture);
                    if (string.IsNullOrEmpty(original) || !System.IO.File.Exists(original) || ToolPaths.Contains(target, original) || excluded.Any(e => ToolPaths.Contains(e, original))) continue;
                    if (!copies.ContainsKey(original)) copies.Add(original, paths.Reserve(original, target, ""));
                    slots[slot.propertyPath] = texture;
                }
                if (slots.Count == 0) continue;
                try { ToolPaths.RequireWritableAsset(path); } catch (Exception e) { plan.errors.Add(e.Message); }
                plan.Watch(path); edits.Add((path, slots));
            }
            foreach (var copy in copies)
            {
                plan.Watch(copy.Key); plan.Watch(copy.Value);
                plan.Add("复制 " + copy.Key + " → " + copy.Value, transaction =>
                {
                    transaction.Capture(copy.Value);
                    if (!AssetDatabase.CopyAsset(copy.Key, copy.Value)) throw new InvalidOperationException("复制失败：" + copy.Key);
                    AssetDatabase.ImportAsset(copy.Value, ImportAssetOptions.ForceSynchronousImport);
                });
            }
            foreach (var edit in edits)
                plan.Add("更新材质 " + edit.path + " · " + edit.slots.Count + " 个贴图槽", transaction =>
                {
                    ToolPaths.RequireWritableAsset(edit.path); transaction.Capture(edit.path);
                    var material = AssetDatabase.LoadAssetAtPath<Material>(edit.path);
                    var so = new SerializedObject(material);
                    foreach (var slot in edit.slots)
                    {
                        var property = so.FindProperty(slot.Key);
                        if (property == null || property.objectReferenceValue != slot.Value) throw new InvalidOperationException("材质槽已变化，请重新预览：" + edit.path);
                        property.objectReferenceValue = ReferenceRepair.MatchCopy(slot.Value, copies[AssetDatabase.GetAssetPath(slot.Value)]);
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssetIfDirty(material);
                });
            return plan;
        }
    }
}
