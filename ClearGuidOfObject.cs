using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;

namespace ToolSet
{
    public enum FindFilter
    {
        Prefab,Material,Model,
    }
    public class ClearGuidOfObject : EditorWindow,IToolGUI
    {
        private string displayedGuid = "";
        private UnityEngine.Object selectedObject;
    
        private List<UnityEngine.Object> objectInFolder = new List<UnityEngine.Object>();
        private string folderPath = "";
    
        private List<UnityEngine.Object> objectInFolder_GUID = new List<UnityEngine.Object>();
    
        private string clearGuidMsg = "";
    
        private Vector2 stringScrollPos = Vector2.zero;
    
        private string[] trgetBtnOptions = new string[] { "文件", "包含GUID的文件" };
        private int trgetBtnOptionsIndex = 0;
    
        public FindFilter findFilter = 0;
    
        // [MenuItem("BjTools/查询GUID引用工具(整合版)")]
        // public static void ShowWindow()
        // {
        //     GetWindow<ClearGuidOfObject>("查询GUID引用工具(整合版)");
        // }
    
        // 滚动视图变量
        private Vector2 scrollPos = Vector2.zero;
    
        public void DrawGUI()
        {
            // 第一行：显示GUID
            GUILayout.Label("当前选择的文件：", EditorStyles.boldLabel);
    
            if (selectedObject == null)
            {
                GUILayout.Label("请选择一个文件，进行下一步。", EditorStyles.boldLabel);
                selectedObject = EditorGUILayout.ObjectField("", selectedObject, typeof(UnityEngine.Object), false, GUILayout.Width(400));
            }
            else
            {
    
                EditorGUILayout.BeginVertical();
                selectedObject = EditorGUILayout.ObjectField("", selectedObject, typeof(UnityEngine.Object), false, GUILayout.Width(400));
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("GUID：", EditorStyles.boldLabel, GUILayout.Width(40));
                GUILayout.Label(displayedGuid);
                EditorGUILayout.EndHorizontal();
                if (displayedGuid.Length > 1)
                {
                    if (GUILayout.Button("复制GUID", EditorStyles.miniButton, GUILayout.Width(80)))
                    {
                        TextEditor teGUID = new TextEditor();
                        teGUID.text = displayedGuid;
                        teGUID.SelectAll();
                        teGUID.Copy();
                    }
                }
    
                EditorGUILayout.EndVertical();
    
                EditorGUILayout.Space();
    
                // 第二行：显示GUID按钮
                if (GUILayout.Button("选择当前文件提取GUID"))
                {
                    if (selectedObject != null)
                    {
                        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(selectedObject));
                        displayedGuid = guid;
                     
                    }
                    else
                    {
                        displayedGuid = "没有选中任何资源";
                    }
                }
    
                EditorGUILayout.Space();
    
                // 第三行：动态显示路径或提示语
                if (!string.IsNullOrEmpty(folderPath))
                {
                    GUILayout.Label("当前选择的目录：" + folderPath, EditorStyles.boldLabel);
                }
                else
                {
                    GUILayout.Label("选择文件目录：", EditorStyles.boldLabel);
                }
    
                EditorGUILayout.Space();
    
                findFilter = (FindFilter)EditorGUILayout.EnumPopup("要查找的文件类型：", findFilter);
    
                EditorGUILayout.BeginHorizontal(GUILayout.Width(300));
    
                // 第四行：打开文件夹选择对话框的按钮
                if (GUILayout.Button("选择搜索文件目录"))
                {
                    string defaultPath = Application.dataPath; // 默认从 Assets 开始
                    string selectedPath = EditorUtility.OpenFolderPanel("选择要查找的文件所在的目录", defaultPath, "");
                    if (!string.IsNullOrEmpty(selectedPath))
                    {
                        folderPath = "Assets" + selectedPath.Substring(Application.dataPath.Length);
                    }
                }
    
                if (folderPath.Length > 0)
                {
                    
                    if (GUILayout.Button("刷新路径中的文件"))
                    {
                        try
                        {
                            LoadObjectInFolder();
                        }
                        catch
                        {
                            Debug.Log("未选择目录");
                            folderPath = "";
                        }
                    }
                }
    
                EditorGUILayout.EndHorizontal();
    
                EditorGUILayout.Space();
                trgetBtnOptionsIndex = GUILayout.Toolbar(trgetBtnOptionsIndex, trgetBtnOptions, GUILayout.Width(400));
                scrollPos = EditorGUILayout.BeginScrollView(scrollPos, false, true);
                EditorGUILayout.Space();
                switch (trgetBtnOptionsIndex)
                {
                    case 0:
                        listView_Prefab();
                        break;
                    case 1:
                        listView_Prefab_GUID();
                        break;
                }
    
                EditorGUILayout.EndScrollView();
    
                stringScrollPos = EditorGUILayout.BeginScrollView(stringScrollPos, GUILayout.Height(200));
    
                EditorGUILayout.Space();
                EditorGUILayout.BeginHorizontal();
    
                if (GUILayout.Button("搜索列表中的GUID", GUILayout.Width(200)))
                {
                    if (!string.IsNullOrEmpty(displayedGuid))
                    {
                        for (int i = 0; i < objectInFolder.Count; i++)
                        {
                            string assetPath = AssetDatabase.GetAssetPath(objectInFolder[i]);
                            string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + assetPath;
                            List<string> lines = new List<string>(System.IO.File.ReadAllLines(fullPath));
                            for (int j = 0; j < lines.Count; j++)
                            {
                                string line = lines[j];
                                // 检查是否是一个新对象块开始，并且包含目标 GUID
                                if (line.Contains(displayedGuid))
                                {
                                    objectInFolder_GUID.Add(objectInFolder[i]);
                                    continue;
                                }
                            }
                        }
                        clearGuidMsg = "文件数量 = " + objectInFolder.Count + "  :  包含GUID的数量 = " + objectInFolder_GUID.Count;
                    }
                    else
                    {
                        Debug.LogError("未获取目标GUID！");
                    }
                   
                }
    
                if (objectInFolder_GUID.Count > 0)
                {
                    if (GUILayout.Button("是否清理GUID", GUILayout.Width(200)))
                    {
                        for (int i = 0; objectInFolder_GUID.Count > i; i++)
                        {
                            string assetPath = AssetDatabase.GetAssetPath(objectInFolder_GUID[i]);
                            string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + assetPath;
                            clearGuidMsg += "\n" + DeleteYamlBlockByGuid(fullPath, displayedGuid);
                        }
                        objectInFolder_GUID.Clear();
                    }
                }
                EditorGUILayout.EndHorizontal();
                GUILayout.Label("GUID 信息：" + clearGuidMsg);
                EditorGUILayout.EndScrollView();
            }
        }
    
