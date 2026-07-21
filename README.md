<p align="center">
  <img src="assets/logo.png" width="128" alt="OFS-Modding">
</p>

# Crash & Drift

![Crash & Drift icon](icon.png)

Small Ore Factory Squad mod built with OFS SDK.

- When the locally driven SCC vehicle hits another SCC or traffic vehicle, the
  other vehicle plays a visual-only explosion and disappears.
- Holding either Shift key while driving lowers the active vehicle's sideways
  grip. Releasing Shift restores the exact original drivetrain values.
- The explosion prefab contains only Unity particle systems. It does not invoke
  dynamite, terrain excavation, player damage or physics explosion forces.

Version `0.1.2` intentionally declares `multiplayer: incompatible`. Despawning
vanilla vehicles is server-authoritative in a single-player host, but the mod
does not yet ship the validation/authorization needed for remote peers.

Build the mod from the repository root:

```powershell
./eng/build.ps1
./eng/package.ps1 -ManagerPath C:/path/to/ofs-manager.exe
```

The repository includes the verified 51 KB VFX bundle and its indexed SHA-256,
so a clean checkout produces the playable package without opening Unity. The
original prefab, materials, and editor builder remain under `authoring/` for
reproducible rebuilding with `OFS-Modding/ofs-asset-authoring` and Unity
6000.3.13f1.
