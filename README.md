# Deadlock VMDL Compiler

A GUI compiler and asset pipeline tool for Valve's Deadlock (Source 2). Automates hero model compilation, skeleton rebinding, AnimGraph resolution, material remaps, and dynamic cloth physics generation.

---

## Features

- **Source 2 ModelDoc Compilation**: Automates .vmdl compilation using Counter-Strike 2 / Deadlock SDK (csdk12 / cs_win64) ModelDoc binaries with direct output to addon game folders.
- **Skeleton & AnimGraph Setup**: Detects hero archetypes and injects required skeleton and AnimGraph references (.vnmskel, .vnmgraph). Resolves ModelDoc syntax differences across format versions.
- **Dynamic Cloth & Softbody Simulation**: Analyzes skeleton bone chains (hair, braids, tails, props, cuffs, chains, sleeves) and generates valid _class = \"Softbody\" nodes with physics curves and body collision spheres. Automatically links custom .dmx cloth proxy meshes if present.
- **Material Remapping**: Automatic material search and remap rules for imported character models.
- **Safety & Backups**: Creates automatic .bak backups before modifying files with a 1-click revert option.
- **Addon Explorer**: Real-time discovery of addons and target .vmdl models in the configured content directory.

---

## Installation & Usage

### Running Prebuilt Binary
1. Download the latest release from the [Releases](https://github.com/kwlnd/deadlock-vmdl-compiler/releases) page.
2. Extract the archive and launch DeadlockVmdlCompiler.exe.
3. Set your **CSWin64 / CSDK12 bin directory** (e.g. .../game/csgo/bin/win64).
4. Set your **Citadel Addons directory** (e.g. .../content/citadel_addons).
5. Select the target addon and model, configure options, and click **Compile Model** or **Transfer Cloth**.

---

## Building from Source

### Requirements
- .NET 10.0 SDK (or .NET 8.0+)
- Windows 10/11 x64

### Build
`ash
git clone https://github.com/kwlnd/deadlock-vmdl-compiler.git
cd deadlock-vmdl-compiler

dotnet build -c Release
dotnet publish DeadlockVmdlCompiler.csproj -c Release -o ./publish
`

---

## License
MIT License
