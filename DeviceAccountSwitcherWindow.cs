#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Heartwood
{
    // Dev-only tool. Lets a single Unity Editor masquerade as multiple PlayFab accounts
    // by appending a postfix to SystemInfo.deviceUniqueIdentifier before login (see
    // ServerAPI.CustomId). Pick a slot here, then enter Play Mode so LoginAsync picks it
    // up. Nothing about this ships in a player build — ServerAPI.CustomId ignores the
    // postfix outside the editor and this whole file is stripped by the UNITY_EDITOR guard.
    public class DeviceAccountSwitcherWindow : EditorWindow
    {
        // Newline-joined list of the postfixes offered as slots. Persisted per machine.
        private const string PostfixListKey = "Heartwood.ServerAPI.AccountPostfixList";

        // "" is the real device account (no postfix); the rest are alternates.
        private static readonly string[] DefaultPostfixes = { "", "-alt1", "-alt2", "-alt3" };

        private List<string> _postfixes;
        private string _newPostfix = "";

        [MenuItem("Heartwood/Account Switcher")]
        private static void Open() => GetWindow<DeviceAccountSwitcherWindow>("Account Switcher");

        private void OnEnable()
        {
            var stored = EditorPrefs.GetString(PostfixListKey, string.Join("\n", DefaultPostfixes));
            _postfixes = stored.Split('\n').ToList();
        }

        private void SaveList() => EditorPrefs.SetString(PostfixListKey, string.Join("\n", _postfixes));

        private static string ActivePostfix
        {
            get => EditorPrefs.GetString(ServerAPI.AccountPostfixKey, string.Empty);
            set => EditorPrefs.SetString(ServerAPI.AccountPostfixKey, value);
        }

        private static string Label(string postfix) =>
            string.IsNullOrEmpty(postfix) ? "(default — real device account)" : postfix;

        private void OnGUI()
        {
            var active = ActivePostfix;

            EditorGUILayout.HelpBox(
                "Appends a postfix to the device ID used as the PlayFab CustomId. Select a " +
                "slot, then enter Play Mode — the choice is read once at login.",
                MessageType.Info);

            EditorGUILayout.LabelField("Base device ID", SystemInfo.deviceUniqueIdentifier, EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(
                "Active CustomId:  " + SystemInfo.deviceUniqueIdentifier + active,
                EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (EditorApplication.isPlaying)
                EditorGUILayout.HelpBox(
                    "In Play Mode. Restart Play Mode after switching slots to log in as the other account.",
                    MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Accounts", EditorStyles.boldLabel);

            for (var i = 0; i < _postfixes.Count; i++)
            {
                var postfix = _postfixes[i];
                EditorGUILayout.BeginHorizontal();

                var isActive = postfix == active;
                if (EditorGUILayout.ToggleLeft(Label(postfix), isActive) && !isActive)
                    ActivePostfix = postfix;

                // The default (empty) slot is a fixture — always available, never removable.
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(postfix)))
                {
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        if (postfix == active) ActivePostfix = string.Empty;
                        _postfixes.RemoveAt(i);
                        SaveList();
                        GUIUtility.ExitGUI();
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            _newPostfix = EditorGUILayout.TextField("New postfix", _newPostfix);
            var canAdd = !string.IsNullOrEmpty(_newPostfix) && !_postfixes.Contains(_newPostfix);
            using (new EditorGUI.DisabledScope(!canAdd))
            {
                if (GUILayout.Button("Add", GUILayout.Width(70)))
                {
                    _postfixes.Add(_newPostfix);
                    _newPostfix = "";
                    SaveList();
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
