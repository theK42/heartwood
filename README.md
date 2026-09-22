# Heartwood

Shared framework code for Kelson's Unity prototypes: async-first `Core` utilities, a
PlayFab-backed `ServerAPI`, and a `View`/`Screen`-based UI system.

## Consuming this package

This is a Unity package (UPM). In the consuming project's `Packages/manifest.json`, add it
as a local package pointing at wherever you've cloned this repo, e.g. as a sibling of the
project folder:

```json
"com.thek42.heartwood": "file:../../Heartwood"
```

Unity reads the folder live, so edits made here show up immediately in any project
referencing it via a `file:` path — no publish/update step needed during development.

### Peer dependencies

These aren't UPM packages in this project's setup, so they aren't declared in
`package.json`. A consuming project needs its own copy alongside Heartwood:

- **PlayFab C# SDK** — asmdef name `PlayFab` (dropped into `Assets/`, not UPM).
- **AYellowpaper SerializedCollections** — asmdef name `AYellowpaper.SerializedCollections`
  (dropped into `Assets/`, not UPM). Available at
  https://github.com/AYellowpaper/SerializedDictionary if you'd rather install it via UPM
  git URL in a new project.
- **Firebase App + Crashlytics** — precompiled `Firebase.App.dll` /
  `Firebase.Crashlytics.dll`, only if you use the runtime assembly's Firebase-dependent code.

## Layout

- `Runtime/` — the `Heartwood` assembly (game code, all platforms).
- `Editor/` — the `Heartwood.Editor` assembly (editor-only tooling: the `View` inspector,
  the device account switcher).
