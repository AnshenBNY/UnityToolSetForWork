using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    internal static class ToolUi
    {
        public static void DrawPager(ref int page, int count, int size)
        {
            int last = Mathf.Max(0, (count - 1) / size);
            page = Mathf.Clamp(page, 0, last);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("上一页", GUILayout.Width(65))) page = Mathf.Max(0, page - 1);
                GUILayout.Label($"{page + 1}/{last + 1} 页 · {count} 项");
                if (GUILayout.Button("下一页", GUILayout.Width(65))) page = Mathf.Min(last, page + 1);
            }
        }
        private static GUIStyle titleStyle;
        private static GUIStyle sectionStyle;
        private static GUIStyle statusStyle;

        private static void EnsureStyles()
        {
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(EditorStyles.largeLabel)
                {
                    fontStyle = FontStyle.Bold
                };
            }

            if (sectionStyle == null)
            {
                sectionStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12
                };
            }

            if (statusStyle == null)
            {
                statusStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    fontSize = 11,
                    wordWrap = true
                };
            }
        }

        public static void DrawToolHeader(string title, string desc = null)
        {
            EnsureStyles();
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(title, titleStyle);
            if (!string.IsNullOrWhiteSpace(desc))
            {
                EditorGUILayout.HelpBox(desc, MessageType.Info);
            }
        }

        public static void BeginCard(string title)
        {
            EnsureStyles();
            EditorGUILayout.BeginVertical("box");
            if (!string.IsNullOrWhiteSpace(title))
            {
                EditorGUILayout.LabelField(title, sectionStyle);
                EditorGUILayout.Space(2);
            }
        }

        public static void EndCard()
        {
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        public static void DrawFoldoutCard(string title, ref bool foldout, System.Action drawContent)
        {
            EnsureStyles();
            EditorGUILayout.BeginVertical("box");
            try
            {
                foldout = EditorGUILayout.Foldout(foldout, title, true);
                if (foldout)
                {
                    EditorGUILayout.Space(2);
                    drawContent?.Invoke();
                }
            }
            finally { EditorGUILayout.EndVertical(); }
            EditorGUILayout.Space(4);
        }

        public static void DrawStatus(string status, MessageType type = MessageType.None)
        {
            EnsureStyles();
            if (type == MessageType.None)
            {
                GUILayout.Label($"状态：{status}", statusStyle);
            }
            else
            {
                EditorGUILayout.HelpBox($"状态：{status}", type);
            }
        }
    }
}
