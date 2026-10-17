# Changelog

## 1.5.2

- The log colours errors, warnings and successes.
- The window size is remembered.
- Drop a .vmdl file or an addon folder on the window to select it.
- The preview explains what to do when no model is selected or its render mesh is missing.
- Fixed: a second compile could start during the pre-compile camera sync, and a failing sync could crash the app.
- Fixed: sync could corrupt compact one-line attachment nodes and missed vectors with a trailing comma or comment.
- Fixed: batch sync stopped at the first failing model and could not resolve variant models such as fencer.
- Fixed: selecting a listed model after opening a file manually left the model list empty.

## 1.5.1

- Selecting a model made before the October 2026 update shows a notice with a shortcut to **sync from game**.
- **sync from game** can run on every model in all addons at once.
- **compile** warns when the model still uses the old camera frame and offers to sync it first, compile anyway, or cancel.
- Removed hitbox replacement from **sync from game**.
- Updated Avalonia to 11.3.22, which removes the vulnerable `Tmds.DBus.Protocol` 0.20.0 dependency.

## 1.5.0

- Restyled the main window and dialogs: consistent section headers with icons, even control heights, manual tools as tiles, and a sync dialog in the style of add addon.
- Added **sync from game...**, which updates older `.vmdl` files from the installed game. Deadlock's October 2026 update moved every `root_motion` attachment (camera pivots, aim, zipline, portrait camera and others) into a new frame, so older skins have misplaced cameras. The sync converts these attachments in the source `.vmdl` (offset `[x,y,z]` becomes `[z,x,y]`, and the rotation is converted to match), so authored adjustments are kept, and unmodified attachments get the current game values. It can also add missing attachments, and update the game's settings structs (camera, unit status, hero model settings). A backup is written first.
- Updated the bundled ValveResourceFormat to upstream `master` (`aab066c`), which reads the models of the October 2026 update and includes the upstream cloth exporter and VCS 72 support.
- Added an opt-in **skip material/texture compilation** setting for newer CSWin64 builds. It passes `-skiptype vmat,vtex`, temporarily disables missing-material fatal errors in CSWin64 `gameinfo.gi`, restores that file after compilation, and preserves the original CSDK material references.
- Added a startup update check that asks before downloading newer Windows releases from the project's GitHub releases, then offers to restart and apply the update beside the current executable.

## 1.3.7

- Added support for neutral creeps.

## 1.3.6

- Locate Deadlock using Steam's `libraryfolders.vdf` and `appmanifest_1422450.acf`, taking the game folder from `installdir`. Removed fixed drive paths and game folder name guesses.
- Support modern and legacy library lists, both Windows registry views, custom library locations, and damaged or stale metadata in other libraries.
- Use the same Steam discovery for cloth resources. Keep explicit path hints and manual VPK selection available.

## 1.3.5

- Combined Qusai0's automatic animation detection with the manual AnimationList override. Fixed disabled parent lists, unintended clip unmuting, and incorrect matches based only on file names.
- Synchronize referenced animation sources outside the model directory while preserving their addon paths.
- Create CSDK12 addons by exporting the main hero model and its animation, material, texture, and available cloth dependencies using the original game paths.
- Expanded the export catalog to 44 heroes. Added Baba, Deadman Danny, Nurse Harrow, Rat King, Solomon, and Violet with model-derived AG2 presets and automatic hero detection.
- Added searchable hero selection and game portraits; matched known AG2 presets to hero names and portraits. Unknown or unavailable portraits use the existing fallback.
- Updated the desktop interface, app icon, and model preview camera and material loading.
- Added VCS 72 shader support while preserving the existing cloth exporter.
- Verify compiled AG2 references before deployment and packaging. Protect compiled models from CSDK12 overwrites until verified packaging or the user's refusal.
- Publish one self-contained Windows x64 executable, including managed and native dependencies.

The current prerelease game assets for the six newest heroes do not yet include
their small portraits or compiled default AG2 graph files. Their presets retain
the exact graph references stored in the game models. Cloth nodes can still
require manual corrections.
