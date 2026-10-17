# Bundled ValveResourceFormat package

`ValveResourceFormat.20.2.0-master.aab066c.nupkg` is built from upstream
ValveResourceFormat `master` at commit
[`aab066c`](https://github.com/ValveResourceFormat/ValveResourceFormat/commit/aab066c),
because no NuGet release contains everything Deadlock needs yet:

- the cloth exporter (`ValveResourceFormat/IO/Extract/Cloth`), which rebuilds cloth
  chains, proxy meshes and collision shapes from the FeModel;
- VCS 72 shader support;
- the binary KV3 types used by models since the October 2026 update. The 20.0 and
  20.1 releases fail on them with `Unknown KVType ... 26`.

The prerelease version label distinguishes it from official NuGet releases. Upstream
MIT licenses and notices are retained.

To rebuild, check out that commit in the VRF repository and run:

```powershell
dotnet pack ValveResourceFormat/ValveResourceFormat.csproj -c Release -p:PackageVersion=20.2.0-master.aab066c -p:TreatWarningsAsErrors=false
```

SHA256: `0FB3364F143A83D95723833413C1AC4ED70CA0841586F4C52242DC76FC800DD8`

`valvepak.6.0.0.182.nupkg` is the ValvePak version this VRF build references.
