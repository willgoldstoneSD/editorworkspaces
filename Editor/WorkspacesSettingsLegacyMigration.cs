using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace WillGoldstone.Workspaces
{
    /// <summary>
    /// Unity does not reliably expose <c>ProjectSettings/*.asset</c> through <see cref="AssetDatabase.LoadAllAssetsAtPath"/>,
    /// so we copy the legacy file on disk before <see cref="WorkspacesSettings"/> is first deserialized.
    /// </summary>
    internal static class WorkspacesSettingsLegacyMigration
    {
        private const string LegacyRelative = "ProjectSettings/ProjectWeasel.Workspaces.asset";
        private const string CurrentRelative = "ProjectSettings/WillGoldstone.Workspaces.asset";

        private static string PrefKeyV2 =>
            "WillGoldstone.Workspaces.LegacyAssetFileMigrated_v2." + Directory.GetParent(Application.dataPath)!.FullName;

        [InitializeOnLoadMethod(order = -32000)]
        private static void RunBeforeAnySingletonAccess()
        {
            try
            {
                RunFileMigrationIfNeeded();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Workspaces] Legacy settings migration failed: {e.Message}\n{e.StackTrace}");
            }
        }

        private static void RunFileMigrationIfNeeded()
        {
            if (EditorPrefs.GetBool(PrefKeyV2, false))
                return;

            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var legacyFull = Path.Combine(projectRoot, LegacyRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(legacyFull))
            {
                EditorPrefs.SetBool(PrefKeyV2, true);
                return;
            }

            var currentFull = Path.Combine(projectRoot, CurrentRelative.Replace('/', Path.DirectorySeparatorChar));
            var legacyLen = new FileInfo(legacyFull).Length;

            if (File.Exists(currentFull) && new FileInfo(currentFull).Length >= legacyLen)
            {
                EditorPrefs.SetBool(PrefKeyV2, true);
                return;
            }

            var dir = Path.GetDirectoryName(currentFull);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.Copy(legacyFull, currentFull, true);
            TryClearScriptableSingletonCache();

            if (!AssetDatabase.DeleteAsset(LegacyRelative))
            {
                File.Delete(legacyFull);
                var meta = legacyFull + ".meta";
                if (File.Exists(meta))
                    File.Delete(meta);
            }

            AssetDatabase.Refresh();
            EditorPrefs.SetBool(PrefKeyV2, true);
            Debug.Log("[Workspaces] Migrated ProjectWeasel.Workspaces.asset → WillGoldstone.Workspaces.asset (on-disk copy). If the toolbar still looks empty, restart the Editor once.");
        }

        private static void TryClearScriptableSingletonCache()
        {
            for (var t = typeof(WorkspacesSettings); t != null && t != typeof(UnityEngine.Object) && t != typeof(object); t = t.BaseType)
            {
                foreach (var fi in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (!typeof(WorkspacesSettings).IsAssignableFrom(fi.FieldType))
                        continue;
                    fi.SetValue(null, null);
                    return;
                }
            }

            Debug.LogWarning("[Workspaces] Could not clear ScriptableSingleton cache via reflection; restart the Editor if workspaces still look empty.");
        }
    }
}
