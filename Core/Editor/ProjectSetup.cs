using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Heartwood
{
    // The work behind Heartwood > New Project Setup, with no window attached, so the same code
    // also runs headless:
    //   unity run <project> --command heartwood setup -- --game <Name> --git_hooks
    //     (needs com.unity.pipeline in the project; see ProjectSetupCommand)
    //   Unity -batchmode -quit -projectPath <project> -executeMethod Heartwood.ProjectSetup.RunFromCommandLine -heartwoodGame <Name> -heartwoodGitHooks
    // Only touches what's selected and never overwrites existing files.
    public static class ProjectSetup
    {
        public const string CoreKey = "com.thek42.heartwood";
        public const string PlayFabModuleKey = "com.thek42.heartwood.playfab";
        public const string FirebaseModuleKey = "com.thek42.heartwood.firebase";

        public class Options
        {
            public bool UsePlayFab;
            public string TitleId = "";
            public bool UseFirebase;
            public bool CreateGame = true;
            public string GameName = "";
            public bool InstallGitHooks;
        }

        // What's already in the project, as far as setup cares.
        public class State
        {
            public bool CoreEntryFound;
            public bool PlayFabSdkPresent;
            public bool PlayFabModuleInstalled;
            public bool FirebaseSdkPresent;
            public bool FirebaseModuleInstalled;
            public bool HasGoogleServicesJson;
            public bool HasGoogleServiceInfoPlist;
            public string PlayFabSettingsPath;
            public bool IsGitRepo;
            public bool GitHooksInstalled;
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        private static string ManifestPath => Path.Combine(ProjectRoot, "Packages", "manifest.json");

        public static State Inspect()
        {
            var manifest = File.Exists(ManifestPath) ? File.ReadAllText(ManifestPath) : "";
            return new State
            {
                CoreEntryFound = Regex.IsMatch(manifest, EntryPattern(CoreKey)),
                PlayFabModuleInstalled = Regex.IsMatch(manifest, EntryPattern(PlayFabModuleKey)),
                FirebaseModuleInstalled = Regex.IsMatch(manifest, EntryPattern(FirebaseModuleKey)),

                PlayFabSdkPresent = AssetPaths("PlayFab").Any(p => p.EndsWith("/PlayFab.asmdef")),
                FirebaseSdkPresent =
                    AssetPaths("Firebase.App").Any(p => p.EndsWith("/Firebase.App.dll")) &&
                    AssetPaths("Firebase.Crashlytics").Any(p => p.EndsWith("/Firebase.Crashlytics.dll")),
                HasGoogleServicesJson = AssetPaths("google-services").Any(p => p.EndsWith("/google-services.json")),
                HasGoogleServiceInfoPlist = AssetPaths("GoogleService-Info").Any(p => p.EndsWith("/GoogleService-Info.plist")),

                PlayFabSettingsPath = AssetPaths("PlayFabSharedSettings").FirstOrDefault(p => p.EndsWith("/PlayFabSharedSettings.asset")),

                IsGitRepo = PackageSourceSwitcher.IsGitRepo(),
                GitHooksInstalled = PackageSourceSwitcher.GitHooksInstalled(),
            };
        }

        private static IEnumerable<string> AssetPaths(string query) =>
            AssetDatabase.FindAssets(query).Select(AssetDatabase.GUIDToAssetPath);

        private static string EntryPattern(string key) => $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"";

        public static List<string> BlockingIssues(Options options, State state)
        {
            var issues = new List<string>();

            if ((options.UsePlayFab || options.UseFirebase) && !state.CoreEntryFound)
                issues.Add($"Couldn't find a \"{CoreKey}\" entry in Packages/manifest.json, so the module packages can't be added automatically.");

            if (options.UsePlayFab)
            {
                if (!state.PlayFabSdkPresent) issues.Add("Import the PlayFab SDK first.");
                else if (!Regex.IsMatch(options.TitleId ?? "", "^[A-Za-z0-9]{3,10}$")) issues.Add("Enter your PlayFab Title ID.");
            }

            if (options.UseFirebase && !state.FirebaseSdkPresent)
                issues.Add("Import the Firebase SDK first.");

            if (options.CreateGame && !IsValidIdentifier(options.GameName))
                issues.Add("The game name must be a valid C# identifier (letters, digits, underscores; not starting with a digit).");

            if (options.InstallGitHooks && !state.IsGitRepo)
                issues.Add($"{ProjectRoot} isn't a git repository yet. Run `git init` there first.");

            return issues;
        }

        // Callers check BlockingIssues first. Returns one line per thing done (or skipped).
        public static List<string> Apply(Options options)
        {
            var state = Inspect();
            var report = new List<string>();

            if (options.UsePlayFab || options.UseFirebase)
            {
                var manifest = File.ReadAllText(ManifestPath);
                var changed = false;

                if (options.UsePlayFab && !state.PlayFabModuleInstalled)
                    changed |= TryAddModule(ref manifest, PlayFabModuleKey, "PlayFab", report);
                if (options.UseFirebase && !state.FirebaseModuleInstalled)
                    changed |= TryAddModule(ref manifest, FirebaseModuleKey, "Firebase", report);

                if (changed) File.WriteAllText(ManifestPath, manifest);
            }

            if (options.UsePlayFab)
                report.Add(TrySetTitleId(state.PlayFabSettingsPath, options.TitleId.Trim())
                    ? "Set the PlayFab Title ID."
                    : "Couldn't set the Title ID — set it in PlayFabSharedSettings.");

            if (options.CreateGame)
                WriteGameScaffold(options, report);

            if (options.InstallGitHooks)
                report.Add(PackageSourceSwitcher.TryInstallGitHooks(out var message) ? message : "Git hooks: " + message);

            AssetDatabase.Refresh();
            Client.Resolve();
            return report;
        }

        // Entry point for -executeMethod. Flags (all optional):
        //   -heartwoodGame <Name>        name for the starter Game class (default: from Product Name)
        //   -heartwoodNoGame             don't write a starter Game class
        //   -heartwoodPlayFab <TitleId>  add the PlayFab module and set the Title ID
        //   -heartwoodFirebase           add the Firebase module
        //   -heartwoodGitHooks           install the pre-commit hook and set core.hooksPath
        // Exits with code 1 if anything blocks setup, leaving the project untouched.
        public static void RunFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            string ValueOf(string flag)
            {
                var i = Array.IndexOf(args, flag);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            bool Has(string flag) => args.Contains(flag);

            var titleId = ValueOf("-heartwoodPlayFab");
            var options = new Options
            {
                UsePlayFab = titleId != null,
                TitleId = titleId ?? "",
                UseFirebase = Has("-heartwoodFirebase"),
                CreateGame = !Has("-heartwoodNoGame"),
                GameName = ValueOf("-heartwoodGame") ?? SuggestGameName(),
                InstallGitHooks = Has("-heartwoodGitHooks"),
            };

            var issues = BlockingIssues(options, Inspect());
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                    Debug.LogError("Heartwood setup: " + issue);
                EditorApplication.Exit(1);
                return;
            }

            foreach (var line in Apply(options))
                Debug.Log("Heartwood setup: " + line);
        }

        private static bool TryAddModule(ref string manifest, string key, string folder, List<string> report)
        {
            var core = Regex.Match(manifest, EntryPattern(CoreKey));
            if (!TryDeriveModuleSpec(core.Groups[1].Value, folder, out var spec))
            {
                report.Add($"Couldn't work out where {key} lives from the core entry ({core.Groups[1].Value}); add it to manifest.json by hand.");
                return false;
            }

            var newline = manifest.Contains("\r\n") ? "\r\n" : "\n";
            manifest = manifest.Insert(core.Index + core.Length, $",{newline}    \"{key}\": \"{spec}\"");
            report.Add($"Added {key}.");
            return true;
        }

        // The modules sit beside core in the Heartwood repo, so their location is the core
        // entry's with the trailing "Core" folder swapped (git URLs carry it in ?path=).
        private static bool TryDeriveModuleSpec(string coreValue, string folder, out string spec)
        {
            var git = Regex.Match(coreValue, @"^(.*\?path=/)Core(#.*)?$");
            if (git.Success)
            {
                spec = git.Groups[1].Value + folder + git.Groups[2].Value;
                return true;
            }

            var local = Regex.Match(coreValue, @"^(file:.*[/\\])Core$");
            if (local.Success)
            {
                spec = local.Groups[1].Value + folder;
                return true;
            }

            spec = null;
            return false;
        }

        public static string ReadTitleId(string playFabSettingsPath)
        {
            var settings = playFabSettingsPath == null ? null : AssetDatabase.LoadMainAssetAtPath(playFabSettingsPath);
            var property = settings == null ? null : new SerializedObject(settings).FindProperty("TitleId");
            return property?.stringValue ?? "";
        }

        // Goes through SerializedObject so setup needs no compile-time reference to the PlayFab SDK.
        private static bool TrySetTitleId(string playFabSettingsPath, string titleId)
        {
            var settings = playFabSettingsPath == null ? null : AssetDatabase.LoadMainAssetAtPath(playFabSettingsPath);
            if (settings == null) return false;

            var serialized = new SerializedObject(settings);
            var property = serialized.FindProperty("TitleId");
            if (property == null) return false;

            property.stringValue = titleId;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static void WriteGameScaffold(Options options, List<string> report)
        {
            var folder = Path.Combine(ProjectRoot, "Assets", "Source", options.GameName);
            Directory.CreateDirectory(folder);

            var references = new List<string> { "Heartwood" };
            if (options.UsePlayFab) references.AddRange(new[] { "Heartwood.PlayFab", "PlayFab" });
            if (options.UseFirebase) references.Add("Heartwood.Firebase");

            WriteIfMissing(Path.Combine(folder, options.GameName + ".asmdef"), BuildAsmdef(options.GameName, references), report);
            WriteIfMissing(Path.Combine(folder, "Game.cs"), BuildGameClass(options), report);
        }

        private static void WriteIfMissing(string path, string content, List<string> report)
        {
            var name = Path.GetFileName(path);
            if (File.Exists(path))
            {
                report.Add($"Left existing {name} alone.");
                return;
            }

            File.WriteAllText(path, content);
            report.Add($"Created {name}.");
        }

        private static string BuildAsmdef(string gameName, List<string> references)
        {
            var refs = string.Join(",\n", references.Select(r => $"        \"{r}\""));
            return
                "{\n" +
                $"    \"name\": \"{gameName}\",\n" +
                $"    \"rootNamespace\": \"{gameName}\",\n" +
                "    \"references\": [\n" + refs + "\n    ],\n" +
                "    \"includePlatforms\": [],\n" +
                "    \"excludePlatforms\": [],\n" +
                "    \"allowUnsafeCode\": false,\n" +
                "    \"overrideReferences\": false,\n" +
                "    \"precompiledReferences\": [],\n" +
                "    \"autoReferenced\": true,\n" +
                "    \"defineConstraints\": [],\n" +
                "    \"versionDefines\": [],\n" +
                "    \"noEngineReferences\": false\n" +
                "}\n";
        }

        private static string BuildGameClass(Options options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Heartwood;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine($"namespace {options.GameName}");
            sb.AppendLine("{");
            sb.AppendLine("    public class Game : Heartwood.Game");
            sb.AppendLine("    {");
            sb.AppendLine("        // Typed accessor so game code doesn't have to cast Core.Instance.Game on every call.");
            sb.AppendLine("        public static Game Instance => (Game)Core.Instance.Game;");
            sb.AppendLine();
            if (options.UsePlayFab)
            {
                sb.AppendLine("        public ServerAPI ServerAPI { get; private set; }");
                sb.AppendLine();
            }
            sb.AppendLine("        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]");
            sb.AppendLine("        private static void Register()");
            sb.AppendLine("        {");
            sb.AppendLine("            Core.RegisterGame(() => new Game());");
            if (options.UseFirebase)
                sb.AppendLine("            Core.RegisterCrashReporter(() => new FirebaseCrashReporter());");
            sb.AppendLine("        }");
            sb.AppendLine();
            if (options.UsePlayFab)
            {
                sb.AppendLine("        public override async Task StartAsync(CancellationToken ct)");
                sb.AppendLine("        {");
                sb.AppendLine("            ServerAPI = new ServerAPI();");
                sb.AppendLine("            await ServerAPI.LoginAsync(ct);");
                sb.AppendLine("        }");
            }
            else
            {
                sb.AppendLine("        public override Task StartAsync(CancellationToken ct)");
                sb.AppendLine("        {");
                sb.AppendLine("            // Game startup goes here.");
                sb.AppendLine("            return Task.CompletedTask;");
                sb.AppendLine("        }");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string SuggestGameName()
        {
            var name = Regex.Replace(PlayerSettings.productName ?? "", "[^A-Za-z0-9_]", "");
            if (name.Length == 0) return "MyGame";
            return char.IsDigit(name[0]) ? "_" + name : name;
        }

        private static bool IsValidIdentifier(string name) =>
            !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");
    }
}
