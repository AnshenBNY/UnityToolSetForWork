using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ToolSet;

public class ToolManagerWindow : EditorWindow
{
    int currentTab;
    readonly Dictionary<int, IToolGUI> tools = new Dictionary<int, IToolGUI>();
    readonly Vector2[] scroll = new Vector2[7];
    readonly string[] names = { "预制体复制", "依赖记录检查", "非法引用查询", "贴图迁移", "批量重命名", "预制体与材质映射", "模型导入设置" };
    string error;
    void OnEnable()
    {
        titleContent = new GUIContent("多功能电饭煲");
        minSize = new Vector2(760, 620);
        currentTab = Mathf.Clamp(EditorPrefs.GetInt(ToolPaths.PreferenceKey("Tab")), 0, 6);
        tools[0] = new PrefabCopyTool(); tools[1] = new DependencyRecordTool(); tools[2] = new IllegalReferenceTool();
        tools[3] = new MaterialTextureMigrator(); tools[4] = new BatchRenameTool(); tools[5] = new PrefabMatProjectTool(); tools[6] = new BatchChangeModelImport();
        foreach (var panel in tools.Values) ToolPreferences.Load(panel);
        Selection.selectionChanged += Invalidate;
        EditorApplication.projectChanged += Invalidate;
        EditorApplication.hierarchyChanged += Invalidate;
        ToolWorkflow.Changed += Repaint;
    }
    void OnDisable()
    {
        EditorPrefs.SetInt(ToolPaths.PreferenceKey("Tab"), currentTab);
        Selection.selectionChanged -= Invalidate; EditorApplication.projectChanged -= Invalidate;
        EditorApplication.hierarchyChanged -= Invalidate; ToolWorkflow.Changed -= Repaint;
        foreach (var panel in tools.Values) ToolPreferences.Save(panel);
        tools.Clear();
    }
    void Invalidate() { foreach (var panel in tools.Values) (panel as ToolPanel)?.Invalidate(); Repaint(); }
    void Navigation(string group, params int[] entries)
    {
        GUILayout.Space(10); GUILayout.Label(group, EditorStyles.miniBoldLabel);
        foreach (int index in entries)
        {
            bool selected = GUILayout.Toggle(currentTab == index, names[index], "Button", GUILayout.Height(29));
            if (selected && currentTab != index) { currentTab = index; error = null; GUI.FocusControl(null); }
        }
    }
    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(155)))
            {
                Navigation("复制与迁移", 0, 3, 5); Navigation("检查与修复", 1, 2); Navigation("批量编辑", 4, 6);
                GUILayout.FlexibleSpace();
                GUILayout.Label("文件恢复：下方按钮\n场景编辑：Ctrl+Z", EditorStyles.wordWrappedMiniLabel);
            }
            using (new EditorGUILayout.VerticalScope())
            {
                scroll[currentTab] = EditorGUILayout.BeginScrollView(scroll[currentTab]);
                try
                {
                    using (new EditorGUI.DisabledScope(ToolWorkflow.Running))
                        if (tools.TryGetValue(currentTab, out var panel)) panel.DrawGUI();
                    if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
                }
                catch (ExitGUIException) { throw; }
                catch (Exception e) { error = e.Message; Debug.LogException(e); }
                finally { EditorGUILayout.EndScrollView(); }
                ToolWorkflow.Draw(currentTab == 0 ? nameof(PrefabCopyTool) : null);
            }
        }
    }
    [MenuItem("BjTools/多功能电饭煲", false, 10)]
    public static void OpenHub()
    {
        var window = GetWindow<ToolManagerWindow>(); window.Show();
    }
}
