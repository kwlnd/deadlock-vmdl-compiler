# ⚡ Deadlock VMDL Compiler & Node Upgrader

A modern, high-performance GUI compiler and asset pipeline tool for **Valve's Deadlock** (Source 2 engine). Designed to streamline custom hero skin compilation, skeleton rebinding, AnimGraph resolution, material remaps, and automated dynamic cloth physics transfer.

![Deadlock VMDL Compiler](AppIcon.png)

---

## 🌟 Key Features

* **🚀 1-Click Source 2 ModelDoc Compilation**:
  * Automatically compiles .vmdl files using Counter-Strike 2 / Deadlock SDK (csdk12 / cs_win64) ModelDoc compiler binaries.
  * Direct output path resolution into your addon directory.

* **🦴 Skeleton & AnimGraph Upgrader**:
  * Automatically detects hero archetype and injects official skeleton and AnimGraph references (.vanmgrph / .vanmgrph_c).
  * Fixes missing bones, binding poses, and model doc syntax across ModelDoc versions (v28 -> v40+).

* **🧵 Automated Dynamic Cloth & Softbody Physics Transfer**:
  * Scans skeleton bone chains for dynamic elements (hair, braids, tails, ears, fur, cuffs, bolas, jacket sleeves, chains).
  * Generates clean ModelDoc 40 compliant _class = "Softbody" nodes with smooth graduated damping, stiffness, and mass curves.
  * Injects hero-proportioned body collision spheres (ClothShapeSphere) to prevent clipping.
  * Automatically discovers and links custom 2D .dmx cloth proxy meshes (ClothProxyMeshFile) with authentic physics parameters.

* **🎨 Material Remap & Texture Assignment**:
  * Automated material search and remap rules for ported or custom hero textures.

* **🛡️ Backup & Safety System**:
  * Automatic .bak snapshot generation before any modifying operation.
  * 1-Click Revert functionality to restore original .vmdl files instantly.

* **📁 Multi-Addon Scanner**:
  * Real-time auto-discovery of all installed Deadlock addons and target .vmdl models with visual status indicators.

---

## 📥 Installation & Usage

### 🚀 Running the Prebuilt Binary (Recommended)
1. Download the latest release from the [Releases](https://github.com/kwlnd/deadlock-vmdl-compiler/releases) page.
2. Extract the archive and launch DeadlockVmdlCompiler.exe.
3. Set your **CSWin64 / CSDK12 bin directory** (e.g. .../game/csgo/bin/win64 or .../game/bin/win64).
4. Set your **Citadel Addons directory** (e.g. .../content/citadel_addons).
5. Select your addon and target .vmdl model, configure options, and click **🚀 Compile Model** or **🧵 Transfer Cloth**!

---

## 🛠️ Building from Source

### Prerequisites
* [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or .NET 8.0+)
* Windows 10/11 x64

### Build Commands
`ash
# Clone repository
git clone https://github.com/kwlnd/deadlock-vmdl-compiler.git
cd deadlock-vmdl-compiler

# Build release
dotnet build -c Release

# Publish standalone executable
dotnet publish DeadlockVmdlCompiler.csproj -c Release -o ./publish
`

---

## 📄 License
This project is open source and available under the [MIT License](LICENSE).