        private void listView_Prefab()
        {
            for (int i = 0; i < objectInFolder.Count; i++)
            {
                EditorGUILayout.BeginVertical();
                string matPath = AssetDatabase.GetAssetPath(objectInFolder[i]);
    
                GUILayout.Label("路径：" + matPath, EditorStyles.label);
    
                #region//按钮
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox, GUILayout.Width(600));
    
                // 材质名按钮 - 点击跳转
                if (GUILayout.Button(objectInFolder[i].name, EditorStyles.label, GUILayout.ExpandWidth(true)))
                {
                    EditorGUIUtility.PingObject(objectInFolder[i]);
                    Selection.activeObject = objectInFolder[i];
                }
    
                // 复制路径按钮
                if (GUILayout.Button("复制路径", EditorStyles.miniButton, GUILayout.Width(80)))
                {
                    TextEditor te = new TextEditor();
                    te.text = AssetDatabase.GetAssetPath(objectInFolder[i]);
                    te.SelectAll();
                    te.Copy();
                }
    
                // 移出这个材质球
                if (GUILayout.Button("移出这个文件", EditorStyles.miniButton, GUILayout.Width(100)))
                {
                    objectInFolder.RemoveAt(i);
                }
    
                if (GUILayout.Button("清除这个文件中包含的GUID", EditorStyles.miniButton, GUILayout.Width(200)))
                {
                    string assetPath = AssetDatabase.GetAssetPath(objectInFolder[i]);
                    string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + assetPath;
                    DeleteYamlBlockByGuid(fullPath, displayedGuid);
                }
                EditorGUILayout.EndHorizontal();
                #endregion
    
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space();
            }
        }
        private void listView_Prefab_GUID()
        {
            for (int i = 0; i < objectInFolder_GUID.Count; i++)
            {
                EditorGUILayout.BeginVertical();
                string matPath = AssetDatabase.GetAssetPath(objectInFolder_GUID[i]);
    
                GUILayout.Label("路径：" + matPath, EditorStyles.label);
    
                #region//按钮
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox, GUILayout.Width(600));
    
                // 材质名按钮 - 点击跳转
                if (GUILayout.Button(objectInFolder_GUID[i].name, EditorStyles.label, GUILayout.ExpandWidth(true)))
                {
                    EditorGUIUtility.PingObject(objectInFolder_GUID[i]);
                    Selection.activeObject = objectInFolder_GUID[i];
                }
    
                // 复制路径按钮
                if (GUILayout.Button("复制路径", EditorStyles.miniButton, GUILayout.Width(80)))
                {
                    TextEditor te = new TextEditor();
                    te.text = AssetDatabase.GetAssetPath(objectInFolder_GUID[i]);
                    te.SelectAll();
                    te.Copy();
                }
    
                if (GUILayout.Button("清除这个文件中包含的GUID", EditorStyles.miniButton, GUILayout.Width(200)))
                {
                    string assetPath = AssetDatabase.GetAssetPath(objectInFolder_GUID[i]);
                    string fullPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + assetPath;
                    DeleteYamlBlockByGuid(fullPath, displayedGuid);
                }
                EditorGUILayout.EndHorizontal();
                #endregion
    
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space();
            }
        }
    
        private void LoadObjectInFolder()
        {
            if (string.IsNullOrEmpty(folderPath)) return;
    
            objectInFolder.Clear();
            objectInFolder_GUID.Clear();
    
            AssetDatabase.Refresh();
    
            string[] objectPathList = AssetDatabase.FindAssets("t:" + findFilter, new[] { folderPath });
            Debug.Log("FindFilter = " + findFilter);
    
            foreach (string guid in objectPathList)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (obj != null)
                {
                    objectInFolder.Add(obj);
                }
            }
        }
    
        public static string DeleteYamlBlockByGuid(string filePath, string searchString)
        {
            AssetDatabase.Refresh();
            if (!System.IO.File.Exists(filePath))
            {
                Console.WriteLine("文件不存在！");
                return "文件不存在：" + filePath;
            }
    
            try
            {
                List<string> lines = new List<string>(System.IO.File.ReadAllLines(filePath));
                List<string> filteredLines = new List<string>();
    
                foreach (string line in lines)
                {
                    if (!line.Contains(searchString))
                    {
                        filteredLines.Add(line);
                    }
                }
    
                System.IO.File.WriteAllLines(filePath, filteredLines);
                AssetDatabase.Refresh();
                return "文件：" + filePath + "\n" + $"已经清理 {lines.Count - filteredLines.Count}条";
            }
            catch (Exception ex)
            {
                Console.WriteLine("发生错误: " + ex.Message);
                return "发生错误: " + ex.Message;
            }
        }
    }
}







