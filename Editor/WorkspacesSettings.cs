using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace WillGoldstone.Workspaces
{
    [Serializable]
    internal class WorkspaceDefinition
    {
        [SerializeField] private string id = Guid.NewGuid().ToString("N");
        [SerializeField] private string displayName = "Prefab";
        [SerializeField] private string prefabGuid = string.Empty;
        [SerializeField] private string prefabLayoutPath = string.Empty;
        [SerializeField] private string homeLayoutPath = string.Empty;
        [SerializeField] private bool keepSelection = true;
        [SerializeField] private bool isGameViewWorkspace = false;

        internal string Id
        {
            get => string.IsNullOrEmpty(id) ? (id = Guid.NewGuid().ToString("N")) : id;
            set => id = value;
        }

        internal string DisplayName
        {
            get => displayName;
            set => displayName = value;
        }

        internal string PrefabGuid
        {
            get => prefabGuid;
            set => prefabGuid = value;
        }

        internal string PrefabLayoutPath
        {
            get => prefabLayoutPath;
            set => prefabLayoutPath = value;
        }

        internal string HomeLayoutPath
        {
            get => homeLayoutPath;
            set => homeLayoutPath = value;
        }

        internal bool KeepSelection
        {
            get => keepSelection;
            set => keepSelection = value;
        }

        internal bool IsGameViewWorkspace
        {
            get => isGameViewWorkspace;
            set => isGameViewWorkspace = value;
        }

        internal string PrefabPath => string.IsNullOrEmpty(prefabGuid) ? null : AssetDatabase.GUIDToAssetPath(prefabGuid);
    }

    [FilePath("ProjectSettings/WillGoldstone.Workspaces.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class WorkspacesSettings : ScriptableSingleton<WorkspacesSettings>
    {
        [SerializeField] private WorkspaceDefinition homeWorkspace;
        [SerializeField] private List<WorkspaceDefinition> workspaces = new List<WorkspaceDefinition>();

        internal IReadOnlyList<WorkspaceDefinition> Workspaces => workspaces.Where(w => w.Id != "home").ToList();
        
        // Internal access for Settings UI
        internal List<WorkspaceDefinition> WorkspacesList => workspaces;

        internal void EnsureDefaults()
        {
            // Don't modify properties during tab switches to prevent Settings window from opening
            if (isSwitchingTabs)
            {
                // Just ensure collections exist, but don't modify anything
                if (workspaces == null)
                {
                    workspaces = new List<WorkspaceDefinition>();
                }
                if (homeWorkspace == null)
                {
                    homeWorkspace = new WorkspaceDefinition
                    {
                        DisplayName = "Home",
                        Id = "home",
                        KeepSelection = true
                    };
                }
                return;
            }
            
            if (workspaces == null)
            {
                workspaces = new List<WorkspaceDefinition>();
            }

            if (homeWorkspace == null)
            {
                homeWorkspace = new WorkspaceDefinition
                {
                    DisplayName = "Home",
                    Id = "home",
                    KeepSelection = true
                };
            }

            // Ensure Home has the correct ID
            if (homeWorkspace.Id != "home")
            {
                homeWorkspace.Id = "home";
            }

            // Remove any Home entries from the regular workspaces list
            workspaces.RemoveAll(w => w.Id == "home");

            if (workspaces.Count == 0)
            {
                // Only seed the template list when there is no real settings file yet. After a domain
                // reload (script recompile), the in-memory list can be empty briefly while the asset on
                // disk still holds your workspaces — ResetToDefaults() here previously overwrote that file.
                if (ShouldSeedFirstRunDefaultsBecauseNoSettingsFile())
                    ResetToDefaults();
            }

            ValidateIds();
        }

        private static string SettingsFileAbsolutePath =>
            Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, "ProjectSettings", "WillGoldstone.Workspaces.asset");

        private static bool ShouldSeedFirstRunDefaultsBecauseNoSettingsFile()
        {
            try
            {
                if (!File.Exists(SettingsFileAbsolutePath))
                    return true;
                return new FileInfo(SettingsFileAbsolutePath).Length < 96;
            }
            catch
            {
                return true;
            }
        }

        internal void ResetToDefaults()
        {
            homeWorkspace = new WorkspaceDefinition
            {
                DisplayName = "Home",
                Id = "home",
                KeepSelection = true
            };

            workspaces = new List<WorkspaceDefinition>
            {
                new WorkspaceDefinition
                {
                    DisplayName = "Prefab",
                    KeepSelection = true
                }
            };

            ValidateIds();
            // Don't auto-save - user must click Save button in Project Settings
        }

        internal WorkspaceDefinition GetHome()
        {
            EnsureDefaults();
            return homeWorkspace;
        }

        internal void ValidateIds()
        {
            var seen = new HashSet<string>();
            foreach (var workspace in workspaces)
            {
                // Never allow prefab workspaces to steal the reserved Home id.
                if (workspace.Id == "home")
                {
                    workspace.Id = Guid.NewGuid().ToString("N");
                }

                if (string.IsNullOrEmpty(workspace.Id) || !seen.Add(workspace.Id))
                {
                    workspace.Id = Guid.NewGuid().ToString("N");
                }
            }
        }

        internal WorkspaceDefinition FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id == "home") return homeWorkspace;
            return workspaces.FirstOrDefault(w => w.Id == id);
        }

        internal WorkspaceDefinition FirstOrDefault()
        {
            return workspaces.FirstOrDefault();
        }

        /// <summary>
        /// Sets the specified workspace as the Game View workspace (used during Play mode).
        /// Only one workspace can be the Game View workspace at a time.
        /// </summary>
        internal void SetAsGameViewWorkspace(WorkspaceDefinition target)
        {
            // Clear flag from all workspaces
            if (homeWorkspace != null) homeWorkspace.IsGameViewWorkspace = false;
            foreach (var ws in workspaces)
            {
                ws.IsGameViewWorkspace = false;
            }
            
            // Set on target
            if (target != null)
            {
                target.IsGameViewWorkspace = true;
            }
        }

        /// <summary>
        /// Gets the workspace designated as the Game View workspace, or null if none.
        /// </summary>
        internal WorkspaceDefinition GetGameViewWorkspace()
        {
            if (homeWorkspace != null && homeWorkspace.IsGameViewWorkspace)
                return homeWorkspace;
            
            return workspaces.FirstOrDefault(w => w.IsGameViewWorkspace);
        }

        private static bool isSwitchingTabs = false;
        private static bool allowAutoSave = false; // Only allow saves when explicitly requested from UI

        // Hide the base Save() method to prevent Unity from auto-saving
        // Only save when explicitly requested via SaveSettingsExplicit()
        public new void Save(bool saveAsText = true)
        {
            // Never auto-save - only save when explicitly requested from Project Settings UI
            if (!allowAutoSave)
            {
                return;
            }
            
            // Call base Save() only when explicitly allowed
            base.Save(saveAsText);
            allowAutoSave = false; // Reset flag after save
        }

        internal void SaveSettings()
        {
            // This method is kept for compatibility but now just calls Save()
            SaveSettingsExplicit();
        }
        
        internal void SaveSettingsExplicit()
        {
            // Explicit save from UI - allow it
            allowAutoSave = true;
            Save(false); // Use Save(false) to avoid triggering settings window refresh

            WorkspacesToolbarCodeGenerator.RegenerateAfterSave();
            
            // Refresh toolbar after saving / codegen (script compile may follow)
            EditorApplication.delayCall += () =>
            {
                EditorApplication.delayCall += () =>
                {
                    WorkspacesToolbar.RefreshToolbar();
                    WorkspacesToolbar.RefreshAllWorkspaceToolbarButtons();
                    
                    var currentCount = instance.Workspaces.Count;
                    if (currentCount > 0)
                    {
                        Debug.Log($"[Workspaces] Settings saved. {currentCount} prefab workspace(s). Toolbar registrations were regenerated; wait for script compile if the Main Toolbar does not update immediately.");
                    }
                };
            };
        }

        internal static void SetSwitchingTabs(bool switching)
        {
            isSwitchingTabs = switching;
        }
        
        internal static bool IsSwitchingTabs()
        {
            return isSwitchingTabs;
        }

        #region Settings Provider

        private static ReorderableList reorderableList;

        [SettingsProvider]
        private static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider("Project/Workspaces", SettingsScope.Project)
            {
                label = "Workspaces",
                guiHandler = searchContext =>
                {
                    var settings = instance;
                    settings.EnsureDefaults();

                    var serializedObject = new SerializedObject(settings);
                    var homeProp = serializedObject.FindProperty("homeWorkspace");
                    var workspacesProp = serializedObject.FindProperty("workspaces");

                    if (reorderableList == null || reorderableList.serializedProperty != workspacesProp)
                    {
                        reorderableList = CreateList(serializedObject, workspacesProp);
                    }

                    serializedObject.Update();

                    EditorGUILayout.HelpBox("Configure the Home tab and prefab tabs for the Workspaces toolbar. Layouts are automatically saved when switching between workspaces. Use 'Update Workspace Layout' to manually save the current window arrangement. Enable 'Game View Workspace' on one workspace to auto-switch to it during Play mode.\n\nEach prefab row gets its own Main Toolbar button and Add (+) menu entry (label uses Display Name + id). Click **Save** to regenerate toolbar code when you add, remove, or rename workspaces.", MessageType.Info);
                    
                    // Home section
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Home Tab", EditorStyles.boldLabel);
                    DrawHomeWorkspace(serializedObject, homeProp);
                    
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Prefab Tabs", EditorStyles.boldLabel);
                    reorderableList.DoLayoutList();

                    if (serializedObject.ApplyModifiedProperties())
                    {
                        settings.ValidateIds();
                        // Don't auto-save - user must click Save button
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Reset to defaults"))
                        {
                            settings.ResetToDefaults();
                            serializedObject.Update();
                            reorderableList = CreateList(serializedObject, serializedObject.FindProperty("workspaces"));
                            // Don't auto-save after reset - user must click Save button
                        }

                        if (GUILayout.Button("Save"))
                        {
                            settings.ValidateIds();
                            settings.SaveSettingsExplicit();
                        }
                    }
                },
                keywords = new HashSet<string>(new[] { "Workspaces", "Tabs", "Prefab", "Layout", "Toolbar" })
            };

            return provider;
        }

        private static void DrawHomeWorkspace(SerializedObject serializedObject, SerializedProperty homeProp)
        {
            var nameProp = homeProp.FindPropertyRelative("displayName");
            var keepProp = homeProp.FindPropertyRelative("keepSelection");
            var isGameViewProp = homeProp.FindPropertyRelative("isGameViewWorkspace");

            EditorGUILayout.PropertyField(nameProp, new GUIContent("Display Name"));
            
            // Show auto-generated layout path (read-only)
            var homeWorkspace = instance.GetHome();
            var layoutPath = WorkspacesLayoutUtility.GetLayoutPathForWorkspace(homeWorkspace);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Layout File", layoutPath);
            }
            
            // Update Workspace Layout button
            if (GUILayout.Button("Update Workspace Layout", GUILayout.Width(200)))
            {
                WorkspacesLayoutUtility.SaveLayout(layoutPath);
                EditorUtility.DisplayDialog("Layout Saved", $"Layout saved to:\n{layoutPath}", "OK");
            }
            
            EditorGUILayout.PropertyField(keepProp, new GUIContent("Keep Selection"));
            
            // Game View workspace toggle with mutual exclusivity
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(isGameViewProp, new GUIContent("Game View Workspace", "Automatically switch to this workspace when entering Play mode"));
            if (EditorGUI.EndChangeCheck() && isGameViewProp.boolValue)
            {
                // Clear the flag from all other workspaces
                instance.SetAsGameViewWorkspace(homeWorkspace);
                serializedObject.Update();
            }
        }

        private static ReorderableList CreateList(SerializedObject serializedObject, SerializedProperty property)
        {
            var list = new ReorderableList(serializedObject, property, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Workspaces"),
                elementHeightCallback = index =>
                {
                    var element = property.GetArrayElementAtIndex(index);
                    var idProp = element.FindPropertyRelative("id");
                    var isHome = idProp.stringValue == "home";
                    // name, prefab, layout file (read-only), update button, keep selection, game view toggle
                    return (EditorGUIUtility.singleLineHeight + 2f) * (isHome ? 5 : 6) + 6f;
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    var element = property.GetArrayElementAtIndex(index);
                    var idProp = element.FindPropertyRelative("id");
                    var nameProp = element.FindPropertyRelative("displayName");
                    var keepProp = element.FindPropertyRelative("keepSelection");
                    var prefabGuidProp = element.FindPropertyRelative("prefabGuid");
                    var isGameViewProp = element.FindPropertyRelative("isGameViewWorkspace");
                    
                    var isHome = idProp.stringValue == "home";
                    
                    // Get the workspace definition for this element
                    WorkspaceDefinition workspace = null;
                    if (isHome)
                    {
                        workspace = instance.GetHome();
                    }
                    else if (index >= 0 && index < instance.WorkspacesList.Count)
                    {
                        workspace = instance.WorkspacesList[index];
                    }

                    rect.y += 2f;
                    rect.height = EditorGUIUtility.singleLineHeight;

                    EditorGUI.PropertyField(rect, nameProp, new GUIContent("Display Name"));
                    rect.y += EditorGUIUtility.singleLineHeight + 2f;

                    if (!isHome)
                    {
                        // Prefab field (only for non-Home workspaces)
                        var prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuidProp.stringValue);
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        var newPrefab = (GameObject)EditorGUI.ObjectField(rect, "Prefab", prefab, typeof(GameObject), false);
                        if (newPrefab != prefab)
                        {
                            prefabGuidProp.stringValue = newPrefab == null ? string.Empty : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(newPrefab));
                        }
                        rect.y += EditorGUIUtility.singleLineHeight + 2f;
                    }

                    // Show auto-generated layout path (read-only)
                    if (workspace != null)
                    {
                        var layoutPath = WorkspacesLayoutUtility.GetLayoutPathForWorkspace(workspace);
                        var labelRect = rect;
                        labelRect.width = EditorGUIUtility.labelWidth;
                        EditorGUI.LabelField(labelRect, "Layout File");
                        
                        var fieldRect = rect;
                        fieldRect.x += EditorGUIUtility.labelWidth;
                        fieldRect.width -= EditorGUIUtility.labelWidth;
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUI.TextField(fieldRect, layoutPath);
                        }
                        rect.y += EditorGUIUtility.singleLineHeight + 2f;

                        // Update Workspace Layout button
                        var buttonRect = rect;
                        buttonRect.width = 200;
                        if (GUI.Button(buttonRect, "Update Workspace Layout"))
                        {
                            WorkspacesLayoutUtility.SaveLayout(layoutPath);
                            EditorUtility.DisplayDialog("Layout Saved", $"Layout saved to:\n{layoutPath}", "OK");
                        }
                        rect.y += EditorGUIUtility.singleLineHeight + 2f;
                    }

                    EditorGUI.PropertyField(rect, keepProp, new GUIContent("Keep Selection"));
                    rect.y += EditorGUIUtility.singleLineHeight + 2f;

                    // Game View workspace toggle with mutual exclusivity
                    EditorGUI.BeginChangeCheck();
                    EditorGUI.PropertyField(rect, isGameViewProp, new GUIContent("Game View Workspace", "Automatically switch to this workspace when entering Play mode"));
                    if (EditorGUI.EndChangeCheck() && isGameViewProp.boolValue && workspace != null)
                    {
                        // Clear the flag from all other workspaces
                        instance.SetAsGameViewWorkspace(workspace);
                        serializedObject.Update();
                    }
                }
            };

            return list;
        }

        #endregion
    }
}

