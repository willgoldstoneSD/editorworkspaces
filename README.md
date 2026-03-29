# Workspaces

Main Toolbar buttons that switch between a **Home** workspace, **prefab isolation** workspaces (each with its own saved layout), and optional extra tabs. Selection and hierarchy expansion can be preserved when switching.

## Installation

**Embedded (this repo layout)**  
Add the folder `Packages/com.willgoldstone.workspaces` to your project (or clone this repository with the package at the repo root — see below).

**Git URL (UPM)**  
In your project’s `Packages/manifest.json`, add:

```json
"com.willgoldstone.workspaces": "https://github.com/YOUR_ORG/unity-workspaces.git?path=/Packages/com.willgoldstone.workspaces#v0.1.1"
```

Adjust the URL and Git tag to match your fork. Unity imports the assembly `WillGoldstone.Workspaces.Editor`.

### Standalone GitHub repository

To share only this package (no game project):

1. Create a new repo and copy **this entire folder** (`com.willgoldstone.workspaces`) into it.
2. Choose a layout:
   - **Package at repo root:** move `package.json`, `Editor/`, and `README.md` to the root of the repo. UPM entry: `"com.willgoldstone.workspaces": "https://github.com/USER/REPO.git#v0.1.1"`.
   - **Under `Packages/`:** keep `Packages/com.willgoldstone.workspaces/...`. UPM entry: `"com.willgoldstone.workspaces": "https://github.com/USER/REPO.git?path=/Packages/com.willgoldstone.workspaces#v0.1.1"`.
3. Tag releases (e.g. `v0.1.1`) so consumers can pin versions.

## Using Workspaces

- In the **Main Toolbar**, Workspaces appear in the middle group (Unity **6.3+** — `MainToolbarElement` API).
- **Home** is always present and returns to the main stage.
- **Prefab** workspaces open a configured prefab in isolation (`PrefabStage`).
- The **active** workspace is shown with brackets in the toolbar, e.g. `[Bomb]`.
- Prefab workspaces are listed in **Project Settings** order. Saving settings **regenerates** `Packages/com.willgoldstone.workspaces/Editor/WorkspacesToolbar.Generated.cs` with **one** `[MainToolbarElement]` per prefab row so the Main Toolbar and **Add (+)** menu only show workspaces that exist. Toolbar button text and the Add menu label use the **Display Name** (sanitized for the path; a short id suffix is added only if two workspaces would collide).
- Click **Save** after adding, removing, or renaming workspaces so the generator runs and Unity can recompile.

## Project Settings

Settings are stored in `ProjectSettings/WillGoldstone.Workspaces.asset`. If you still have `ProjectSettings/ProjectWeasel.Workspaces.asset` from an older package id, it is migrated automatically once (data is copied, then the legacy file is removed).

Open **Project Settings → Workspaces**:

- Configure **Home** and add/rename **prefab** workspaces; assign a prefab per prefab tab.
- **Keep Selection** — remember hierarchy/project selection when leaving a workspace (per workspace).
- **Game View Workspace** — at most one workspace can be marked. On **Enter Play Mode**, the tool caches the current editing workspace, switches to this layout (e.g. Game View maximized), then **restores the cached workspace** when you exit Play mode.
- **Layout files** — layouts are stored automatically under `Assets/Workspaces/Layouts/` as `{DisplayName}_{IdPrefix}.wlt`. Use **Update Workspace Layout** to save the **current** editor layout into that workspace’s file.

### Script recompiles (domain reload)

On **first** load of the editor in a session, the package restores the last active workspace from session data (and may load its `.wlt`).

After that, **each script recompile** triggers a domain reload but **does not** reload the workspace layout file again — only the active workspace id is synced for the toolbar. This avoids resetting windows and **stealing focus** on every compile.

Restarting the Unity Editor starts a new session and performs a full restore again.

## Technical notes

- Layout save uses reflection on Unity’s internal `WindowLayout.SaveWindowLayout`; load uses `EditorUtility.LoadWindowLayout`.
- Play mode transitions use `SessionState` so state survives domain reloads when entering/exiting Play mode.
- Prefab isolation: `PrefabStageUtility.OpenPrefab` / `StageUtility.GoToMainStage`.
- The active workspace id is stored in `SessionState` for the current editor session.
