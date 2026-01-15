using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace ToolSet
{
    public class PrefabMatProjectTool : EditorWindow ,IToolGUI
    {
        
        private GameObject source;
        private GameObject target;
    
        private GameObject source_skin;
        private GameObject target_skin;
        // private List<GameObject> fxPrefabs;
        // private List<string> fxPaths;
        private Dictionary<GameObject,string> fxPrefabs;
        private Dictionary<Material[], string> skinMats;
    
        private bool fxToggle = true;
        private bool matToggle = false;
        // [MenuItem("BjTools/预制体&材质映射工具")]
        // public static void ShowWindow()
        // {
        //     GetWindow<PrefabMatProjectTool>("预制体&材质映射工具");
        // }
    
        public void DrawGUI() 
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("预制体&材质映射工具",EditorStyles.boldLabel);
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("源根节点：");
            source = EditorGUILayout.ObjectField(source,typeof(GameObject)) as GameObject;
            EditorGUILayout.LabelField("目标根节点：");
            target = EditorGUILayout.ObjectField(target, typeof(GameObject)) as GameObject;
            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("预制体映射");
            fxToggle = EditorGUILayout.Toggle(fxToggle);
            EditorGUILayout.EndHorizontal();
           
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("材质映射");
            matToggle = EditorGUILayout.Toggle(matToggle);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("【注意】预制体映射会解包预制体在源根节点中的所有父级预制体。",EditorStyles.boldLabel);
            EditorGUILayout.Space(10);
            
            if (GUILayout.Button(("运行")))
            {
                Run();
            }
        }
    
        private void Run()
        {
            
            if (source == null)
            {
                Debug.LogError("未选择源根节点！");
                return;
            }
            if (target == null)
            {
                Debug.LogError("未选择目标根节点！");
                return;
            }
    
            if (fxToggle)
            {
                ProjectFx();
            }
                
            if (matToggle)
            {
                ProjectMat();
            }
    
        }
    
        private void GetSkinnedMeshRenderers(GameObject src)
        {
            skinMats = new Dictionary<Material[], string>();
            foreach (Renderer renderer in src.GetComponentsInChildren<Renderer>(true))
            {
                if(renderer.gameObject == src)
                    continue;
                string path = GetChildRelativePath(renderer.transform);
                Debug.Log(path);
                Material[] matList = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                {
                    matList[i] = renderer.sharedMaterials[i];
                }
                skinMats.Add(matList,path);
            }
        }
        
        public void GetPrefabAtChild(GameObject src)
        {
            Debug.Log("> GetPrefabAtChild");
            fxPrefabs = new Dictionary<GameObject, string>();
            
            foreach (Transform fx in src.GetComponentsInChildren<Transform>(true))
            {
                
                if (PrefabUtility.IsAnyPrefabInstanceRoot(fx.gameObject))
                {
                    
                    while (PrefabUtility.IsPartOfPrefabInstance(fx.gameObject) && 
                           PrefabUtility.GetOutermostPrefabInstanceRoot(fx.gameObject) != fx.gameObject)
                    {
                        var ins = PrefabUtility.GetOutermostPrefabInstanceRoot(fx.gameObject);
                        PrefabUtility.UnpackPrefabInstance(ins,PrefabUnpackMode.OutermostRoot,InteractionMode.AutomatedAction);
                    }
                   
                    if (fx != src.transform)
                    {
                        string path = GetChildRelativePath(fx);
                        // if (path == fx.name)
                        // {
                        //     path = path + "/";
                        // }
                        // else
                        // {
                        //     path = path.Substring(0, path.Length - fx.name.Length);
                        // }
                        path = path.Substring(0, path.Length - fx.name.Length);
                        //Debug.Log(path);
                        fxPrefabs.Add(fx.gameObject,path);
                    }
                    
                }
            }
        }
    
        private void ProjectFx()
        {
            GetPrefabAtChild(source);
            
            foreach (var VARIABLE in fxPrefabs.Keys)
            {
                Transform child = FindChild(target.transform,fxPrefabs[VARIABLE]);
               
                if (child != null)
                {
                    //Debug.LogWarning(VARIABLE.name);
                    
                    GameObject src = PrefabUtility.GetCorrespondingObjectFromSource(VARIABLE) as GameObject;
                    PrefabUtility.InstantiatePrefab(src, child);
                }
                else
                {
                    Debug.LogError("child is null");
                }
                
            }
        }
    
        private void ProjectMat()
        {
            GetSkinnedMeshRenderers(source);
            foreach (var VARIABLE in skinMats.Keys)
            {
                Transform child = FindChild(target.transform, skinMats[VARIABLE]);
    
                if (child != null)
                {
                    SkinnedMeshRenderer renderer = child.GetComponent<SkinnedMeshRenderer>();
                    if (renderer != null)
                    {
                        renderer.sharedMaterials = VARIABLE;
                    }
                    else
                    {
                        Debug.LogWarning($"Renderer not found.Name is: +{child.name}");
                    }
                        
                }
            }
        }
        
        // 获取子对象相对于其直接父对象的路径
        private string GetChildRelativePath(Transform child)
        {
            Debug.Log("> GetChildRelativePath");
            if (child.parent == null)
                return ""; // 如果没有父对象，则返回根目录符号或者你可以选择返回空字符串或其他标识符
            
            string path = child.name;
            Transform parent = child.parent;
            
            while(parent != null) // 继续向上直到找到最顶层的父对象
            {
                if (parent.gameObject == source.gameObject)
                {
                    path = "<root>" + "/" + path;
                    break;
                }
                    
                path = parent.name + "/" + path;
    
                parent = parent.parent;
            }
            Debug.Log("ChildRelativePath : " + path);
            // 如果需要的是相对于直接父对象的路径，可以简化上面的循环逻辑
            // 这里我们返回的是从根到该对象的完整路径
            return path;
        }
        // 根据路径字符串查找子对象
        private Transform FindChild(Transform root, string path)
        {
            Debug.Log("> FindChild");
            Transform parent = root;
            // 如果路径为空或者parent为null，则直接返回null
            if (string.IsNullOrEmpty(path) || parent == null)
            {
                Debug.LogError("path or parent is null");
                return null;
            }
                
            
            // 按照'/'分割路径
            string[] parts = path.Split('/');
    
            //bool skip1 = true;
            // 遍历每个部分
            foreach (string part in parts)
            {
                // if (skip1)
                // {
                //     skip1 = false;
                //     continue;
                // }
                
                // 尝试找到名字匹配的子对象
                //Debug.LogWarning(part);
                if (part == "<root>")
                {
                    continue;
                }
                Transform child = parent.Find(part);
                if (child == null)
                {
                    Debug.LogWarning($"Child not found, path is: {path}");
                    return null; // 如果找不到，返回null
                }
                    
                else
                    parent = child; // 向下一层搜索
            }
            Debug.Log($"Child found, path is: {path}");
            return parent; // 返回最终找到的对象
        }
    
       
    }
}


