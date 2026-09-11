using System;
using System.Collections.Generic;
using UnityEditor;

namespace ToolSet
{
    [InitializeOnLoad] internal static class ToolAssetCache
    {
        static readonly Dictionary<string, string[]> named = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string[]> dependencies = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        static ToolAssetCache() { EditorApplication.projectChanged += Clear; }
        internal static void Clear() { named.Clear(); dependencies.Clear(); }
        internal static string[] FindNamed(string name)
        {
            if (!named.TryGetValue(name, out var result)) named[name] = result = AssetDatabase.FindAssets(name);
            return result;
        }
        internal static string[] Dependencies(string path, bool recursive)
        {
            string key = path + "|" + recursive;
            if (!dependencies.TryGetValue(key, out var result)) dependencies[key] = result = AssetDatabase.GetDependencies(path, recursive);
            return result;
        }
    }
}
