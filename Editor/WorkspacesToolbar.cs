using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace WillGoldstone.Workspaces
{
    internal static class WorkspacesLayoutUtility
    {
        internal static void LoadLayout(string layoutPath)
        {
            if (string.IsNullOrEmpty(layoutPath))
            {
                Debug.LogWarning("[Workspaces] Layout path is empty. Assign a layout file in Project Settings > Workspaces.");
                return;
            }

            var absolutePath = Path.IsPathRooted(layoutPath)
                ? layoutPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", layoutPath));

            if (!File.Exists(absolutePath))
            {
                Debug.LogWarning($"[Workspaces] Layout file not found: {absolutePath}");
                return;
            }

            EditorUtility.LoadWindowLayout(absolutePath);
        }

        internal static bool TryGetCurrentLayoutPath(out string path)
        {
            path = null;

            // Try internal WindowLayout fields/properties first.
            var windowLayoutType = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
            if (windowLayoutType != null)
            {
                var field = windowLayoutType.GetField("s_CurrentLayoutPath", BindingFlags.Static | BindingFlags.NonPublic);
                if (field != null)
                {
                    path = field.GetValue(null) as string;
                    if (IsValidLayout(path)) return true;
                }

                var prop = windowLayoutType.GetProperty("currentLayoutPath", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (prop != null)
                {
                    path = prop.GetValue(null, null) as string;
                    if (IsValidLayout(path)) return true;
                }
            }

            // Known EditorPrefs keys Unity uses to store layout paths.
            string[] keys =
            {
                "CurrentLayoutPath",
                "LastLayoutPath",
                "LastUsedLayoutPath",
                "kLastLoadedLayoutPath"
            };

            foreach (var key in keys)
            {
                var candidate = EditorPrefs.GetString(key, string.Empty);
                if (IsValidLayout(candidate))
                {
                    path = candidate;
                    return true;
                }
            }

            path = null;
            return false;
        }

        private static bool IsValidLayout(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        private static MethodInfo _miSaveWindowLayout;
        private static bool _reflectionInitialized = false;

        private static void InitializeReflection()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;

            var windowLayoutType = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
            if (windowLayoutType != null)
            {
                _miSaveWindowLayout = windowLayoutType.GetMethod("SaveWindowLayout",
                    BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            }
        }

        /// <summary>
        /// Saves the current window layout to the specified path.
        /// </summary>
        internal static void SaveLayout(string layoutPath)
        {
            if (string.IsNullOrEmpty(layoutPath))
            {
                Debug.LogWarning("[Workspaces] Cannot save layout: path is empty.");
                return;
            }

            InitializeReflection();

            if (_miSaveWindowLayout == null)
            {
                Debug.LogWarning("[Workspaces] Cannot save layout: SaveWindowLayout method not found via reflection.");
                return;
            }

            try
            {
                // Ensure the directory exists
                var directory = Path.GetDirectoryName(layoutPath);
                var absoluteDirectory = Path.IsPathRooted(directory)
                    ? directory
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", directory));

                if (!Directory.Exists(absoluteDirectory))
                {
                    Directory.CreateDirectory(absoluteDirectory);
                }

                // Convert to absolute path for saving
                var absolutePath = Path.IsPathRooted(layoutPath)
                    ? layoutPath
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", layoutPath));

                _miSaveWindowLayout.Invoke(null, new object[] { absolutePath });
                
                // Refresh AssetDatabase if the file is in Assets folder
                if (layoutPath.StartsWith("Assets/"))
                {
                    AssetDatabase.Refresh();
                }

                WorkspacesLayoutToolbarCleanup.StripLegacyAfterLayoutSave(layoutPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Workspaces] Failed to save layout: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the directory where workspace layouts are stored.
        /// </summary>
        internal static string GetLayoutDirectory() => "Assets/Workspaces/Layouts";

        /// <summary>
        /// Gets a safe filename from a display name (removes invalid characters).
        /// </summary>
        private static string GetSafeFileName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return "Workspace";
            
            var invalid = Path.GetInvalidFileNameChars();
            var safeName = new string(displayName.Where(c => !invalid.Contains(c) && c != ' ').ToArray());
            return string.IsNullOrEmpty(safeName) ? "Workspace" : safeName;
        }

        /// <summary>
        /// Gets the auto-generated layout path for a workspace.
        /// </summary>
        internal static string GetLayoutPathForWorkspace(WorkspaceDefinition workspace)
        {
            if (workspace == null)
            {
                return $"{GetLayoutDirectory()}/Unknown.wlt";
            }

            var safeName = GetSafeFileName(workspace.DisplayName);
            var idPrefix = workspace.Id.Length >= 8 ? workspace.Id.Substring(0, 8) : workspace.Id;
            return $"{GetLayoutDirectory()}/{safeName}_{idPrefix}.wlt";
        }

        /// <summary>
        /// Checks if a layout file exists for the given workspace.
        /// </summary>
        internal static bool LayoutExistsForWorkspace(WorkspaceDefinition workspace)
        {
            var layoutPath = GetLayoutPathForWorkspace(workspace);
            var absolutePath = Path.IsPathRooted(layoutPath)
                ? layoutPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", layoutPath));
            return File.Exists(absolutePath);
        }
    }

    [InitializeOnLoad]
    internal static class WorkspacesState
    {
        private const string SessionKey = "WillGoldstone.Workspaces.ActiveId";
        /// <summary>
        /// After the first <see cref="RestoreFromSession"/> in an editor session, script recompiles
        /// cause domain reload but we only sync the active workspace reference — we must not reload
        /// the saved layout each time or the user loses window focus and layout state.
        /// </summary>
        private const string SessionRestoreCompletedKey = "WillGoldstone.Workspaces.SessionRestoreCompleted";

        private static WorkspaceDefinition active;
        private static readonly Dictionary<string, int[]> cachedSelections = new Dictionary<string, int[]>();
        private static readonly Dictionary<string, string> cachedLayoutPaths = new Dictionary<string, string>();
        private static readonly Dictionary<string, List<int>> cachedExpandedHierarchyItems = new Dictionary<string, List<int>>();
        private static readonly Dictionary<string, string> runtimeLayoutPaths = new Dictionary<string, string>(); // Store layout paths without modifying workspace
        private static readonly Dictionary<string, List<string>> cachedPrefabGameObjectPaths = new Dictionary<string, List<string>>(); // Store prefab GameObject paths (transform paths relative to prefab root)

        internal static event Action<WorkspaceDefinition> ActiveWorkspaceChanged;

        internal static WorkspaceDefinition Active => active;

        // Play mode tracking - skip layout restoration during play mode to avoid interfering with Play Maximized etc.
        private static bool isInPlayModeTransition = false;
        private static bool wasInPlayMode = false;
        
        // Track the editing workspace to return to after play mode
        // Use SessionState to persist across domain reloads (which happen when exiting play mode)
        private const string LastEditingWorkspaceKey = "WillGoldstone.Workspaces.LastEditingWorkspaceId";
        private const string GameViewWorkspaceActiveKey = "WillGoldstone.Workspaces.GameViewWorkspaceActive";
        
        private static string LastEditingWorkspaceId
        {
            get => SessionState.GetString(LastEditingWorkspaceKey, null);
            set => SessionState.SetString(LastEditingWorkspaceKey, value ?? string.Empty);
        }
        
        private static bool IsGameViewWorkspaceActive
        {
            get => SessionState.GetBool(GameViewWorkspaceActiveKey, false);
            set => SessionState.SetBool(GameViewWorkspaceActiveKey, value);
        }

        static WorkspacesState()
        {
            // Unsubscribe first to prevent duplicate subscriptions after domain reload
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= DetectLayoutChange;
            
            // Subscribe to events
            EditorApplication.delayCall += RestoreFromSession;
            EditorApplication.update += DetectLayoutChange;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            
            // Track initial play mode state
            wasInPlayMode = EditorApplication.isPlaying;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            var settings = WorkspacesSettings.instance;
            settings.EnsureDefaults();
            var gameViewWorkspace = settings.GetGameViewWorkspace();

            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // About to enter play mode - set transition flag
                    isInPlayModeTransition = true;
                    
                    // Get the current workspace - use static variable if set, otherwise fallback to SessionState
                    var currentWorkspace = active;
                    if (currentWorkspace == null)
                    {
                        var savedId = SessionState.GetString(SessionKey, "home");
                        currentWorkspace = settings.FindById(savedId) ?? settings.GetHome();
                    }
                    
                    // If there's a Game View workspace configured, cache current workspace and switch to it
                    if (gameViewWorkspace != null && currentWorkspace != null)
                    {
                        // Only switch if we're not already on the game view workspace
                        if (currentWorkspace.Id != gameViewWorkspace.Id)
                        {
                            LastEditingWorkspaceId = currentWorkspace.Id;
                            IsGameViewWorkspaceActive = true;
                            
                            // Switch to Game View workspace (allow layout change for this specific case)
                            ActivateForPlayMode(gameViewWorkspace);
                        }
                    }
                    break;
                    
                case PlayModeStateChange.ExitingPlayMode:
                    // About to exit play mode - set transition flag
                    isInPlayModeTransition = true;
                    break;
                    
                case PlayModeStateChange.EnteredPlayMode:
                    // Finished entering play mode - clear transition flag after a delay
                    EditorApplication.delayCall += () =>
                    {
                        EditorApplication.delayCall += () =>
                        {
                            isInPlayModeTransition = false;
                            wasInPlayMode = true;
                        };
                    };
                    break;
                    
                case PlayModeStateChange.EnteredEditMode:
                    // Finished exiting play mode - restore the editing workspace
                    EditorApplication.delayCall += () =>
                    {
                        EditorApplication.delayCall += () =>
                        {
                            isInPlayModeTransition = false;
                            wasInPlayMode = false;
                            
                            // If we switched to Game View workspace, return to the previous editing workspace
                            // Note: Get fresh settings reference after domain reload
                            var lastWorkspaceId = LastEditingWorkspaceId;
                            
                            if (IsGameViewWorkspaceActive && !string.IsNullOrEmpty(lastWorkspaceId))
                            {
                                var freshSettings = WorkspacesSettings.instance;
                                freshSettings.EnsureDefaults();
                                var editingWorkspace = freshSettings.FindById(lastWorkspaceId);
                                
                                if (editingWorkspace != null)
                                {
                                    IsGameViewWorkspaceActive = false;
                                    LastEditingWorkspaceId = null;
                                    ActivateForPlayMode(editingWorkspace);
                                }
                                else
                                {
                                    // Clear the flags even if workspace not found
                                    IsGameViewWorkspaceActive = false;
                                    LastEditingWorkspaceId = null;
                                }
                            }
                        };
                    };
                    break;
            }
        }
        
        /// <summary>
        /// Activate a workspace specifically for play mode transitions (bypasses the skip layout check).
        /// </summary>
        private static void ActivateForPlayMode(WorkspaceDefinition target)
        {
            if (target == null) return;

            var isHome = target.Id == "home";

            // Switch to the target workspace
            if (isHome)
            {
                StageUtility.GoToMainStage();
            }
            else
            {
                var prefabPath = target.PrefabPath;
                if (!string.IsNullOrEmpty(prefabPath))
                {
                    PrefabStageUtility.OpenPrefab(prefabPath);
                }
            }

            // Set active BEFORE loading layout - layout loading recreates toolbar buttons,
            // and they need to see the correct active state when created
            active = target;
            SessionState.SetString(SessionKey, active.Id);

            // Load the workspace layout SYNCHRONOUSLY - delayCall won't survive domain reload
            var layoutPath = WorkspacesLayoutUtility.GetLayoutPathForWorkspace(target);
            if (WorkspacesLayoutUtility.LayoutExistsForWorkspace(target))
            {
                WorkspacesLayoutUtility.LoadLayout(layoutPath);
            }

            // Fire event after layout loaded (buttons may have been recreated)
            ActiveWorkspaceChanged?.Invoke(active);
        }

        /// <summary>
        /// Returns true if we should skip layout restoration (during play mode or transitions).
        /// </summary>
        private static bool ShouldSkipLayoutRestoration()
        {
            // Skip if currently in play mode
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                return true;
            
            // Skip if transitioning between play modes
            if (isInPlayModeTransition)
                return true;
            
            // Skip if we just exited play mode (domain reload case)
            // Check if this is a domain reload after play mode by detecting state change
            if (wasInPlayMode && !EditorApplication.isPlaying)
            {
                // We just came out of play mode - don't restore layout
                wasInPlayMode = false;
                return true;
            }
            
            return false;
        }

        private static string lastDetectedLayoutPath = string.Empty;
        private static bool isRestoringLayout = false;
        private static bool isMonitoringSettingsWindow = false;

        private static void MonitorSettingsWindow()
        {
            if (!WorkspacesSettings.IsSwitchingTabs())
            {
                if (isMonitoringSettingsWindow)
                {
                    EditorApplication.update -= MonitorSettingsWindow;
                    isMonitoringSettingsWindow = false;
                }
                return;
            }

            try
            {
                var settingsWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SettingsWindow");
                if (settingsWindowType != null)
                {
                    var windows = Resources.FindObjectsOfTypeAll(settingsWindowType);
                    foreach (var window in windows)
                    {
                        if (window is EditorWindow settingsWindow && settingsWindow != null)
                        {
                            // Close the window if it's open during tab switch
                            settingsWindow.Close();
                        }
                    }
                }
            }
            catch { }
        }

        private static void DetectLayoutChange()
        {
            // Skip detection while we're restoring layouts programmatically
            if (isRestoringLayout)
            {
                return;
            }

            if (active == null)
            {
                lastDetectedLayoutPath = string.Empty;
                return;
            }

            // Check if the current layout path has changed
            if (WorkspacesLayoutUtility.TryGetCurrentLayoutPath(out var currentPath))
            {
                if (currentPath != lastDetectedLayoutPath && !string.IsNullOrEmpty(currentPath))
                {
                    var cacheKey = GetCacheKey(active);
                    var savedLayoutPath = runtimeLayoutPaths.TryGetValue(cacheKey, out var saved) ? saved : 
                        (active.Id == "home" ? active.HomeLayoutPath : active.PrefabLayoutPath);
                    
                    // Layout changed - check if it's different from what's saved
                    if (savedLayoutPath != currentPath)
                    {
                        // Store in runtime dictionary instead of modifying workspace (prevents auto-save)
                        runtimeLayoutPaths[cacheKey] = currentPath;
                    }

                    lastDetectedLayoutPath = currentPath;
                }
            }
        }

        private static void RestoreFromSession()
        {
            // Domain reload happens on script recompile; SessionState survives that. After the first
            // restore in this editor session, only sync active workspace for toolbar UI — do not call
            // Activate() (which would reload the .wlt and steal focus).
            if (SessionState.GetBool(SessionRestoreCompletedKey, false))
            {
                var settings = WorkspacesSettings.instance;
                settings.EnsureDefaults();
                var savedId = SessionState.GetString(SessionKey, "home");
                var target = settings.FindById(savedId) ?? settings.GetHome();
                if (target != null)
                {
                    active = target;
                    ActiveWorkspaceChanged?.Invoke(active);
                }
                return;
            }

            SessionState.SetBool(SessionRestoreCompletedKey, true);

            // Skip restoration if in play mode (avoids interfering with Play Maximized etc.)
            if (ShouldSkipLayoutRestoration())
            {
                // Still restore the active workspace reference (without loading layout)
                var settings = WorkspacesSettings.instance;
                settings.EnsureDefaults();
                var savedId = SessionState.GetString(SessionKey, "home");
                var target = settings.FindById(savedId) ?? settings.GetHome();
                if (target != null)
                {
                    active = target;
                    ActiveWorkspaceChanged?.Invoke(active);
                }
                return;
            }
            
            var settingsInstance = WorkspacesSettings.instance;
            settingsInstance.EnsureDefaults();

            var id = SessionState.GetString(SessionKey, "home");
            var workspace = settingsInstance.FindById(id) ?? settingsInstance.GetHome();
            if (workspace != null)
            {
                Activate(workspace, false);
            }
        }

        internal static void Activate(WorkspaceDefinition target, bool recordSelection = true)
        {
            if (target == null)
            {
                return;
            }

            // Prevent Project Settings from opening during tab switch (set BEFORE EnsureDefaults)
            WorkspacesSettings.SetSwitchingTabs(true);
            
            // Start monitoring for Settings window opening
            if (!isMonitoringSettingsWindow)
            {
                EditorApplication.update += MonitorSettingsWindow;
                isMonitoringSettingsWindow = true;
            }
            
            WorkspacesSettings.instance.EnsureDefaults();
            
            try
            {
                var previous = active;
                var isHome = target.Id == "home";

                if (recordSelection)
                {
                    StoreSelection(previous);
                    StoreExpandedHierarchyItems(previous);
                }

                // Save current layout path before switching (not temp files, use actual layout path)
                if (previous != null)
                {
                    SaveCurrentLayoutPath(previous);
                }

                // Switch to the target workspace
                if (isHome)
                {
                    StageUtility.GoToMainStage();
                    EditorApplication.delayCall += () =>
                    {
                        // Skip layout loading during play mode to avoid interfering with Play Maximized etc.
                        if (ShouldSkipLayoutRestoration())
                        {
                            // Still restore selection/expansion without changing layout
                            EditorApplication.delayCall += () =>
                            {
                                RestoreExpandedHierarchyItems(target);
                                RestoreSelection(target);
                            };
                            return;
                        }
                        
                        // Use the auto-generated layout path for this workspace
                        var layoutPath = WorkspacesLayoutUtility.GetLayoutPathForWorkspace(target);
                        
                        if (WorkspacesLayoutUtility.LayoutExistsForWorkspace(target))
                        {
                            isRestoringLayout = true;
                            WorkspacesLayoutUtility.LoadLayout(layoutPath);
                            EditorApplication.delayCall += () => 
                            { 
                                isRestoringLayout = false;
                                // Restore selection and expansion after layout is loaded
                                EditorApplication.delayCall += () =>
                                {
                                    RestoreExpandedHierarchyItems(target);
                                    RestoreSelection(target);
                                };
                            };
                        }
                        else
                        {
                            // No layout file exists yet - just restore selection/expansion
                            EditorApplication.delayCall += () =>
                            {
                                RestoreExpandedHierarchyItems(target);
                                RestoreSelection(target);
                            };
                        }
                    };
                }
                else
                {
                    // Prefab workspace
                    var prefabPath = target.PrefabPath;
                    if (string.IsNullOrEmpty(prefabPath))
                    {
                        Debug.LogWarning("[Workspaces] Prefab workspace has no prefab assigned.");
                    }
                    else
                    {
                        PrefabStageUtility.OpenPrefab(prefabPath);
                        
                        // Close Settings window if it opens during prefab opening
                        EditorApplication.delayCall += () =>
                        {
                            try
                            {
                                var settingsWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SettingsWindow");
                                if (settingsWindowType != null)
                                {
                                    var settingsWindow = EditorWindow.GetWindow(settingsWindowType, false, null, false);
                                    if (settingsWindow != null && WorkspacesSettings.IsSwitchingTabs())
                                    {
                                        settingsWindow.Close();
                                    }
                                }
                            }
                            catch { }
                        };
                    }

                    EditorApplication.delayCall += () =>
                    {
                        // Skip layout loading during play mode to avoid interfering with Play Maximized etc.
                        if (ShouldSkipLayoutRestoration())
                        {
                            // Still restore selection/expansion without changing layout
                            EditorApplication.delayCall += () =>
                            {
                                EditorApplication.delayCall += () =>
                                {
                                    RestoreExpandedHierarchyItems(target);
                                    RestoreSelection(target);
                                };
                            };
                            return;
                        }
                        
                        // Use the auto-generated layout path for this workspace
                        var layoutPath = WorkspacesLayoutUtility.GetLayoutPathForWorkspace(target);
                        
                        if (WorkspacesLayoutUtility.LayoutExistsForWorkspace(target))
                        {
                            isRestoringLayout = true;
                            WorkspacesLayoutUtility.LoadLayout(layoutPath);
                            EditorApplication.delayCall += () => 
                            { 
                                isRestoringLayout = false;
                                // Restore selection and expansion after layout is loaded
                                // Use multiple delays for prefab stages to ensure they're fully ready
                                EditorApplication.delayCall += () =>
                                {
                                    EditorApplication.delayCall += () =>
                                    {
                                        RestoreExpandedHierarchyItems(target);
                                        RestoreSelection(target);
                                    };
                                };
                            };
                        }
                        else
                        {
                            // No layout file exists yet - just restore selection/expansion
                            EditorApplication.delayCall += () =>
                            {
                                EditorApplication.delayCall += () =>
                                {
                                    RestoreExpandedHierarchyItems(target);
                                    RestoreSelection(target);
                                };
                            };
                        }
                    };
                }

                active = target;
                SessionState.SetString(SessionKey, active.Id);
                ActiveWorkspaceChanged?.Invoke(active);
            }
            finally
            {
                // Re-enable saves after tab switch completes
                // Use multiple delays to ensure prefab stage is fully initialized before clearing flag
                EditorApplication.delayCall += () =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        EditorApplication.delayCall += () =>
                        {
                            EditorApplication.delayCall += () =>
                            {
                                WorkspacesSettings.SetSwitchingTabs(false);
                                // Stop monitoring after a delay to ensure window doesn't reopen
                                EditorApplication.delayCall += () =>
                                {
                                    if (isMonitoringSettingsWindow)
                                    {
                                        EditorApplication.update -= MonitorSettingsWindow;
                                        isMonitoringSettingsWindow = false;
                                    }
                                };
                            };
                        };
                    };
                };
            }
        }

        private static void SaveCurrentLayoutPath(WorkspaceDefinition workspaceToSave)
        {
            if (workspaceToSave == null) return;
            
            // Save the current layout to the auto-generated layout file for this workspace
            var layoutPath = WorkspacesLayoutUtility.GetLayoutPathForWorkspace(workspaceToSave);
            WorkspacesLayoutUtility.SaveLayout(layoutPath);
            
            // Also cache the path for fallback
            var cacheKey = GetCacheKey(workspaceToSave);
            cachedLayoutPaths[cacheKey] = layoutPath;
        }

        internal static string GetCacheKey(WorkspaceDefinition workspace)
        {
            if (workspace == null)
            {
                return "home";
            }

            // Home workspace is identified by Id == "home"
            if (workspace.Id == "home")
            {
                return "home";
            }

            return string.IsNullOrEmpty(workspace.Id) ? "ws-unknown" : workspace.Id;
        }

        private static void StoreSelection(WorkspaceDefinition workspace)
        {
            if (workspace == null || !workspace.KeepSelection)
            {
                return;
            }

            var cacheKey = GetCacheKey(workspace);
            cachedSelections[cacheKey] = Selection.instanceIDs;
            
            // For prefab stages, also store GameObject paths (since instance IDs change when prefab reopens)
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.prefabContentsRoot != null)
            {
                var prefabGameObjectPaths = new List<string>();
                foreach (var id in Selection.instanceIDs)
                {
                    var obj = EditorUtility.InstanceIDToObject(id);
                    if (obj is GameObject go && !AssetDatabase.Contains(go))
                    {
                        // Check if GameObject is in the prefab stage
                        if (prefabStage.prefabContentsRoot == go || go.transform.IsChildOf(prefabStage.prefabContentsRoot.transform))
                        {
                            // Store transform path relative to prefab root
                            var path = GetTransformPath(go.transform, prefabStage.prefabContentsRoot.transform);
                            if (!string.IsNullOrEmpty(path))
                            {
                                prefabGameObjectPaths.Add(path);
                            }
                        }
                    }
                }
                cachedPrefabGameObjectPaths[cacheKey] = prefabGameObjectPaths;
            }
            else
            {
                // Clear prefab paths for non-prefab workspaces
                cachedPrefabGameObjectPaths.Remove(cacheKey);
            }
        }
        
        private static string GetTransformPath(Transform transform, Transform root)
        {
            if (transform == root)
            {
                return ""; // Root is empty path
            }
            
            var path = new List<string>();
            var current = transform;
            
            while (current != null && current != root)
            {
                path.Insert(0, current.name);
                current = current.parent;
            }
            
            return string.Join("/", path);
        }
        
        private static GameObject FindGameObjectByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return root.gameObject;
            }
            
            var parts = path.Split('/');
            var current = root;
            
            foreach (var part in parts)
            {
                if (current == null)
                    return null;
                    
                var found = false;
                foreach (Transform child in current)
                {
                    if (child.name == part)
                    {
                        current = child;
                        found = true;
                        break;
                    }
                }
                
                if (!found)
                    return null;
            }
            
            return current != null ? current.gameObject : null;
        }

        private static void RestoreSelection(WorkspaceDefinition workspace)
        {
            if (workspace == null || !workspace.KeepSelection)
            {
                return;
            }

            var cacheKey = GetCacheKey(workspace);
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            
            // For prefab stages, try to restore GameObjects by path first (since instance IDs change)
            var prefabGameObjectIds = new List<int>();
            if (prefabStage != null && prefabStage.prefabContentsRoot != null)
            {
                if (cachedPrefabGameObjectPaths.TryGetValue(cacheKey, out var paths) && paths != null && paths.Count > 0)
                {
                    foreach (var path in paths)
                    {
                        var go = FindGameObjectByPath(prefabStage.prefabContentsRoot.transform, path);
                        if (go != null)
                        {
                            prefabGameObjectIds.Add(go.GetInstanceID());
                        }
                    }
                }
            }
            
            // Also restore assets and other objects by instance ID
            var validIds = new List<int>();
            if (cachedSelections.TryGetValue(cacheKey, out var ids) && ids != null && ids.Length > 0)
            {
                // Filter out invalid instance IDs (objects that no longer exist)
                foreach (var id in ids)
                {
                    var obj = EditorUtility.InstanceIDToObject(id);
                    if (obj == null)
                        continue;
                    
                    // Assets are always valid (they're in the project, not in any stage)
                    if (AssetDatabase.Contains(obj))
                    {
                        validIds.Add(id);
                        continue;
                    }
                    
                    // For prefab stages, skip GameObjects (we restore them by path instead)
                    if (prefabStage != null && obj is GameObject)
                    {
                        continue; // Already handled by path-based restoration above
                    }
                    
                    // For main stage, all scene objects are valid
                    if (prefabStage == null)
                    {
                        validIds.Add(id);
                    }
                }
            }
            
            // Combine prefab GameObjects (by path) with assets/other objects (by instance ID)
            validIds.AddRange(prefabGameObjectIds);
            
            if (validIds.Count > 0)
            {
                // Use multiple delay calls to ensure stage is fully ready
                EditorApplication.delayCall += () =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        try
                        {
                            // Check if selection contains assets (Project browser) or scene objects (Hierarchy)
                            var assetIds = new List<int>();
                            var sceneIds = new List<int>();
                            
                            foreach (var id in validIds)
                            {
                                var obj = EditorUtility.InstanceIDToObject(id);
                                if (obj != null)
                                {
                                    if (AssetDatabase.Contains(obj))
                                    {
                                        assetIds.Add(id);
                                    }
                                    else
                                    {
                                        sceneIds.Add(id);
                                    }
                                }
                            }
                            
                            // Restore selection - restore both scene objects and assets
                            var allIds = new List<int>();
                            if (sceneIds.Count > 0)
                            {
                                allIds.AddRange(sceneIds);
                                Selection.instanceIDs = sceneIds.ToArray();
                            }
                            
                            // Always restore assets if they exist (even if there are scene objects)
                            if (assetIds.Count > 0)
                            {
                                allIds.AddRange(assetIds);
                                
                                // Focus Project browser
                                EditorUtility.FocusProjectWindow();
                                
                                // Set selection to include assets (merge with scene objects if any)
                                if (allIds.Count > 0)
                                {
                                    Selection.instanceIDs = allIds.ToArray();
                                }
                                else
                                {
                                    Selection.instanceIDs = assetIds.ToArray();
                                }
                                
                                // Navigate Project browser to the selected asset
                                var projectWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProjectBrowser");
                                if (projectWindowType != null)
                                {
                                    var projectWindow = EditorWindow.GetWindow(projectWindowType, false, null, false);
                                    if (projectWindow != null)
                                    {
                                        projectWindow.Focus();
                                        
                                        // Ping and frame the first selected asset to navigate Project browser to it
                                        var firstAssetId = assetIds[0];
                                        var firstAsset = EditorUtility.InstanceIDToObject(firstAssetId);
                                        if (firstAsset != null)
                                        {
                                            // Use multiple delays to ensure Project browser is ready
                                            EditorApplication.delayCall += () =>
                                            {
                                                EditorApplication.delayCall += () =>
                                                {
                                                    try
                                                    {
                                                        // Ping object first to expand folders and navigate to it
                                                        EditorGUIUtility.PingObject(firstAssetId);
                                                        
                                                        // Use reflection to call FrameObject for more reliable navigation
                                                        var frameObjectMethod = projectWindowType.GetMethod("FrameObject", 
                                                            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                                                        if (frameObjectMethod != null)
                                                        {
                                                            frameObjectMethod.Invoke(projectWindow, new object[] { firstAssetId });
                                                        }
                                                        
                                                        // Ensure selection is set again after navigation
                                                        Selection.activeInstanceID = firstAssetId;
                                                        if (allIds.Count > 1)
                                                        {
                                                            Selection.instanceIDs = allIds.ToArray();
                                                        }
                                                        else if (assetIds.Count > 1)
                                                        {
                                                            Selection.instanceIDs = assetIds.ToArray();
                                                        }
                                                    
                                                        projectWindow.Repaint();
                                                    }
                                                    catch { }
                                                };
                                            };
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        };
                    };
                }
            }

        private static void StoreExpandedHierarchyItems(WorkspaceDefinition workspace)
        {
            if (workspace == null || !workspace.KeepSelection)
            {
                return;
            }

            try
            {
                var cacheKey = GetCacheKey(workspace);
                var expandedItems = new List<int>();

                // Get all expanded items from the hierarchy window
                var hierarchyWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
                if (hierarchyWindowType != null)
                {
                    var windows = Resources.FindObjectsOfTypeAll(hierarchyWindowType);
                    foreach (var window in windows)
                    {
                        if (window is EditorWindow hierarchyWindow)
                        {
                            // Try to get expanded state through reflection
                            var treeViewField = hierarchyWindowType.GetField("m_TreeView", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (treeViewField != null)
                            {
                                var treeView = treeViewField.GetValue(hierarchyWindow);
                                if (treeView != null)
                                {
                                    var getExpandedIdsMethod = treeView.GetType().GetMethod("GetExpanded", BindingFlags.Public | BindingFlags.Instance);
                                    if (getExpandedIdsMethod != null)
                                    {
                                        var expanded = getExpandedIdsMethod.Invoke(treeView, null) as IList;
                                        if (expanded != null)
                                        {
                                            foreach (var item in expanded)
                                            {
                                                if (item is int id)
                                                {
                                                    expandedItems.Add(id);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                cachedExpandedHierarchyItems[cacheKey] = expandedItems;
            }
            catch
            {
                // Silently fail if we can't access hierarchy expansion state
            }
        }

        private static void RestoreExpandedHierarchyItems(WorkspaceDefinition workspace)
        {
            if (workspace == null || !workspace.KeepSelection)
            {
                return;
            }

            try
            {
                var cacheKey = GetCacheKey(workspace);
                if (!cachedExpandedHierarchyItems.TryGetValue(cacheKey, out var expandedItems) || expandedItems == null || expandedItems.Count == 0)
                {
                    return;
                }

                // Restore expanded state in hierarchy window with multiple delays to ensure stage is ready
                var hierarchyWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
                if (hierarchyWindowType != null)
                {
                    EditorApplication.delayCall += () =>
                    {
                        EditorApplication.delayCall += () =>
                        {
                            var windows = Resources.FindObjectsOfTypeAll(hierarchyWindowType);
                            foreach (var window in windows)
                            {
                                if (window is EditorWindow hierarchyWindow)
                                {
                                    var treeViewField = hierarchyWindowType.GetField("m_TreeView", BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (treeViewField != null)
                                    {
                                        var treeView = treeViewField.GetValue(hierarchyWindow);
                                        if (treeView != null)
                                        {
                                            var setExpandedMethod = treeView.GetType().GetMethod("SetExpanded", BindingFlags.Public | BindingFlags.Instance);
                                            if (setExpandedMethod != null)
                                            {
                                                // Filter to valid IDs and check if they're in the current stage
                                                var validIds = expandedItems.Where(id =>
                                                {
                                                    var obj = EditorUtility.InstanceIDToObject(id);
                                                    if (obj == null)
                                                        return false;
                                                    
                                                    // For prefab stages, only restore if object is in that prefab
                                                    if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                                                    {
                                                        var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                                                        if (obj is GameObject go)
                                                        {
                                                            return prefabStage.prefabContentsRoot == go || go.transform.IsChildOf(prefabStage.prefabContentsRoot.transform);
                                                        }
                                                        return false;
                                                    }
                                                    
                                                    return true;
                                                }).ToList();

                                                foreach (var id in validIds)
                                                {
                                                    try
                                                    {
                                                        setExpandedMethod.Invoke(treeView, new object[] { id, true });
                                                    }
                                                    catch { }
                                                }
                                                
                                                // Force refresh
                                                hierarchyWindow.Repaint();
                                            }
                                        }
                                    }
                                }
                            }
                        };
                    };
                }
            }
            catch
            {
                // Silently fail if we can't restore hierarchy expansion state
            }
        }
    }

    internal static partial class WorkspacesToolbar
    {
        /// <summary>Registered <see cref="MainToolbarElementAttribute.path"/> for Home (used with <see cref="MainToolbar.Refresh"/>).</summary>
        internal const string ToolbarPathHome = "Workspaces/Home";
        /// <summary>Registered path for Settings.</summary>
        internal const string ToolbarPathSettings = "Workspaces/Settings";

        static WorkspacesToolbar()
        {
            WorkspacesState.ActiveWorkspaceChanged -= OnActiveWorkspaceChangedToolbar;
            WorkspacesState.ActiveWorkspaceChanged += OnActiveWorkspaceChangedToolbar;
        }

        static void OnActiveWorkspaceChangedToolbar(WorkspaceDefinition _)
        {
            WorkspacesSettings.instance.EnsureDefaults();
            foreach (var kv in registeredButtons.ToList())
            {
                if (kv.Value == null) continue;
                var def = WorkspacesSettings.instance.FindById(kv.Key);
                if (def != null)
                    UpdateButtonContent(kv.Value, def);
            }
        }

        [MainToolbarElement(ToolbarPathHome, defaultDockPosition = MainToolbarDockPosition.Middle, defaultDockIndex = 0, menuPriority = 0)]
        public static MainToolbarElement HomeButton()
        {
            WorkspacesSettings.instance.EnsureDefaults();
            var homeDef = WorkspacesSettings.instance.GetHome();

            var content = new MainToolbarContent((Texture2D)null)
            {
                text = homeDef.DisplayName,
                tooltip = $"Go to main stage\nLayout: {(string.IsNullOrEmpty(homeDef.HomeLayoutPath) ? "keep current" : Path.GetFileNameWithoutExtension(homeDef.HomeLayoutPath))}"
            };

            var button = new MainToolbarButton(content, () => WorkspacesState.Activate(homeDef));
            ApplyButtonStyling(button, homeDef);
            return button;
        }

        /// <summary>
        /// Called from generated <c>WorkspacesToolbar.Generated.cs</c> — one registration per prefab workspace.
        /// </summary>
        internal static MainToolbarElement CreatePrefabWorkspaceToolbarElement(string workspaceId)
        {
            WorkspacesSettings.instance.EnsureDefaults();
            var def = WorkspacesSettings.instance.FindById(workspaceId);
            if (def == null)
            {
                var placeholder = new MainToolbarButton(new MainToolbarContent((Texture2D)null), () => { })
                {
                    displayed = false
                };
                return placeholder;
            }

            var content = new MainToolbarContent((Texture2D)null)
            {
                text = def.DisplayName,
                tooltip = BuildTooltip(def)
            };

            var capturedId = workspaceId;
            var button = new MainToolbarButton(content, () =>
            {
                WorkspacesSettings.instance.EnsureDefaults();
                var d = WorkspacesSettings.instance.FindById(capturedId);
                if (d != null)
                    WorkspacesState.Activate(d);
            });

            ApplyButtonStyling(button, def);
            return button;
        }

        [MainToolbarElement(ToolbarPathSettings, defaultDockPosition = MainToolbarDockPosition.Middle, defaultDockIndex = 999, menuPriority = 10000)]
        public static MainToolbarElement SettingsButton()
        {
            var content = new MainToolbarContent((Texture2D)null)
            {
                text = "Settings",
                tooltip = "Open Project Settings > Workspaces"
            };

            return new MainToolbarButton(content, () => SettingsService.OpenProjectSettings("Project/Workspaces"));
        }

        /// <summary>
        /// Forces Unity's toolbar to refresh, showing/hiding buttons based on current settings.
        /// Call this after saving settings to update the toolbar.
        /// </summary>
        internal static void RefreshToolbar()
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    MainToolbar.Refresh(ToolbarPathHome);
                    WorkspacesSettings.instance.EnsureDefaults();
                    foreach (var w in WorkspacesSettings.instance.Workspaces)
                    {
                        var p = WorkspacesToolbarPaths.GetPrefabWorkspaceToolbarPath(w);
                        if (!string.IsNullOrEmpty(p))
                            MainToolbar.Refresh(p);
                    }
                    MainToolbar.Refresh(ToolbarPathSettings);

                    // Force Unity to rebuild the toolbar by accessing the MainToolbar instance
                    var toolbarType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Toolbars.MainToolbar");
                    if (toolbarType != null)
                    {
                        var instanceProp = toolbarType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        var toolbar = instanceProp?.GetValue(null);
                        if (toolbar != null)
                        {
                            // Try to call Refresh or Rebuild methods
                            var refreshMethod = toolbarType.GetMethod("Refresh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            refreshMethod?.Invoke(toolbar, null);
                            
                            var rebuildMethod = toolbarType.GetMethod("Rebuild", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            rebuildMethod?.Invoke(toolbar, null);
                            
                            // Force repaint
                            var repaintMethod = toolbarType.GetMethod("Repaint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            repaintMethod?.Invoke(toolbar, null);
                        }
                    }
                    
                    // Also try to trigger a toolbar update via EditorApplication
                    EditorApplication.RepaintHierarchyWindow();
                    EditorApplication.RepaintProjectWindow();
                }
                catch
                {
                    // Silently fail if reflection doesn't work
                }
            };
        }

        private static readonly Dictionary<string, MainToolbarButton> registeredButtons = new Dictionary<string, MainToolbarButton>();

        internal static void RefreshAllWorkspaceToolbarButtons()
        {
            WorkspacesSettings.instance.EnsureDefaults();
            foreach (var kv in registeredButtons.ToList())
            {
                if (kv.Value == null) continue;
                var def = WorkspacesSettings.instance.FindById(kv.Key);
                if (def == null) continue;
                if (def.Id != "home")
                {
                    var c = kv.Value.content;
                    c.tooltip = BuildTooltip(def);
                    kv.Value.content = c;
                }
                UpdateButtonContent(kv.Value, def);
            }
        }

        private static void ApplyButtonStyling(MainToolbarButton button, WorkspaceDefinition def)
        {
            registeredButtons[def.Id] = button;
            if (def.Id != "home")
            {
                var c = button.content;
                c.tooltip = BuildTooltip(def);
                button.content = c;
            }
            UpdateButtonContent(button, def);
        }

        private static void UpdateButtonContent(MainToolbarButton button, WorkspaceDefinition def)
        {
            var active = WorkspacesState.Active;
            var isActive = active != null && WorkspacesState.GetCacheKey(def) == WorkspacesState.GetCacheKey(active);

            var content = button.content;
            var baseText = def?.DisplayName ?? string.Empty;
            content.text = isActive ? $"[{baseText}]" : baseText;
            
            button.content = content;
        }

        private static string BuildTooltip(WorkspaceDefinition workspace)
        {
            if (workspace == null)
                return "Open prefab in isolation mode";

            var prefabInfo = string.IsNullOrEmpty(workspace.PrefabPath)
                ? "No prefab assigned"
                : $"Prefab: {workspace.PrefabPath}";

            var layoutInfo = string.IsNullOrEmpty(workspace.PrefabLayoutPath)
                ? "Layout: keep current"
                : $"Layout: {Path.GetFileNameWithoutExtension(workspace.PrefabLayoutPath)}";

            return $"{prefabInfo}\n{layoutInfo}";
        }
    }
}
