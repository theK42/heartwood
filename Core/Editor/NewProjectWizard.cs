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
    // One-stop setup for a new project that has just the Heartwood core package installed:
    // opt in to the PlayFab / Firebase modules, get pointed at the official SDK downloads,
    // and generate a starter Game class. Only touches what's selected, never overwrites
    // existing files, and does nothing until Apply is pressed.
    public class NewProjectWizard : EditorWindow
    {
        private const string CoreKey = "com.thek42.heartwood";
        private const string PlayFabModuleKey = "com.thek42.heartwood.playfab";
        private const string FirebaseModuleKey = "com.thek42.heartwood.firebase";

        private const string PlayFabDownloadUrl = "https://aka.ms/PlayFabUnitySdk";
        private const string FirebaseSetupUrl = "https://firebase.google.com/docs/unity/setup";
        private const string FirebaseConfigFolder = "Assets/FirebaseSettings";

        [SerializeField] private bool _usePlayFab;
        [SerializeField] private string _titleId = "";
        [SerializeField] private bool _useFirebase;
        [SerializeField] private bool _createGame = true;
        [SerializeField] private string _gameName = "";
        [SerializeField] private Vector2 _scroll;

        private bool _coreEntryFound;
        private bool _playFabSdkPresent;
        private bool _playFabModuleInstalled;
        private bool _firebaseSdkPresent;
        private bool _firebaseModuleInstalled;
        private bool _hasGoogleServicesJson;
        private bool _hasGoogleServiceInfoPlist;
        private string _playFabSettingsPath;

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        private static string ManifestPath => Path.Combine(ProjectRoot, "Packages", "manifest.json");

        [MenuItem("Heartwood/New Project Setup")]
        private static void Open() => GetWindow<NewProjectWizard>("Heartwood Setup");

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_gameName)) _gameName = SuggestGameName();
            Refresh();
        }

        private void OnFocus() => Refresh();
        private void OnProjectChange() => Refresh();

        private void Refresh()
        {
            var manifest = File.Exists(ManifestPath) ? File.ReadAllText(ManifestPath) : "";
            _coreEntryFound = Regex.IsMatch(manifest, EntryPattern(CoreKey));
            _playFabModuleInstalled = Regex.IsMatch(manifest, EntryPattern(PlayFabModuleKey));
            _firebaseModuleInstalled = Regex.IsMatch(manifest, EntryPattern(FirebaseModuleKey));

            _playFabSdkPresent = AssetPaths("PlayFab").Any(p => p.EndsWith("/PlayFab.asmdef"));
            _firebaseSdkPresent =
                AssetPaths("Firebase.App").Any(p => p.EndsWith("/Firebase.App.dll")) &&
                AssetPaths("Firebase.Crashlytics").Any(p => p.EndsWith("/Firebase.Crashlytics.dll"));
            _hasGoogleServicesJson = AssetPaths("google-services").Any(p => p.EndsWith("/google-services.json"));
            _hasGoogleServiceInfoPlist = AssetPaths("GoogleService-Info").Any(p => p.EndsWith("/GoogleService-Info.plist"));

            _playFabSettingsPath = AssetPaths("PlayFabSharedSettings").FirstOrDefault(p => p.EndsWith("/PlayFabSharedSettings.asset"));
            if (string.IsNullOrEmpty(_titleId) && _playFabSettingsPath != null)
                _titleId = ReadTitleId();

            Repaint();
        }

        private static IEnumerable<string> AssetPaths(string query) =>
            AssetDatabase.FindAssets(query).Select(AssetDatabase.GUIDToAssetPath);

        private static string EntryPattern(string key) => $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"";

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Sets up optional Heartwood modules and a starter Game class for this project. " +
                "Nothing changes until you press Apply.", MessageType.Info);

            DrawPlayFab();
            EditorGUILayout.Space();
            DrawFirebase();
            EditorGUILayout.Space();
            DrawGameScaffold();
            EditorGUILayout.Space();
            DrawApply();

            EditorGUILayout.EndScrollView();
        }

        private void DrawPlayFab()
        {
            EditorGUILayout.LabelField("PlayFab", EditorStyles.boldLabel);
            _usePlayFab = EditorGUILayout.ToggleLeft("Use PlayFab (adds the ServerAPI base class)", _usePlayFab);
            if (!_usePlayFab) return;

            EditorGUI.indentLevel++;
            DrawStatus(_playFabSdkPresent, "PlayFab SDK in project");
            if (!_playFabSdkPresent)
            {
                EditorGUILayout.HelpBox(
                    "Heartwood's ServerAPI needs PlayFab's legacy Unity SDK (the one with PlayFabClientAPI), " +
                    "not the newer GDK-based package. Download the .unitypackage, then import it.",
                    MessageType.None);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Download SDK")) Application.OpenURL(PlayFabDownloadUrl);
                    if (GUILayout.Button("Import .unitypackage…")) ImportPackageFile();
                }
            }

            DrawStatus(_playFabModuleInstalled, "Heartwood PlayFab module in manifest");
            using (new EditorGUI.DisabledScope(!_playFabSdkPresent))
                _titleId = EditorGUILayout.TextField("Title ID", _titleId);
            EditorGUI.indentLevel--;
        }

        private void DrawFirebase()
        {
            EditorGUILayout.LabelField("Firebase", EditorStyles.boldLabel);
            _useFirebase = EditorGUILayout.ToggleLeft("Use Firebase Crashlytics crash reporting", _useFirebase);
            if (!_useFirebase) return;

            EditorGUI.indentLevel++;
            DrawStatus(_firebaseSdkPresent, "Firebase App + Crashlytics SDK in project");
            if (!_firebaseSdkPresent)
            {
                EditorGUILayout.HelpBox(
                    "Firebase's Unity SDK is distributed as .unitypackage files. Download and unzip it, then " +
                    "import the Crashlytics package (Firebase's setup page lists what it needs).",
                    MessageType.None);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open Firebase setup page")) Application.OpenURL(FirebaseSetupUrl);
                    if (GUILayout.Button("Import .unitypackage…")) ImportPackageFile();
                }
            }

            DrawStatus(_firebaseModuleInstalled, "Heartwood Firebase module in manifest");

            DrawStatus(_hasGoogleServicesJson, "google-services.json (Android)");
            if (!_hasGoogleServicesJson && GUILayout.Button("Add google-services.json…"))
                CopyFirebaseConfigFile("google-services.json", "json");
            DrawStatus(_hasGoogleServiceInfoPlist, "GoogleService-Info.plist (iOS)");
            if (!_hasGoogleServiceInfoPlist && GUILayout.Button("Add GoogleService-Info.plist…"))
                CopyFirebaseConfigFile("GoogleService-Info.plist", "plist");
            EditorGUI.indentLevel--;
        }

        private void DrawGameScaffold()
        {
            EditorGUILayout.LabelField("Starter game", EditorStyles.boldLabel);
            _createGame = EditorGUILayout.ToggleLeft("Create a starter Game class", _createGame);
            if (!_createGame) return;

            EditorGUI.indentLevel++;
            _gameName = EditorGUILayout.TextField("Name (namespace + assembly)", _gameName);
            EditorGUILayout.HelpBox(
                $"Writes Assets/Source/{_gameName}/{_gameName}.asmdef and Game.cs. Existing files are never overwritten.",
                MessageType.None);
            EditorGUI.indentLevel--;
        }

        private void DrawApply()
        {
            var issues = BlockingIssues();
            foreach (var issue in issues)
                EditorGUILayout.HelpBox(issue, MessageType.Warning);

            var nothingSelected = !_usePlayFab && !_useFirebase && !_createGame;
            using (new EditorGUI.DisabledScope(issues.Count > 0 || nothingSelected))
            {
                if (GUILayout.Button("Apply", GUILayout.Height(28)))
                    Apply();
            }
        }

        private static void DrawStatus(bool ok, string text)
        {
            var icon = EditorGUIUtility.IconContent(ok ? "TestPassed" : "TestFailed").image;
            EditorGUILayout.LabelField(new GUIContent(text, icon));
        }

        private List<string> BlockingIssues()
        {
            var issues = new List<string>();

            if ((_usePlayFab || _useFirebase) && !_coreEntryFound)
                issues.Add($"Couldn't find a \"{CoreKey}\" entry in Packages/manifest.json, so the module packages can't be added automatically.");

            if (_usePlayFab)
            {
                if (!_playFabSdkPresent) issues.Add("Import the PlayFab SDK first.");
                else if (!Regex.IsMatch(_titleId ?? "", "^[A-Za-z0-9]{3,10}$")) issues.Add("Enter your PlayFab Title ID.");
            }

            if (_useFirebase && !_firebaseSdkPresent)
                issues.Add("Import the Firebase SDK first.");

            if (_createGame && !IsValidIdentifier(_gameName))
                issues.Add("The game name must be a valid C# identifier (letters, digits, underscores; not starting with a digit).");

            return issues;
        }

        private void Apply()
        {
            var report = new List<string>();

            if (_usePlayFab || _useFirebase)
            {
                var manifest = File.ReadAllText(ManifestPath);
                var changed = false;

                if (_usePlayFab && !_playFabModuleInstalled)
                    changed |= TryAddModule(ref manifest, PlayFabModuleKey, "PlayFab", report);
                if (_useFirebase && !_firebaseModuleInstalled)
                    changed |= TryAddModule(ref manifest, FirebaseModuleKey, "Firebase", report);

                if (changed) File.WriteAllText(ManifestPath, manifest);
            }

            if (_usePlayFab)
                report.Add(TrySetTitleId(_titleId.Trim()) ? "Set the PlayFab Title ID." : "Couldn't set the Title ID — set it in PlayFabSharedSettings.");

            if (_createGame)
                WriteGameScaffold(report);

            AssetDatabase.Refresh();
            Client.Resolve();
            Refresh();

            EditorUtility.DisplayDialog("Heartwood setup", string.Join("\n", report) + "\n\nUnity will now resolve packages and recompile.", "OK");
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

        private string ReadTitleId()
        {
            var settings = AssetDatabase.LoadMainAssetAtPath(_playFabSettingsPath);
            var property = settings == null ? null : new SerializedObject(settings).FindProperty("TitleId");
            return property?.stringValue ?? "";
        }

        // Goes through SerializedObject so the wizard needs no compile-time reference to the PlayFab SDK.
        private bool TrySetTitleId(string titleId)
        {
            var settings = _playFabSettingsPath == null ? null : AssetDatabase.LoadMainAssetAtPath(_playFabSettingsPath);
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

        private void WriteGameScaffold(List<string> report)
        {
            var folder = Path.Combine(ProjectRoot, "Assets", "Source", _gameName);
            Directory.CreateDirectory(folder);

            var references = new List<string> { "Heartwood" };
            if (_usePlayFab) references.AddRange(new[] { "Heartwood.PlayFab", "PlayFab" });
            if (_useFirebase) references.Add("Heartwood.Firebase");

            WriteIfMissing(Path.Combine(folder, _gameName + ".asmdef"), BuildAsmdef(references), report);
            WriteIfMissing(Path.Combine(folder, "Game.cs"), BuildGameClass(), report);
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

        private string BuildAsmdef(List<string> references)
        {
            var refs = string.Join(",\n", references.Select(r => $"        \"{r}\""));
            return
                "{\n" +
                $"    \"name\": \"{_gameName}\",\n" +
                $"    \"rootNamespace\": \"{_gameName}\",\n" +
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

        private string BuildGameClass()
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Heartwood;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine($"namespace {_gameName}");
            sb.AppendLine("{");
            sb.AppendLine("    public class Game : Heartwood.Game");
            sb.AppendLine("    {");
            sb.AppendLine("        // Typed accessor so game code doesn't have to cast Core.Instance.Game on every call.");
            sb.AppendLine("        public static Game Instance => (Game)Core.Instance.Game;");
            sb.AppendLine();
            if (_usePlayFab)
            {
                sb.AppendLine("        public ServerAPI ServerAPI { get; private set; }");
                sb.AppendLine();
            }
            sb.AppendLine("        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]");
            sb.AppendLine("        private static void Register()");
            sb.AppendLine("        {");
            sb.AppendLine("            Core.RegisterGame(() => new Game());");
            if (_useFirebase)
                sb.AppendLine("            Core.RegisterCrashReporter(() => new FirebaseCrashReporter());");
            sb.AppendLine("        }");
            sb.AppendLine();
            if (_usePlayFab)
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

        private static void ImportPackageFile()
        {
            var path = EditorUtility.OpenFilePanel("Select a .unitypackage", "", "unitypackage");
            if (!string.IsNullOrEmpty(path))
                AssetDatabase.ImportPackage(path, true);
        }

        private void CopyFirebaseConfigFile(string fileName, string extension)
        {
            var source = EditorUtility.OpenFilePanel($"Select {fileName}", "", extension);
            if (string.IsNullOrEmpty(source)) return;

            var folder = Path.Combine(ProjectRoot, FirebaseConfigFolder);
            Directory.CreateDirectory(folder);
            File.Copy(source, Path.Combine(folder, fileName), true);
            AssetDatabase.Refresh();
            Refresh();
        }

        private static string SuggestGameName()
        {
            var name = Regex.Replace(PlayerSettings.productName ?? "", "[^A-Za-z0-9_]", "");
            if (name.Length == 0) return "MyGame";
            return char.IsDigit(name[0]) ? "_" + name : name;
        }

        private static bool IsValidIdentifier(string name) =>
            !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");
    }
}
