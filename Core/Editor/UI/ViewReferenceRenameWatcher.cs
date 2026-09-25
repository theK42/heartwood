using System.Collections.Generic;
using AYellowpaper.SerializedCollections;
using UnityEditor;
using UnityEngine;

namespace Heartwood.UI.Editor
{
    // Mirrors ViewEditor's rename prompt in the opposite direction: renaming a
    // reference's key there offers to rename the GameObject to match, and this
    // watches for the GameObject being renamed in the Hierarchy instead, offering to
    // rename the reference key to follow.
    //
    // There's no "GameObject renamed" event to hook, so this diffs referenced
    // GameObjects' names against a per-session baseline every time hierarchyChanged
    // fires (debounced to one scan per batch of changes). A reference's baseline is
    // seeded from its GameObject's current name the first time it's seen this
    // session — never compared against on that first sight — so pre-existing or
    // previously-declined divergences never trigger a prompt; only a name that
    // actually changes while this watcher is running does.
    [InitializeOnLoad]
    internal static class ViewReferenceRenameWatcher
    {
        private static readonly Dictionary<(EntityId viewId, string key), string> _lastKnownName = new();
        private static bool _scanQueued;

        static ViewReferenceRenameWatcher()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private static void OnHierarchyChanged()
        {
            if (_scanQueued) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;

            _scanQueued = true;
            EditorApplication.delayCall += () =>
            {
                _scanQueued = false;
                ScanForRenames();
            };
        }

        private static void ScanForRenames()
        {
            foreach (var view in Resources.FindObjectsOfTypeAll<View>())
            {
                // FindObjectsOfTypeAll also returns prefab/asset copies that aren't
                // part of any open scene (or prefab stage) — those don't have a
                // meaningful "Hierarchy" to have been renamed in.
                if (!view.gameObject.scene.IsValid()) continue;

                var references = view.EditorReferences;
                if (references == null || references.Count == 0) continue;

                var viewId = view.GetEntityId();
                // Snapshot before iterating — a confirmed rename mutates the dictionary.
                foreach (var entry in new List<KeyValuePair<string, GameObject>>(references))
                {
                    CheckEntry(view, references, viewId, entry.Key, entry.Value);
                }
            }
        }

        private static void CheckEntry(View view, SerializedDictionary<string, GameObject> references,
            EntityId viewId, string key, GameObject go)
        {
            if (go == null) return;

            var cacheKey = (viewId, key);
            if (!_lastKnownName.TryGetValue(cacheKey, out var lastName))
            {
                // First sight this session — establish the baseline, nothing to
                // compare against yet.
                _lastKnownName[cacheKey] = go.name;
                return;
            }

            if (go.name == lastName) return;

            _lastKnownName[cacheKey] = go.name;
            if (go.name == key) return; // renamed back to match the reference already

            if (EditorUtility.DisplayDialog("Rename View Reference?",
                $"On '{view.name}', the GameObject for reference '{key}' was renamed to '{go.name}'.\n\n" +
                "Rename the reference to match?",
                "Rename", "Keep Reference Name"))
            {
                RenameReference(view, references, viewId, key, go.name);
            }
        }

        private static void RenameReference(View view, SerializedDictionary<string, GameObject> references,
            EntityId viewId, string oldKey, string newKey)
        {
            if (references.ContainsKey(newKey))
            {
                Debug.LogWarning(
                    $"View on '{view.name}' already has a reference named '{newKey}'; leaving '{oldKey}' as is.");
                return;
            }

            Undo.RecordObject(view, "Rename View Reference");
            var go = references[oldKey];
            references.Remove(oldKey);
            references.Add(newKey, go);
            EditorUtility.SetDirty(view);

            _lastKnownName.Remove((viewId, oldKey));
            _lastKnownName[(viewId, newKey)] = newKey;
        }
    }
}
