using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ToolSet
{
    internal static class ToolPreferences
    {
        [Serializable] class Values { public List<Value> entries = new List<Value>(); }
        [Serializable] class Value { public string field, text; public List<string> guids = new List<string>(); }
        // Persist user input only; never persist results, operation closures, scene references or caches.
        static readonly HashSet<string> fields = new HashSet<string>((
            "rootPrefab sourcePrefabs outputFolder excludeFolders sourceMaterialsFolder targetTexturesFolder " +
            "newBaseName replacePart newPart insertPart insertIndex insertPosition renameTarget includeChildren options startNumber numberDigits " +
            "importMode avatarDefinition sourceAvatar animationRotationError animationPositionError animationScaleError applyMaterialImportMode applyAnimationError applyAvatarDefinition " +
            "fxToggle matToggle onlyShowMissing candidateMaxCount repairOnlyJsonScope repairExpandRootDependencies excludeEditorScripts excludeExtensionsInput replaceCopyTargetFolder showAdvanced").Split(' '));
        public static void Save(IToolGUI panel)
        {
            var data = new Values();
            foreach (var field in panel.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(f => fields.Contains(f.Name)))
            {
                object value = field.GetValue(panel);
                var entry = new Value { field = field.Name };
                if (typeof(Object).IsAssignableFrom(field.FieldType)) entry.text = GuidOf(value as Object);
                else if (value is IList list) foreach (var item in list) entry.guids.Add(GuidOf(item as Object));
                else entry.text = Convert.ToString(value, CultureInfo.InvariantCulture);
                data.entries.Add(entry);
            }
            EditorPrefs.SetString(ToolPaths.PreferenceKey(panel.GetType().Name), JsonUtility.ToJson(data));
        }
        static string GuidOf(Object obj) => obj != null && EditorUtility.IsPersistent(obj) ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(obj)) : "";
        public static void Load(IToolGUI panel)
        {
            try
            {
                string json = EditorPrefs.GetString(ToolPaths.PreferenceKey(panel.GetType().Name));
                if (string.IsNullOrEmpty(json)) return;
                var data = JsonUtility.FromJson<Values>(json);
                foreach (var entry in data.entries)
                {
                    var field = panel.GetType().GetField(entry.field, BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field == null || !fields.Contains(entry.field)) continue;
                    if (typeof(Object).IsAssignableFrom(field.FieldType)) field.SetValue(panel, AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(entry.text), field.FieldType));
                    else if (field.GetValue(panel) is IList list)
                    {
                        Type itemType = field.FieldType.GetGenericArguments()[0];
                        list.Clear(); foreach (string guid in entry.guids) list.Add(AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guid), itemType));
                    }
                    else if (field.FieldType.IsEnum) field.SetValue(panel, Enum.Parse(field.FieldType, entry.text));
                    else field.SetValue(panel, Convert.ChangeType(entry.text, field.FieldType, CultureInfo.InvariantCulture));
                }
                if (panel is PrefabCopyTool copy && !data.entries.Any(e => e.field == "sourcePrefabs")) copy.MigrateLegacyCopyInput();
            }
            catch (Exception e) { Debug.LogWarning("工具偏好已重置：" + e.Message); }
        }
    }
}
