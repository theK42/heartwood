using System.IO;
using UnityEditor;
using UnityEngine;

namespace Heartwood
{
    // One-stop setup for a new project that has just the Heartwood core package installed:
    // opt in to the PlayFab / Firebase modules, get pointed at the official SDK downloads,
    // generate a starter Game class and install the git hooks. The work itself lives in
    // ProjectSetup (which can also run headless); this window only gathers the options, and
    // does nothing until Apply is pressed.
    public class NewProjectWizard : EditorWindow
    {
        private const string PlayFabDownloadUrl = "https://aka.ms/PlayFabUnitySdk";
        private const string FirebaseSetupUrl = "https://firebase.google.com/docs/unity/setup";
        private const string FirebaseConfigFolder = "Assets/FirebaseSettings";

        [SerializeField] private bool _usePlayFab;
        [SerializeField] private string _titleId = "";
        [SerializeField] private bool _useFirebase;
        [SerializeField] private bool _createGame = true;
        [SerializeField] private string _gameName = "";
        [SerializeField] private bool _installGitHooks = true;
        [SerializeField] private Vector2 _scroll;

        private ProjectSetup.State _state = new ProjectSetup.State();

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        [MenuItem("Heartwood/New Project Setup")]
        private static void Open() => GetWindow<NewProjectWizard>("Heartwood Setup");

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_gameName)) _gameName = ProjectSetup.SuggestGameName();
            Refresh();
        }

        private void OnFocus() => Refresh();
        private void OnProjectChange() => Refresh();

        private void Refresh()
        {
            _state = ProjectSetup.Inspect();
            if (string.IsNullOrEmpty(_titleId) && _state.PlayFabSettingsPath != null)
                _titleId = ProjectSetup.ReadTitleId(_state.PlayFabSettingsPath);

            Repaint();
        }

        private ProjectSetup.Options CurrentOptions => new ProjectSetup.Options
        {
            UsePlayFab = _usePlayFab,
            TitleId = _titleId,
            UseFirebase = _useFirebase,
            CreateGame = _createGame,
            GameName = _gameName,
            InstallGitHooks = _installGitHooks && !_state.GitHooksInstalled,
        };

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
            DrawGitHooks();
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
            DrawStatus(_state.PlayFabSdkPresent, "PlayFab SDK in project");
            if (!_state.PlayFabSdkPresent)
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

            DrawStatus(_state.PlayFabModuleInstalled, "Heartwood PlayFab module in manifest");
            using (new EditorGUI.DisabledScope(!_state.PlayFabSdkPresent))
                _titleId = EditorGUILayout.TextField("Title ID", _titleId);
            EditorGUI.indentLevel--;
        }

        private void DrawFirebase()
        {
            EditorGUILayout.LabelField("Firebase", EditorStyles.boldLabel);
            _useFirebase = EditorGUILayout.ToggleLeft("Use Firebase Crashlytics crash reporting", _useFirebase);
            if (!_useFirebase) return;

            EditorGUI.indentLevel++;
            DrawStatus(_state.FirebaseSdkPresent, "Firebase App + Crashlytics SDK in project");
            if (!_state.FirebaseSdkPresent)
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

            DrawStatus(_state.FirebaseModuleInstalled, "Heartwood Firebase module in manifest");

            DrawStatus(_state.HasGoogleServicesJson, "google-services.json (Android)");
            if (!_state.HasGoogleServicesJson && GUILayout.Button("Add google-services.json…"))
                CopyFirebaseConfigFile("google-services.json", "json");
            DrawStatus(_state.HasGoogleServiceInfoPlist, "GoogleService-Info.plist (iOS)");
            if (!_state.HasGoogleServiceInfoPlist && GUILayout.Button("Add GoogleService-Info.plist…"))
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

        private void DrawGitHooks()
        {
            EditorGUILayout.LabelField("Git hooks", EditorStyles.boldLabel);
            if (_state.GitHooksInstalled)
            {
                DrawStatus(true, "Pre-commit hook installed");
                return;
            }

            _installGitHooks = EditorGUILayout.ToggleLeft("Install the pre-commit hook that blocks committing local Heartwood references", _installGitHooks);
        }

        private void DrawApply()
        {
            var options = CurrentOptions;
            var issues = ProjectSetup.BlockingIssues(options, _state);
            foreach (var issue in issues)
                EditorGUILayout.HelpBox(issue, MessageType.Warning);

            var nothingSelected = !options.UsePlayFab && !options.UseFirebase && !options.CreateGame && !options.InstallGitHooks;
            using (new EditorGUI.DisabledScope(issues.Count > 0 || nothingSelected))
            {
                if (GUILayout.Button("Apply", GUILayout.Height(28)))
                    Apply(options);
            }
        }

        private static void DrawStatus(bool ok, string text)
        {
            var icon = EditorGUIUtility.IconContent(ok ? "TestPassed" : "TestFailed").image;
            EditorGUILayout.LabelField(new GUIContent(text, icon));
        }

        private void Apply(ProjectSetup.Options options)
        {
            var report = ProjectSetup.Apply(options);
            Refresh();

            EditorUtility.DisplayDialog("Heartwood setup", string.Join("\n", report) + "\n\nUnity will now resolve packages and recompile.", "OK");
        }

        private static void ImportPackageFile()
        {
            var path = EditorUtility.OpenFilePanel("Select a .unitypackage", "", "unitypackage");
            if (!string.IsNullOrEmpty(path))
                UnityEditor.AssetPackage.Package.Import(path, true);
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
    }
}
