# Deadlock VMDL Compiler

A specialized GUI compiler and asset pipeline tool for Valve's Deadlock (Source 2). Bypasses CSDK12 limitations by automating CSWin64 ModelDoc compilation, AnimGraph 2 (AG2) skeleton and graph reference injection, and dynamic cloth physics generation.

---

## Why This Tool Exists

Deadlock uses AnimGraph 2 (AG2) animation structures. Standard CSDK12 tooling cannot compile AG2 nodes directly into .vmdl files. This tool solves the problem by:
1. Temporarily updating the .vmdl with compiled vanilla skeleton (.vnmskel) and AnimGraph (.vnmgraph) nodes.
2. Invoking the CSWin64 ModelDoc compiler to produce a fully valid compiled model (.vmdl_c).
3. Deploying the compiled asset directly to the target addon directory and restoring the working source file.

---

## Features

- **CSWin64 ModelDoc Compilation**: Automates compilation using the CSWin64 ModelDoc compiler and exports compiled .vmdl_c files directly into your addon game directory.
- **AnimGraph 2 (AG2) Pipeline**: Injects vanilla skeleton (.vnmskel) and AnimGraph references (.vnmgraph) prior to compilation to bypass CSDK12 AG2 compiler limitations.
- **Dynamic Cloth & Softbody Simulation**: Automatically detects bone chains (hair, braids, tails, ears, fur, cuffs, shackles, bolas, sleeves) and creates valid _class = \"Softbody\" nodes with physics curves and collision spheres. Automatically links custom .dmx cloth proxy meshes if present.
- **Safety & Backups**: Creates automatic .bak backups before modifying files with a 1-click revert option.
- **Addon Explorer**: Real-time discovery of addons and target .vmdl models in the configured content directory.

---

## Installation & Usage

### Running Prebuilt Binary
1. Download the latest release from the [Releases](https://github.com/kwlnd/deadlock-vmdl-compiler/releases) page.
2. Extract the archive and launch DeadlockVmdlCompiler.exe.
3. Set your **CSWin64 bin directory** (e.g. .../game/csgo/bin/win64 or .../game/bin/win64).
4. Set your **Citadel Addons directory** (e.g. .../content/citadel_addons).
5. Select the target addon and model, and click **Compile Model** or **Transfer Cloth**.

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
