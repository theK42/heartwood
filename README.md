# Heartwood

Shared framework code for Kelson's Unity prototypes, split into a core package and
optional modules. Each package lives in its own subfolder of this repo.

| Package | Folder | Contents |
| --- | --- | --- |
| `com.thek42.heartwood` | `Core/` | Async-first `Core` utilities, the `View`/`Screen` UI system, the `ICrashReporter` hook. Vendors SerializedDictionary (MIT) under `Core/ThirdParty`. |
| `com.thek42.heartwood.playfab` | `PlayFab/` | PlayFab-backed `ServerAPI` and the editor account switcher. Needs the PlayFab Unity SDK (asmdef `PlayFab`). |
| `com.thek42.heartwood.firebase` | `Firebase/` | `FirebaseCrashReporter` (Crashlytics). Needs the Firebase Unity SDK (`Firebase.App.dll`, `Firebase.Crashlytics.dll`). |

Core has no dependency on PlayFab or Firebase; install only the modules a project uses.
The modules require the core package.

## Starting a new project

Add just the core package (below), then open **Heartwood → New Project Setup**. The wizard
lets you opt in to PlayFab and/or Firebase and generates a starter `Game` class. For each
SDK it detects whether the SDK is in the project and, if not, links to the official download
and offers an **Import .unitypackage…** button (the PlayFab legacy SDK and the Firebase Unity
SDK are both `.unitypackage`-only). Once an SDK is present, **Apply**:

- adds the matching Heartwood module to `manifest.json` (its location is derived from your
  core entry, so it works for both git and `file:` references),
- writes your PlayFab Title ID into `PlayFabSharedSettings`,
- writes `Assets/Source/<Name>/<Name>.asmdef` and `Game.cs` (registering the crash reporter
  and logging in to PlayFab as selected). Existing files are never overwritten.

Firebase's `google-services.json` / `GoogleService-Info.plist` can be copied in from the
wizard too. The PlayFab SDK to use is the legacy one (`PlayFabClientAPI`), not the newer
GDK-based `microsoft.playfab.sdk` package.

## Consuming these packages

Add the packages to the consuming project's `Packages/manifest.json`. Because UPM git
URLs support subfolders, each package is referenced with `?path=`:

```json
"com.thek42.heartwood": "git@github.com:theK42/heartwood.git?path=/Core#<sha>",
"com.thek42.heartwood.playfab": "git@github.com:theK42/heartwood.git?path=/PlayFab#<sha>"
```

For live editing, point them at a sibling checkout of this repo instead (Unity reads the
folders directly, so edits show up immediately with no publish step):

```json
"com.thek42.heartwood": "file:../../Heartwood/Core",
"com.thek42.heartwood.playfab": "file:../../Heartwood/PlayFab"
```

Pyramid has an Editor menu (**Heartwood → Package Source**) that toggles between the two
and a pre-commit hook that stops the `file:` form being committed; see its README.

### Registering optional pieces

Core doesn't know about the modules; the game registers what it uses from a
`[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]` method:

```csharp
Core.RegisterGame(() => new Game());
Core.RegisterCrashReporter(() => new FirebaseCrashReporter()); // optional
```

The game's own asmdef references `Heartwood`, plus `Heartwood.PlayFab` / `Heartwood.Firebase`
for the modules it installs.

## Third-party notes

- SerializedDictionary (`Core/ThirdParty/SerializedDictionary`) is MIT-licensed; its
  `LICENSE.md` travels with it. It's expected to go away once Unity's built-in dictionary
  serialization (6.6+) replaces it.
- The vendored copy is patched in three places (one `.uxml`, two editor scripts) to load its
  editor assets from `Packages/com.thek42.heartwood/ThirdParty/...` instead of the upstream's
  hardcoded `Assets/Plugins/SerializedCollections/...`. Reapply that if re-vendoring.
- A project must not also contain its own copy of SerializedDictionary (duplicate
  `AYellowpaper.SerializedCollections` assemblies).
- PlayFab and Firebase SDKs are not installed by these packages. Each module's asmdef
  references them by assembly name, so the project just needs them present.

## Layout of each package

- `Runtime/` — the runtime assembly (`Heartwood`, `Heartwood.PlayFab`, `Heartwood.Firebase`).
- `Editor/` — editor-only assemblies (`Heartwood.Editor`, `Heartwood.PlayFab.Editor`).
