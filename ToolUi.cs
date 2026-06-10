using UnityEditor;
using UnityEngine;

namespace ToolSet
{
    internal static class ToolUi
    {
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
            foldout = EditorGUILayout.Foldout(foldout, title, true);
            if (foldout)
            {
                EditorGUILayout.Space(2);
                drawContent?.Invoke();
            }

            EditorGUILayout.EndVertical();
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
