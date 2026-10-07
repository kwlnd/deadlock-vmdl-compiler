# Changelog

## Unreleased

- Compilation, fix(modeldoc) and CSWin64 export run off the UI thread. ModelDoc parsing no longer rescans the file for every node: a 743 KB hero model with 300 clips prepares in about 0.4 s instead of 4 s, with byte-identical output.
- The compile button cancels a running compile and stops `resourcecompiler.exe`; nothing is deployed.
- A mesh or material that cannot be copied to CSWin64 now fails the compile instead of silently compiling the previous copy. Replaced files are detected by size and timestamp, including older ones.
- Neutral creep models are listed in the addon model menu and can be exported with add addon.
- fix(modeldoc) and addon export share one comment- and string-aware cleaner; standalone `NmSkeletonReference` nodes are removed by both.
- Backups are created only when the source `.vmdl` is rewritten, and identical backups are not duplicated.
- Added **load list...** for custom AG2 preset files.
- Deployment and packaging resolve the compiled addon directory through the same code, also when an unrelated `content` folder appears earlier in the path. Selecting CSWin64's `game` directory now uses the matching `content` directory.
- VPK archives are written to a temporary file and swapped in on success; files that change during packaging are rejected; non-ASCII paths verify correctly.
- Injected nodes keep CRLF line endings and are no longer separated by a comma inside a trailing comment.
- A missing material no longer aborts add addon. A configured folder on a disconnected drive is kept in settings.
- Removed unused code: the placeholder mesh statistics, the old launcher, the unused cloth chain generator and VPK preset scanner, and unused settings.

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
