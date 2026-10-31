---
name: new-unity-project
description: Create a new, mostly-empty Unity prototype project in ~/Documents that uses the Heartwood package (like Pyramid, DecoVania, VivaMon, Logistix), with a git repo and an initial commit. Use when the user asks to start a new Unity project, game prototype, or "a project with my usual stuff / Heartwood".
---

# New Heartwood Unity project

Creates `~/Documents/<Name>`: Unity URP template, Heartwood core pinned to the latest pushed
commit, a starter `Game` class + asmdef and git hooks (both written by Heartwood's own setup
code, not by hand), an EditMode test assembly, git repo with one commit. Then moves the
session into the new folder.

All paths below are Git Bash style. Template files are in this skill's `templates/` folder
(`~/.claude/skills/new-unity-project/templates`).

## 1. Gather inputs

- **Name** — required. Must be a valid C# identifier (it becomes the namespace and assembly
  name). Ask if not given; offer a couple of placeholder names.
- **Template** — `3d` (default; URP 3D like DecoVania/VivaMon) or `2d`. Only ask if the user
  hints at 2D.
- Fail early if `~/Documents/<Name>` already exists.

## 2. Find the tools

- Unity CLI: `unity` is often not on the Bash tool's PATH. Use
  `U="$LOCALAPPDATA/Unity/bin/unity.exe"` (Windows) if `command -v unity` finds nothing.
  Always pass `--no-banner`.
- Paths: the Unity CLI wants native paths. Set `DOCS="$(cygpath -w ~/Documents)"` and
  `PROJ="$DOCS\<Name>"`, and use those below.
- Editor version: newest installed, from `$U --no-banner editors -i --json`
  (`data.items[].version`). Heartwood needs 6000.6 or later (`unity` in `Core/package.json`).
- Heartwood pin: `git ls-remote git@github.com:theK42/heartwood.git HEAD` → `<sha>`.

## 3. Create the project

```bash
$U --no-banner --non-interactive projects create <Name> --path "$DOCS" --editor-version <version> --template 3d --no-cloud
```

Takes a minute or two; use a long timeout. It registers the project in Unity Hub and adds
`com.unity.pipeline` (needed for step 5) and Cinemachine to the manifest — keep both.

## 4. Adjust the template

In the project folder:

- Delete the tutorial content: `Assets/TutorialInfo`, `Assets/TutorialInfo.meta`,
  `Assets/Readme.asset`, `Assets/Readme.asset.meta` (whichever exist).
- `Packages/manifest.json`: add
  `"com.thek42.heartwood": "git@github.com:theK42/heartwood.git?path=/Core#<sha>"`, remove
  `com.unity.collab-proxy`, keep keys sorted, LF line endings, 2-space indent (edit it with a
  small `python -I` script using an OrderedDict rather than sed).
- Copy `templates/gitignore` → `.gitignore` and `templates/gitattributes` → `.gitattributes`.
- Copy `templates/Tests.asmdef` → `Assets/Tests/EditMode/<Name>.Tests.asmdef`, replacing
  `__NAME__` with the name.
- `git init -q -b main` (must happen before step 5 so the git hooks can install).

## 5. Run Heartwood's setup headless

```bash
$U --no-banner run "$PROJ" --no-tail --log-file "$PROJ\Logs\setup.log" --command heartwood.setup --format json -- --game <Name> --git_hooks
```

This also does the first import (package resolve + compile), so it's slow. Expect
`"success": true` with `result.applied` listing `Created <Name>.asmdef.`, `Created Game.cs.`
and the `core.hooksPath` line. If it fails, the error message says why (nothing is changed
on failure) — fix and rerun.

Other setup flags, only if the user asks: `--playfab_title_id <id>`, `--firebase` (both need
the SDK imported first — that's a manual step in the Editor via Heartwood > New Project
Setup), `--no_game`.

## 6. Verify it compiles

The generated scripts compile on the next Editor launch:

```bash
$U --no-banner run "$PROJ" --no-tail --log-file "$PROJ\Logs\verify.log"
grep -E "error CS|Compilation failed" ~/Documents/<Name>/Logs/verify.log
```

No matches = good. The only expected noise is "Assembly for Assembly Definition File
'Assets/Tests/EditMode/<Name>.Tests.asmdef' will not be compiled, because it has no scripts".

## 7. Commit

```bash
git add -A
git commit -m "Initial commit: empty Heartwood project" -m "<editor version> URP <template> template with Heartwood core, a starter Game class and an EditMode test assembly."
```

End the message with the commit attribution lines the session asks for. The Heartwood
pre-commit hook runs on this commit; it should pass. Don't add a remote or push unless asked.

## 8. Move into the project

- **Claude desktop app:** call `change_directory` with the project's absolute path (`$PROJ`) so
  the session continues inside the new project.
- **Claude Code CLI** (no directory switching): tell the user to open the folder in Rider and
  start `claude` in Rider's terminal there.

Report: path, editor version, Heartwood sha, what setup created, and that it compiled clean.

## Gotchas (Unity CLI 1.0.0-beta)

- `run --command` needs the dotted name `heartwood.setup`; `"heartwood setup"` is rejected
  (with a "Did you mean 'heartwood setup'?" error). `unity command heartwood setup` against an
  open Editor uses the spaced form.
- `run` manages `-quit` itself; passing it after `--` is an error.
- `--log-file` fails (Unity exit 127) if its folder doesn't exist. The project's `Logs/` is
  created by `projects create`, so use that.
- If a project lacks `com.unity.pipeline`, the fallback is
  `$U run <project> -- -executeMethod Heartwood.ProjectSetup.RunFromCommandLine -heartwoodGame <Name> -heartwoodGitHooks`
  (exit code 1 if setup is blocked; details in the log).
