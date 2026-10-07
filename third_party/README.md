# Bundled ValveResourceFormat package

`ValveResourceFormat.20.0.7151-vcs72.1.nupkg` preserves the experimental cloth
exporter used by the working `deadlock-model-decompiler` project and adds VCS 72
shader support required by the September 2026 Deadlock update. The local version
distinguishes it from official NuGet releases. Upstream MIT licenses and notices
are retained.

Source:

- Original cloth-enabled merge commit: [`8232238a10ba67bfbce8b2c5086b0d7d3e18b8ae`](https://github.com/ValveResourceFormat/ValveResourceFormat/commit/8232238a10ba67bfbce8b2c5086b0d7d3e18b8ae).
- VCS 72 support: [`cbca49a0bcf56633825b5efbe0e6cd4599dd156c`](https://github.com/ValveResourceFormat/ValveResourceFormat/commit/cbca49a0bcf56633825b5efbe0e6cd4599dd156c).
- `ValveResourceFormat-vcs72.patch` contains the shader library changes applied to the original source. Model and cloth export source is unchanged.

To rebuild, check out the original merge commit, apply the included patch from
the VRF repository root, and run:

```powershell
dotnet pack ValveResourceFormat/ValveResourceFormat.csproj -c Release -p:PackageVersion=20.0.7151-vcs72.1 -p:TreatWarningsAsErrors=false
```

SHA256: `D141534CB6BF0892918D34AB9BEE950171803F976ED0C74E690A1F1DA9DEBC14`
