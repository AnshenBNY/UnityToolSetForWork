using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace ToolSet
{
    public class BatchRenameTool : EditorWindow,IToolGUI
    {
        private string newBaseName = "";
        private List<string> selectedPaths = new List<string>();

        private string replacePart = "";
        private string newPart = "";

        private Options options = Options.基础名称加序号;

        // [MenuItem("Tools/Batch Rename Tool")]
        // public static void ShowWindow()
        // {
        //     GetWindow<BatchRenameTool>("Batch Rename");
        // }
        //
        
        public void DrawGUI()
        {
            EditorGUILayout.Space(10);
            GUILayout.Label("批量重命名工具", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);
            
            GUILayout.Label("重命名方式：", EditorStyles.boldLabel);
            options = (Options)EditorGUILayout.EnumPopup(options);

            if(options == Options.基础名称加序号)
                newBaseName = EditorGUILayout.TextField("新基础名称:", newBaseName);
            if (options == Options.替换部分字符串)
            {
                replacePart = EditorGUILayout.TextField("替换字符串:", replacePart);
                newPart = EditorGUILayout.TextField("新字符串:", newPart);
            }
            if (GUILayout.Button("重命名"))
            {
                PerformRename();
            }
            GUILayout.Label("当前选择文件：");
            selectedPaths = new List<string>(Selection.assetGUIDs);
            foreach (string guid in selectedPaths)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    GUILayout.Label(Path.GetFileName(assetPath));
                }
            }
        }

        private void PerformRename()
        {
            if (options == Options.基础名称加序号)
            {
               if (string.IsNullOrEmpty(newBaseName) || selectedPaths.Count == 0)
                {
                    Debug.LogWarning("Please enter a base name and select at least one file.");
                    return;
                }

                int count = 1;
                foreach (string guid in selectedPaths)
                {
                    string oldPath = AssetDatabase.GUIDToAssetPath(guid);
                    string extension = Path.GetExtension(oldPath);
                    //string newPath = Path.Combine(Path.GetDirectoryName(oldPath), $"{newBaseName}_{count}{extension}");
                    string newName = $"{newBaseName}_{count}{extension}";
                    AssetDatabase.RenameAsset(oldPath, newName);

                    //AssetDatabase.MoveAsset(oldPath, newPath);
                    count++;
                }

                AssetDatabase.Refresh();
                Debug.Log($"Renamed {selectedPaths.Count} files successfully.");
        
            }

            if (options == Options.替换部分字符串)
            {
                foreach (string guid in selectedPaths)
                {

                    string oldPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!Path.GetFileName(oldPath).Contains(replacePart))
                        continue;
                    string newName = Path.GetFileName(oldPath).Replace(replacePart, newPart);
                    AssetDatabase.RenameAsset(oldPath, newName);
                }
                AssetDatabase.Refresh();
                Debug.Log($"Renamed {selectedPaths.Count} files successfully.");
            }
            
        }
       
    }
    public enum Options
    {
        基础名称加序号,替换部分字符串,
    }
}
