using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Heartwood
{
    // Toggles the Heartwood packages in the consuming project's Packages/manifest.json between
    // local file: references to a sibling Heartwood checkout (live-editable, for whoever is
    // actively working on Heartwood) and git URLs pinned to one commit sha (read-only, for
    // everyone else). Only packages actually listed in the manifest are touched. Also installs
    // a pre-commit hook in the project that blocks commits while in local mode.
    public static class PackageSourceSwitcher
    {
        // Each package lives in its own subfolder of the Heartwood repo.
        private static readonly (string Key, string Folder)[] Packages =
        {
            ("com.thek42.heartwood", "Core"),
            ("com.thek42.heartwood.playfab", "PlayFab"),
            ("com.thek42.heartwood.firebase", "Firebase"),
        };

        private const string HeartwoodFolderName = "Heartwood";
        private const string HooksFolderName = ".githooks";

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        private static string ManifestPath => Path.Combine(ProjectRoot, "Packages", "manifest.json");
        private static string HeartwoodPath => Path.GetFullPath(Path.Combine(ProjectRoot, "..", HeartwoodFolderName));

        private const string PreCommitHook =
@"#!/bin/sh
# Blocks commits while Packages/manifest.json points a Heartwood package at a local
# file: reference instead of the pinned git URL. Local mode only makes sense on the
# machine that's actively editing Heartwood - committing it would break the build for
# anyone who doesn't have a sibling Heartwood checkout at the same relative path.
#
# Installed by Heartwood > Package Source > Install Git Hooks.
# Disable with: git config --unset core.hooksPath

manifest=$(git show :Packages/manifest.json 2>/dev/null)

if [ -z ""$manifest"" ]; then
    exit 0
fi

if echo ""$manifest"" | grep -q '""com\.thek42\.heartwood[a-z.]*""[[:space:]]*:[[:space:]]*""file:'; then
    echo ""error: Packages/manifest.json references a Heartwood package via a local file: path."" >&2
    echo ""       Run Heartwood > Package Source > Use Git Package in the Unity Editor,"" >&2
    echo ""       then re-stage Packages/manifest.json before committing."" >&2
    exit 1
fi

exit 0
";

        [MenuItem("Heartwood/Package Source/Use Local Copy")]
        private static void UseLocalCopy()
        {
            var missing = InstalledPackages()
                .Where(p => !File.Exists(Path.Combine(HeartwoodPath, p.Folder, "package.json")))
                .Select(p => Path.Combine(HeartwoodPath, p.Folder))
                .ToList();
            if (missing.Count > 0)
            {
                EditorUtility.DisplayDialog("Heartwood not found",
                    "No package.json was found at:\n" + string.Join("\n", missing) + "\n\n" +
                    "Clone git@github.com:theK42/heartwood.git to " + HeartwoodPath + " first.", "OK");
                return;
            }

            if (SetManifestReferences(folder => $"file:../../{HeartwoodFolderName}/{folder}"))
                Debug.Log($"Heartwood package source set to local copy at {HeartwoodPath}.");
        }

        [MenuItem("Heartwood/Package Source/Use Git Package (pin current commit)")]
        private static void UseGitPackage()
        {
            if (!Directory.Exists(HeartwoodPath))
            {
                EditorUtility.DisplayDialog("Heartwood not found",
                    $"No Heartwood checkout was found at:\n{HeartwoodPath}\n\n" +
                    "Can't determine which commit to pin without it.", "OK");
                return;
            }

            RunGit(HeartwoodPath, "fetch");

            var (statusCode, statusOut, _) = RunGit(HeartwoodPath, "status --porcelain");
            if (statusCode != 0)
            {
                EditorUtility.DisplayDialog("Git error", $"`git status` failed in {HeartwoodPath}.", "OK");
                return;
            }
            if (!string.IsNullOrWhiteSpace(statusOut))
            {
                EditorUtility.DisplayDialog("Uncommitted changes",
                    "Heartwood has uncommitted changes:\n\n" + statusOut +
                    "\nCommit (or stash) them before switching to the git package.", "OK");
                return;
            }

            var (upstreamCode, _, _) = RunGit(HeartwoodPath, "rev-parse @{u}");
            if (upstreamCode != 0)
            {
                EditorUtility.DisplayDialog("No upstream branch",
                    "The Heartwood checkout's current branch has no upstream tracking branch " +
                    "configured, so there's nothing to confirm it's been pushed. Push it and set " +
                    "an upstream first (`git push -u origin <branch>`).", "OK");
                return;
            }

            var (_, aheadOut, _) = RunGit(HeartwoodPath, "rev-list @{u}..HEAD --count");
            var unpushedCount = int.Parse(aheadOut.Trim());
            if (unpushedCount > 0)
            {
                EditorUtility.DisplayDialog("Unpushed commits",
                    $"Heartwood has {unpushedCount} unpushed commit(s) on the current branch. " +
                    "Push before switching, otherwise teammates won't be able to fetch the " +
                    "commit this pins to.", "OK");
                return;
            }

            var (_, sha, _) = RunGit(HeartwoodPath, "rev-parse HEAD");
            var (_, remoteUrl, _) = RunGit(HeartwoodPath, "remote get-url origin");
            sha = sha.Trim();
            remoteUrl = remoteUrl.Trim();

            if (SetManifestReferences(folder => $"{remoteUrl}?path=/{folder}#{sha}"))
                Debug.Log($"Heartwood package source set to {remoteUrl}#{sha}.");
        }

        [MenuItem("Heartwood/Package Source/Install Git Hooks")]
        private static void InstallGitHooks()
        {
            var (repoCode, _, _) = RunGit(ProjectRoot, "rev-parse --git-dir");
            if (repoCode != 0)
            {
                EditorUtility.DisplayDialog("Not a git repository",
                    $"{ProjectRoot} isn't a git repository yet. Run `git init` there first.", "OK");
                return;
            }

            // Written if missing, never overwritten, so a project can customize its hook.
            var hookPath = Path.Combine(ProjectRoot, HooksFolderName, "pre-commit");
            if (!File.Exists(hookPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hookPath));
                File.WriteAllText(hookPath, PreCommitHook.Replace("\r\n", "\n"));
                RunGit(ProjectRoot, $"update-index --add --chmod=+x {HooksFolderName}/pre-commit");
            }

            var (code, _, error) = RunGit(ProjectRoot, $"config core.hooksPath {HooksFolderName}");
            if (code == 0)
                Debug.Log($"core.hooksPath set to {HooksFolderName} — the pre-commit safety check is now active.");
            else
                Debug.LogError($"Failed to set core.hooksPath: {error}");
        }

        private static IEnumerable<(string Key, string Folder)> InstalledPackages()
        {
            var text = File.ReadAllText(ManifestPath);
            return Packages.Where(p => Regex.IsMatch(text, ManifestEntryPattern(p.Key)));
        }

        private static string ManifestEntryPattern(string key) => $"\"{Regex.Escape(key)}\"\\s*:\\s*\"[^\"]*\"";

        private static bool SetManifestReferences(Func<string, string> valueForFolder)
        {
            var text = File.ReadAllText(ManifestPath);
            var updatedAny = false;
            foreach (var (key, folder) in Packages)
            {
                var pattern = ManifestEntryPattern(key);
                if (!Regex.IsMatch(text, pattern)) continue;

                var newValue = valueForFolder(folder);
                text = Regex.Replace(text, pattern, _ => $"\"{key}\": \"{newValue}\"");
                updatedAny = true;
            }

            if (!updatedAny)
            {
                EditorUtility.DisplayDialog("manifest.json error",
                    $"Couldn't find any Heartwood package entries in {ManifestPath}.", "OK");
                return false;
            }

            File.WriteAllText(ManifestPath, text);
            Client.Resolve();
            return true;
        }

        private static (int code, string stdout, string stderr) RunGit(string workingDirectory, string arguments)
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
    }
}
