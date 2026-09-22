using System.Collections.Generic;
using System.Linq;
using AYellowpaper.SerializedCollections;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Heartwood.UI.Editor
{
    // Replaces the default SerializedDictionary inspector for View.references with a
    // categorized, drag-and-drop-friendly editor. The dictionary stays the single
    // source of truth (still keyed by name, still the "just give me the GameObject"
    // escape hatch) — this editor only groups its entries for display, based on
    // whichever component a reference's GameObject carries, and adds a couple of
    // workflows the default drawer doesn't: drop a GameObject to add it keyed by its
    // own name, and rename a key with an offer to rename the GameObject to match.
    [CustomEditor(typeof(View))]
    public class ViewEditor : UnityEditor.Editor
    {
        private enum Category
        {
            InputFields,
            Buttons,
            Toggles,
            Tables,
            Models,
            Images,
            Text,
            NestedViews,
            Other,
            Missing,
        }

        private static readonly (Category category, string label)[] CategoryOrder =
        {
            (Category.InputFields, "Input Fields"),
            (Category.Buttons, "Buttons"),
            (Category.Toggles, "Toggles"),
            (Category.Tables, "Tables (Scroll Rects)"),
            (Category.Models, "Models (Raw Image)"),
            (Category.Images, "Images"),
            (Category.Text, "Text"),
            (Category.NestedViews, "Nested Views"),
            (Category.Other, "Other GameObjects"),
            (Category.Missing, "Missing"),
        };

        // Foldout state per category. Defaults open (true) the first time a category
        // is seen, via the TryGetValue fallback below.
        private readonly Dictionary<Category, bool> _expanded = new();

        private GameObject _pendingAddObject;

        public override void OnInspectorGUI()
        {
            var view = (View)target;
            var references = view.EditorReferences;

            DrawDropZone(view, references);
            EditorGUILayout.Space();
            DrawManualAdd(view, references);
            EditorGUILayout.Space();

            if (references.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No references yet. Drag GameObjects onto the box above to add them.",
                    MessageType.Info);
                return;
            }

            var grouped = references
                .GroupBy(kv => Categorize(kv.Value))
                .ToDictionary(g => g.Key, g => g.OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase).ToList());

            string removeKey = null;
            string retargetKey = null;
            GameObject retargetGo = null;
            string renameFrom = null, renameTo = null;
            GameObject renameGo = null;

            foreach (var (category, label) in CategoryOrder)
            {
                if (!grouped.TryGetValue(category, out var entries) || entries.Count == 0)
                    continue;

                var expanded = _expanded.TryGetValue(category, out var e) ? e : true;
                expanded = EditorGUILayout.Foldout(expanded, $"{label} ({entries.Count})", true);
                _expanded[category] = expanded;
                if (!expanded) continue;

                EditorGUI.indentLevel++;
                foreach (var entry in entries)
                {
                    var key = entry.Key;
                    var go = entry.Value;
                    EditorGUILayout.BeginHorizontal();

                    var newKey = EditorGUILayout.DelayedTextField(key, GUILayout.MinWidth(80));
                    if (newKey != key)
                    {
                        renameFrom = key;
                        renameTo = newKey;
                        renameGo = go;
                    }

                    EditorGUI.BeginChangeCheck();
                    var newGo = (GameObject)EditorGUILayout.ObjectField(go, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck() && newGo != go)
                    {
                        retargetKey = key;
                        retargetGo = newGo;
                    }

                    if (GUILayout.Button("x", GUILayout.Width(20)))
                        removeKey = key;

                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
            }

            if (removeKey != null)
            {
                Undo.RecordObject(view, "Remove View Reference");
                references.Remove(removeKey);
                EditorUtility.SetDirty(view);
            }
            else if (retargetKey != null)
            {
                Undo.RecordObject(view, "Change View Reference Target");
                if (retargetGo == null) references.Remove(retargetKey);
                else references[retargetKey] = retargetGo;
                EditorUtility.SetDirty(view);
            }
            else if (renameFrom != null)
            {
                TryRename(view, references, renameFrom, renameTo, renameGo);
            }
        }

        // Drop zone: accepts one or many GameObjects dragged in from the Hierarchy.
        // Each is keyed by its own name; collisions are logged and skipped rather than
        // silently overwriting an existing entry (the dictionary's whole job is to
        // catch exactly that ambiguity).
        private void DrawDropZone(View view, SerializedDictionary<string, GameObject> references)
        {
            var rect = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            GUI.Box(rect, "Drag GameObjects here to add as references", EditorStyles.helpBox);

            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition)) return;

            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDrop.objectReferences.Any(o => o is GameObject)
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                var added = 0;
                Undo.RecordObject(view, "Add View References");
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is GameObject go && AddReference(references, go))
                        added++;
                }
                if (added > 0) EditorUtility.SetDirty(view);
                evt.Use();
            }
        }

        private void DrawManualAdd(View view, SerializedDictionary<string, GameObject> references)
        {
            EditorGUILayout.BeginHorizontal();
            _pendingAddObject = (GameObject)EditorGUILayout.ObjectField(
                _pendingAddObject, typeof(GameObject), true);
            using (new EditorGUI.DisabledScope(_pendingAddObject == null))
            {
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    Undo.RecordObject(view, "Add View Reference");
                    if (AddReference(references, _pendingAddObject))
                        EditorUtility.SetDirty(view);
                    _pendingAddObject = null;
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static bool AddReference(SerializedDictionary<string, GameObject> references, GameObject go)
        {
            var key = go.name;
            if (references.TryGetValue(key, out var existing))
            {
                if (existing == go) return false;
                Debug.LogWarning(
                    $"View reference '{key}' already points to a different GameObject " +
                    $"('{(existing != null ? existing.name : "<missing>")}'). Rename '{go.name}' " +
                    "before adding it, or remove the existing entry first.");
                return false;
            }
            references.Add(key, go);
            return true;
        }

        private static void TryRename(View view, SerializedDictionary<string, GameObject> references,
            string oldKey, string newKey, GameObject go)
        {
            newKey = newKey?.Trim();
            if (string.IsNullOrEmpty(newKey))
            {
                Debug.LogWarning("View reference names can't be empty.");
                return;
            }
            if (newKey == oldKey) return;
            if (references.ContainsKey(newKey))
            {
                Debug.LogWarning($"View already has a reference named '{newKey}'. Choose a different name.");
                return;
            }

            Undo.RecordObject(view, "Rename View Reference");
            references.Remove(oldKey);
            references.Add(newKey, go);
            EditorUtility.SetDirty(view);

            if (go != null && go.name != newKey &&
                EditorUtility.DisplayDialog("Rename GameObject?",
                    $"Rename GameObject '{go.name}' to '{newKey}' too, to keep it matching the reference ID?",
                    "Rename", "Keep Current Name"))
            {
                Undo.RecordObject(go, "Rename GameObject");
                go.name = newKey;
                EditorUtility.SetDirty(go);
            }
        }

        // Priority order matters: a Button's target graphic is usually an Image on the
        // same GameObject, so Image is checked well after the more specific types that
        // would otherwise be swamped by it.
        private static Category Categorize(GameObject go)
        {
            if (go == null) return Category.Missing;
            if (go.GetComponent<TMP_InputField>() != null) return Category.InputFields;
            if (go.GetComponent<Button>() != null) return Category.Buttons;
            if (go.GetComponent<Toggle>() != null) return Category.Toggles;
            if (go.GetComponent<ScrollRect>() != null) return Category.Tables;
            if (go.GetComponent<RawImage>() != null) return Category.Models;
            if (go.GetComponent<Image>() != null) return Category.Images;
            if (go.GetComponent<TMP_Text>() != null || go.GetComponent<Text>() != null) return Category.Text;
            if (go.GetComponent<View>() != null) return Category.NestedViews;
            return Category.Other;
        }
    }
}
