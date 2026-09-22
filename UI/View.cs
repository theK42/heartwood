using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AYellowpaper.SerializedCollections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Heartwood.UI
{
    // Split across partials by concern: View.Images.cs owns image loading, View.Tables.cs
    // owns the pooled table view. This file holds the shared surface — the serialized
    // references dictionary, the text/action helpers, and the OnDestroy cascade that
    // hands off to each partial's Cleanup* method.
    public partial class View : MonoBehaviour
    {
        [SerializeField] private SerializedDictionary<string, GameObject> references;

#if UNITY_EDITOR
        // Escape hatch for ViewEditor's reference-management UI (drag-and-drop add,
        // category grouping, rename). Not part of the runtime API surface — the
        // dictionary itself is still the single source of truth; ViewEditor only
        // groups its entries for display, it doesn't split the storage.
        public SerializedDictionary<string, GameObject> EditorReferences => references;
#endif

        private void OnDestroy()
        {
            CleanupImages();
            CleanupTables();
            CleanupModels();
        }

        // Awaits every currently-tracked in-flight load on this View, across every kind
        // of asynchronously-loaded content. Mirrors the OnDestroy cascade: each partial
        // contributes its in-flight tasks through a Collect*Loads method, so adding a new
        // load kind means adding one collector here and one Collect* method in its partial.
        // The task list is snapshotted on entry, so loads started after this call don't
        // extend the wait.
        public Task WhenAllLoadsAsync()
        {
            List<Task> tasks = null;
            CollectImageLoads(ref tasks);
            CollectTableLoads(ref tasks);
            CollectModelLoads(ref tasks);
            return tasks == null ? Task.CompletedTask : Task.WhenAll(tasks);
        }

        // Adds `task` to `tasks` (allocating the list on first use) only when it's a real,
        // still-running load. Shared by every partial's Collect*Loads method so the lazy
        // allocation and the "still in flight?" rule live in exactly one place.
        private static void AddInFlightLoad(ref List<Task> tasks, Task task)
        {
            if (task == null || task.IsCompleted) return;
            tasks ??= new List<Task>();
            tasks.Add(task);
        }

        public GameObject GetReference(string referenceName)
        {
            if (!references.TryGetValue(referenceName, out var go))
                throw new KeyNotFoundException(
                    $"View on '{name}' has no reference named '{referenceName}'.");
            return go;
        }

        // True when the reference exists and is wired to a live object. Lets callers treat
        // a reference as optional (e.g. a validation label a prefab may or may not carry)
        // instead of guarding against the KeyNotFoundException the setters throw.
        public bool HasReference(string referenceName) =>
            references.TryGetValue(referenceName, out var go) && go != null;

        // The View on a nested reference — for prefabs that compose sub-Views (e.g. a
        // relationship slot whose own View owns its Name/EndButton/InviteButton).
        public View GetView(string referenceName)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }
            var view = go.GetComponent<View>();
            if (view == null)
            {
                throw new MissingComponentException($"View on '{name}' has no View component on reference '{referenceName}'.");
            }
            return view;
        }

        public void SetText(string referenceName, string text)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }

            var tmpText = go.GetComponent<TMP_Text>();
            if (tmpText != null)
            {
                tmpText.text = text;
            }
            else
            {
                var uiText = go.GetComponent<Text>();
                if (uiText != null)
                {
                    uiText.text = text;
                }
                else
                {
                    throw new MissingComponentException($"View on '{name}' has no text component on reference '{referenceName}'.");
                }
            }
        }

        public void SetAction(string referenceName, Action action)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }
            var button = go.GetComponent<Button>();
            if (button == null)
            {
                throw new MissingComponentException($"View on '{name}' has no button component on reference '{referenceName}'.");
            }
            button.onClick.AddListener(() => action());
        }

        // Enable or disable any Selectable (Button, TMP_InputField, Toggle, ...). Used to
        // gate input — e.g. holding a submit button non-interactable until a field validates.
        public void SetInteractable(string referenceName, bool interactable)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }
            var selectable = go.GetComponent<Selectable>();
            if (selectable == null)
            {
                throw new MissingComponentException($"View on '{name}' has no Selectable component on reference '{referenceName}'.");
            }
            selectable.interactable = interactable;
        }

        // Current text of a TMP_InputField reference.
        public string GetInputText(string referenceName)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }
            var input = go.GetComponent<TMP_InputField>();
            if (input == null)
            {
                throw new MissingComponentException($"View on '{name}' has no TMP_InputField component on reference '{referenceName}'.");
            }
            return input.text;
        }

        // Invoke `action` with the field's new text on every edit of a TMP_InputField
        // reference. Mirrors SetAction's fire-on-event shape.
        public void SetInputChangedAction(string referenceName, Action<string> action)
        {
            var go = GetReference(referenceName);
            if (go == null)
            {
                throw new KeyNotFoundException($"View on '{name}' has no reference '{referenceName}'.");
            }
            var input = go.GetComponent<TMP_InputField>();
            if (input == null)
            {
                throw new MissingComponentException($"View on '{name}' has no TMP_InputField component on reference '{referenceName}'.");
            }
            input.onValueChanged.AddListener(text => action(text));
        }
    }
}
