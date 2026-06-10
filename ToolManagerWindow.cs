using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ToolSet;
public class ToolManagerWindow : EditorWindow
{
    private int currentTab = 0;
    private readonly string[] tabNames = { "预制体复制", "依赖记录检查", "非法引用查询", "贴图复制", "批量重命名", "材质、预制体映射", "批量修改模型导入设置" };

    // 缓存每个工具的实例（避免每次重绘都新建）
    private readonly Dictionary<int, IToolGUI> toolInstances = new();

    private void OnEnable()
    {
        //预创建所有工具实例
        EnsureToolInstance(0, () => new PrefabCopyTool());
        EnsureToolInstance(1, () => new DependencyRecordTool());
        EnsureToolInstance(2, () => new IllegalReferenceTool());
        EnsureToolInstance(3, () => new MaterialTextureMigrator());
        EnsureToolInstance(4, () => new BatchRenameTool());
        EnsureToolInstance(5, () => new PrefabMatProjectTool());
        EnsureToolInstance(6, () => new BatchChangeModelImport());
    }

    private void OnDisable()
    {
        // // 清理所有工具
        // foreach (var tool in toolInstances.Values)
        // {
        //     tool.OnDisable();
        // }
        toolInstances.Clear();
    }

    private void OnGUI()
    {
        currentTab = GUILayout.Toolbar(currentTab, tabNames, GUILayout.Height(32));
        EditorGUILayout.Space(6);

        // 获取当前工具实例并绘制
        if (toolInstances.TryGetValue(currentTab, out var tool))
        {
            tool.DrawGUI();
        }
    }

    private void EnsureToolInstance(int index, System.Func<IToolGUI> factory)
    {
        if (!toolInstances.ContainsKey(index))
        {
            var instance = factory();
            //instance.OnEnable(); // 手动触发初始化
            toolInstances[index] = instance;
        }
    }
    // 3. 一键打开主窗口（菜单入口）
    [MenuItem("BjTools/多功能电饭煲", false, 10)]
    public static void OpenHub()
    {
        ToolManagerWindow window = GetWindow<ToolManagerWindow>();
        window.titleContent = new GUIContent("多功能电饭煲");
        window.Show();
    }
}
