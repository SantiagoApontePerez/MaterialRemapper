using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

#if UNITY_EDITOR
namespace MaterialRemapper
{
    public class MaterialRemapper : EditorWindow
    {
        #region Private Fields

        // Folder containing .mat assets
        private DefaultAsset _materialsFolder;
        // Name prefix to prepend
        private string _prefix = "M_";

        #endregion

        [MenuItem("Tools/Material Remapper")]
        private static void OpenWindow() => GetWindow<MaterialRemapper>(true, "Material Remapper");

        #region Editor Window
        
        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

            _materialsFolder = (DefaultAsset)EditorGUILayout.ObjectField("Materials Folder", _materialsFolder, typeof(DefaultAsset), false);
            _prefix = EditorGUILayout.TextField("Prefix", _prefix);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "1. Select one or more FBX / Prefab assets in the Project window.\n" +
                "2. Choose the folder with the target .mat files.\n" +
                "3. Enter the prefix those materials use (default: M_).\n" +
                "4. Click the button below to remap.", MessageType.Info);

            var hasSelection = Selection.objects.Length > 0;
            using (new EditorGUI.DisabledScope(!_materialsFolder || !hasSelection))
            {
                if (GUILayout.Button("Remap Selected Assets", GUILayout.Height(32)))
                    RemapSelected();
            }
        }
        
        #endregion

        #region Private Methods
        
        private void RemapSelected()
        {
            var folderPath = AssetDatabase.GetAssetPath(_materialsFolder);
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                Debug.LogError("[MaterialRemapper] Invalid materials folder selected.");
                return;
            }
            
            var lookup = BuildMaterialLookup(folderPath);
            if (lookup.Count == 0)
            {
                Debug.LogWarning("[MaterialRemapper] No .mat assets found in the selected folder.");
                return;
            }

            AssetDatabase.StartAssetEditing();
            TryEditAssets(lookup);
        }

        private void TryEditAssets(Dictionary<string,Material> lookup)
        {
            try
            {
                EditSelection(lookup);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
        }

        private void EditSelection(Dictionary<string, Material> lookup)
        {
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                SwitchObjectType(path, lookup);
            }
        }

        private void SwitchObjectType(string path, Dictionary<string,Material> lookup)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".prefab":
                    RemapPrefab(path, lookup);
                    break;
                case ".fbx":
                case ".obj":
                    RemapModel(path, lookup);
                    break;
                default:
                    break;
            }
        }
        
        private Dictionary<string, Material> BuildMaterialLookup(string folderPath)
        {
            var dict  = new Dictionary<string, Material>();
            var guids = AssetDatabase.FindAssets("t:Material", new[] { folderPath });
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var mat     = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (mat && !dict.ContainsKey(mat.name))
                    dict.Add(mat.name, mat);
            }
            return dict;
        }
        
        private void RemapPrefab(string path, IReadOnlyDictionary<string, Material> lookup)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            if (!root) return;

            var changed = RemapInHierarchy(root, lookup);
            if (changed)
            {
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[MaterialRemapper] Remapped materials in prefab: {path}");
            }
            PrefabUtility.UnloadPrefabContents(root);
        }
        
        private void RemapModel(string path, IReadOnlyDictionary<string, Material> lookup)
        {
            // 1) Instantiate the model temporarily to discover the renderer‑level material names.
            var modelRoot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            if (!modelRoot) return;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (!importer)
            {
                DestroyImmediate(modelRoot);
                Debug.LogWarning($"[MaterialRemapper] Could not obtain ModelImporter for {path}");
                return;
            }

            var changed = false;
            // Copy existing map so we don't double‑add.
            var externalMap = importer.GetExternalObjectMap();

            foreach (var renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var srcMat in renderer.sharedMaterials)
                {
                    if (srcMat == null) continue;

                    var internalName = srcMat.name;                                   // name inside the FBX
                    var targetName   = internalName.StartsWith(_prefix)
                                         ? internalName
                                         : _prefix + internalName;                      // expected .mat name on disk

                    if (!lookup.TryGetValue(targetName, out Material replacement))
                        continue; // no matching .mat found in folder

                    var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), internalName);
                    if (externalMap.TryGetValue(id, out Object existing) && existing == replacement)
                        continue; // already mapped to the correct material

                    importer.AddRemap(id, replacement);
                    changed = true;
                }
            }

            DestroyImmediate(modelRoot); // clean‑up temp instance

            if (!changed) return;
            importer.SaveAndReimport();
            Debug.Log($"[MaterialRemapper] Remapped materials in FBX: {path}");
        }
        
        #endregion

        #region Helpers

        private bool RemapInHierarchy(GameObject root, IReadOnlyDictionary<string, Material> lookup)
        {
            var changed = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var current = mats[i];
                    if (!current) continue;

                    var targetName = current.name.StartsWith(_prefix) ? current.name : _prefix + current.name;
                    if (!lookup.TryGetValue(targetName, out var replacement) || replacement == current) continue;
                    mats[i] = replacement;
                    changed = true;
                }
                if (changed)
                    renderer.sharedMaterials = mats; // push array back to renderer
            }
            return changed;
        }

        #endregion
    }
}
#endif
