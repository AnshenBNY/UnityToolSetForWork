using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ToolSet
{
    internal static class ReferenceRepair
    {
        // Match Unity object references only. Plain strings containing a GUID are never rewritten.
        internal static readonly Regex Reference = new Regex(
            @"\{(?=[^{}]*\bfileID:\s*(?<id>-?\d+)\b)(?=[^{}]*\bguid:\s*(?<guid>[0-9a-fA-F]{32})\b)" +
            @"\s*(?:fileID:\s*-?\d+|guid:\s*[0-9a-fA-F]{32}|type:\s*\d+)" +
            @"(?:\s*,\s*(?:fileID:\s*-?\d+|guid:\s*[0-9a-fA-F]{32}|type:\s*\d+)){1,2}\s*\}", RegexOptions.Compiled);
        static readonly Regex FileIdField = new Regex(@"\bfileID:\s*-?\d+", RegexOptions.Compiled);
        internal static bool IsUnityText(string path, string content) =>
            content.TrimStart('\uFEFF').StartsWith("%YAML", StringComparison.Ordinal) ||
            (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
             content.TrimStart('\uFEFF').StartsWith("fileFormatVersion:", StringComparison.Ordinal));
        internal static bool ReferencesGuid(string path, string guid)
        {
            if (!File.Exists(path) || string.IsNullOrEmpty(guid)) return false;
            string content = File.ReadAllText(path);
            return IsUnityText(path, content) && Reference.Matches(content).Cast<Match>()
                .Any(m => m.Groups["guid"].Value.Equals(guid, StringComparison.OrdinalIgnoreCase));
        }
        internal static bool ValidateReferenceIds(IEnumerable<string> paths, string guid, string assetPath)
        {
            var objects = Objects(assetPath);
            foreach (string path in paths.Where(File.Exists))
            {
                string content = File.ReadAllText(path);
                if (!IsUnityText(path, content)) continue;
                foreach (Match match in Reference.Matches(content))
                    if (match.Groups["guid"].Value.Equals(guid, StringComparison.OrdinalIgnoreCase) &&
                        (!long.TryParse(match.Groups["id"].Value, out long id) || !objects.ContainsKey(id))) return false;
            }
            return true;
        }
        public static Object MatchCopy(Object source, string destination)
        {
            var objects = AssetDatabase.LoadAllAssetsAtPath(destination).Where(o => o != null && o.GetType() == source.GetType()).ToArray();
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string _, out long id);
            var exact = objects.Where(o => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string _, out long other) && id == other).ToArray();
            if (exact.Length == 1) return exact[0];
            var named = objects.Where(o => o.name == source.name).ToArray();
            if (named.Length == 1) return named[0];
            throw new InvalidOperationException("无法唯一匹配子资源：" + source.name + " → " + destination);
        }
        static Dictionary<long, Object> Objects(string path)
        {
            var result = new Dictionary<long, Object>();
            if (string.IsNullOrEmpty(path)) return result;
            var roots = AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o != null).ToList();
            foreach (var go in roots.OfType<GameObject>().ToArray())
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    roots.Add(t.gameObject); roots.AddRange(t.GetComponents<Component>().Where(c => c != null));
                }
            foreach (var obj in roots.Distinct())
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string _, out long id)) result[id] = obj;
            return result;
        }
        static long ResolveId(long oldId, Dictionary<long, Object> oldObjects, Dictionary<long, Object> newObjects, string originalPath, string newPath, bool identicalCopy)
        {
            if (oldObjects.TryGetValue(oldId, out var oldObject))
            {
                if (identicalCopy && newObjects.TryGetValue(oldId, out var copy) && copy.GetType() == oldObject.GetType()) return oldId;
                var candidates = newObjects.Where(p => p.Value.GetType() == oldObject.GetType() && p.Value.name == oldObject.name).ToArray();
                if (candidates.Length == 1) return candidates[0].Key;
                // Single main assets (e.g. two differently named materials) are compatible.
                if (AssetDatabase.IsMainAsset(oldObject))
                {
                    var main = newObjects.Where(p => p.Value.GetType() == oldObject.GetType() && AssetDatabase.IsMainAsset(p.Value)).ToArray();
                    if (main.Length == 1) return main[0].Key;
                }
                throw new InvalidOperationException("子资源类型/名称无法唯一对应：" + oldObject.name + "，fileID=" + oldId);
            }
            // Without the old asset, only Unity's stable main-object IDs establish type.
            var stable = new Dictionary<long, Type> { {2100000, typeof(Material)}, {2800000, typeof(Texture2D)}, {4800000, typeof(Shader)}, {7400000, typeof(AnimationClip)}, {8300000, typeof(AudioClip)}, {4300000, typeof(Mesh)}, {9100000, typeof(RuntimeAnimatorController)}, {8900000, typeof(Cubemap)}, {8400000, typeof(RenderTexture)} };
            if (string.Equals(Path.GetExtension(originalPath), Path.GetExtension(newPath), StringComparison.OrdinalIgnoreCase) &&
                stable.TryGetValue(oldId, out var expected) && newObjects.TryGetValue(oldId, out var target) && expected.IsInstanceOfType(target) && AssetDatabase.IsMainAsset(target)) return oldId;
            throw new InvalidOperationException("旧资源缺失，无法确认子资源类型/fileID（" + oldId + "）。请先恢复原文件或在 Inspector 中指定引用。");
        }
        public static ToolPlan Build(string title, string oldGuid, string originalPath, string newPath, IEnumerable<string> owners, bool clear = false, bool identicalCopy = false, string copyDestination = null)
        {
            if (!Regex.IsMatch(oldGuid ?? "", @"\A[0-9a-fA-F]{32}\z")) throw new ArgumentException("旧 GUID 无效");
            var plan = new ToolPlan { title = title };
            var oldObjects = Objects(AssetDatabase.GUIDToAssetPath(oldGuid));
            var newObjects = clear ? new Dictionary<long, Object>() : Objects(newPath);
            string newGuid = clear ? "" : AssetDatabase.AssetPathToGUID(newPath);
            if (!clear && newObjects.Count == 0) throw new InvalidOperationException("目标资源不可加载：" + newPath);
            if (!clear && newGuid.Equals(oldGuid, StringComparison.OrdinalIgnoreCase) && copyDestination == null)
                throw new InvalidOperationException("候选资源与旧资源 GUID 相同，无需替换。");
            if (!clear) plan.Watch(newPath);
            if (copyDestination != null)
            {
                plan.Watch(copyDestination);
                plan.Add("复制 " + originalPath + " → " + copyDestination, transaction =>
                {
                    transaction.Capture(copyDestination);
                    if (!AssetDatabase.CopyAsset(originalPath, copyDestination)) throw new IOException("复制失败：" + originalPath);
                    AssetDatabase.ImportAsset(copyDestination, ImportAssetOptions.ForceSynchronousImport);
                });
            }
            var ids = new Dictionary<long, long>();
            foreach (string path in owners.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) continue;
                try
                {
                    string content = File.ReadAllText(path);
                    if (!IsUnityText(path, content) || content.IndexOf(oldGuid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string assetPath = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 5) : path;
                    ToolPaths.RequireWritableAsset(assetPath);
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("引用文件只读：" + path);
                    if (EditorUtility.IsDirty(AssetImporter.GetAtPath(assetPath))) throw new IOException("导入设置有未保存修改：" + assetPath);
                    int count = 0;
                    string replaced = Reference.Replace(content, match =>
                    {
                        if (!match.Groups["guid"].Value.Equals(oldGuid, StringComparison.OrdinalIgnoreCase)) return match.Value;
                        count++;
                        if (clear) return "{fileID: 0}";
                        long id = long.Parse(match.Groups["id"].Value);
                        if (!ids.TryGetValue(id, out long replacement)) ids[id] = replacement = ResolveId(id, oldObjects, newObjects, originalPath, newPath, identicalCopy);
                        // Preserve field order, wrapping and optional type. Only identity fields change.
                        return FileIdField.Replace(match.Value, "fileID: " + replacement)
                            .Replace(match.Groups["guid"].Value, newGuid);
                    });
                    if (count == 0) continue;
                    plan.Watch(path);
                    plan.Watch(assetPath);
                    bool bom = File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 });
                    plan.Add(path + " · " + count + " 处引用 → " + (clear ? "空引用" : copyDestination ?? newPath), transaction =>
                    {
                        ToolPaths.RequireWritableAsset(assetPath);
                        if (EditorUtility.IsDirty(AssetImporter.GetAtPath(assetPath))) throw new IOException("导入设置有未保存修改：" + assetPath);
                        transaction.Capture(path, path == assetPath);
                        string finalText = replaced;
                        if (copyDestination != null)
                        {
                            string copyGuid = AssetDatabase.AssetPathToGUID(copyDestination);
                            if (string.IsNullOrEmpty(copyGuid)) throw new IOException("副本未导入：" + copyDestination);
                            finalText = Reference.Replace(replaced, m => m.Groups["guid"].Value.Equals(oldGuid, StringComparison.OrdinalIgnoreCase) ? m.Value.Replace(m.Groups["guid"].Value, copyGuid) : m.Value);
                        }
                        File.WriteAllText(path, finalText, new UTF8Encoding(bom));
                        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        if (ReferencesGuid(path, oldGuid))
                            throw new IOException("复查仍发现旧引用：" + path);
                    });
                }
                catch (Exception e) { plan.errors.Add(path + "：" + e.Message); }
            }
            if (plan.steps.Count == 0 && plan.errors.Count == 0) plan.errors.Add("扫描范围内没有找到旧 GUID 的 Unity 对象引用。请检查引用来源是否已移动、已替换，或是否需要扩大 JSON 修复范围；二进制资源不能直接改写。");
            return plan;
        }
    }
}
