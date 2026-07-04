using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AYellowpaper.SerializedCollections;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace Heartwood
{
    public class View : MonoBehaviour
    {
        [SerializeField] private SerializedDictionary<string, GameObject> references;

        // Created lazily on the first SetImageAsync for each reference — so references
        // whose Image is decorative (buttons, static sprites) stay at their prefab color
        // and never get touched. One slot owns the per-Image load state (CTS, handle,
        // task, address, original color) so loads survive multi-frame gaps, can be
        // reassigned or cancelled independently, and are all released in OnDestroy.
        private readonly Dictionary<string, ImageSlot> _imageSlots = new();

        private void OnDestroy()
        {
            foreach (var slot in _imageSlots.Values)
            {
                slot.CancelAndDisposeCts();
                slot.ReleaseHandle();
            }
        }

        public GameObject GetReference(string referenceName)
        {
            if (!references.TryGetValue(referenceName, out var go))
                throw new KeyNotFoundException(
                    $"View on '{name}' has no reference named '{referenceName}'.");
            return go;
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

        // Fire-and-forget entry point. Chains to Core.Instance.Token so a Core-level
        // cancel aborts the load; the task is still tracked on the slot for gather.
        public void SetImage(string referenceName, string address)
            => Core.FireAndForget(SetImageAsync(referenceName, address, Core.Instance.Token));

        // Cancels any prior in-flight load on the same slot, kicks off a new Addressables
        // load, and returns its Task. The Task is stored on the slot so parallel loads on
        // other slots can be scattered and gathered with WhenAllLoadsAsync.
        public Task SetImageAsync(string referenceName, string address, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(address))
                throw new ArgumentException("Address must be a non-empty string.", nameof(address));

            var slot = GetOrCreateImageSlot(referenceName);

            // Same-address rebind: warn and return the existing task. Faulted/cancelled
            // prior loads fall through to a fresh (retry) load.
            if (slot.Address == address && slot.CurrentTask != null &&
                !slot.CurrentTask.IsFaulted && !slot.CurrentTask.IsCanceled)
            {
                Debug.LogWarning(
                    $"View on '{name}': image '{referenceName}' is already loading or showing '{address}'.");
                return slot.CurrentTask;
            }

            // Cancel any prior in-flight load; its continuation will observe the cancel
            // after Addressables completes and release its local handle in its finally.
            slot.CancelAndDisposeCts();

            slot.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slot.Address = address;
            slot.CurrentTask = LoadAndApplyAsync(slot, address, slot.Cts, slot.Cts.Token);
            return slot.CurrentTask;
        }

        // Awaits every currently-tracked in-flight load on this View. Snapshots the task
        // list on entry, so loads started after this call don't extend the wait.
        public Task WhenAllLoadsAsync()
        {
            List<Task> tasks = null;
            foreach (var slot in _imageSlots.Values)
            {
                if (slot.CurrentTask != null && !slot.CurrentTask.IsCompleted)
                {
                    tasks ??= new List<Task>();
                    tasks.Add(slot.CurrentTask);
                }
            }
            return tasks == null ? Task.CompletedTask : Task.WhenAll(tasks);
        }

        // Look up or create the slot for `referenceName`. Slot creation captures the
        // Image's original color (so it can be restored on load) and immediately hides
        // it via alpha=0 — any prefab-authored placeholder disappears the moment we
        // commit to loading over it.
        private ImageSlot GetOrCreateImageSlot(string referenceName)
        {
            if (_imageSlots.TryGetValue(referenceName, out var slot))
                return slot;

            var go = GetReference(referenceName);
            if (go == null)
                throw new KeyNotFoundException(
                    $"View on '{name}' has no reference '{referenceName}'.");
            var image = go.GetComponent<Image>();
            if (image == null)
                throw new MissingComponentException(
                    $"View on '{name}' has no Image component on reference '{referenceName}'.");

            slot = new ImageSlot
            {
                Image = image,
                OriginalColor = image.color,
            };

            var c = image.color;
            c.a = 0f;
            image.color = c;

            _imageSlots[referenceName] = slot;
            return slot;
        }

        private async Task LoadAndApplyAsync(ImageSlot slot, string address,
            CancellationTokenSource myCts, CancellationToken token)
        {
            // Addressables.LoadAssetAsync doesn't accept a CancellationToken — if we get
            // cancelled mid-load, we let it finish and release the just-loaded handle in
            // the finally block below.
            var handle = Addressables.LoadAssetAsync<Sprite>(address);
            var applied = false;
            try
            {
                await handle.Task;

                // Reassigned by a newer SetImageAsync on this slot? A reassign always
                // cancels myCts too, so the identity check is belt-and-suspenders.
                if (slot.Cts != myCts || token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    return;
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw handle.OperationException ?? new Exception(
                        $"Failed to load sprite '{address}'.");

                // View or the Image child was destroyed between kickoff and completion.
                if (slot.Image == null) return;

                // Success. Release the previously-displayed handle (if any), transfer
                // ownership of the new handle to the slot, then swap in the sprite +
                // restore the original color so the alpha=0 preview becomes visible.
                slot.ReleaseHandle();
                slot.Handle = handle;
                applied = true;

                slot.Image.sprite = handle.Result;
                slot.Image.color = slot.OriginalColor;
            }
            finally
            {
                if (!applied && handle.IsValid())
                    Addressables.Release(handle);
            }
        }

        private class ImageSlot
        {
            public Image Image;
            public Color OriginalColor;
            public string Address;
            public CancellationTokenSource Cts;
            public AsyncOperationHandle<Sprite> Handle;
            public Task CurrentTask;

            public void CancelAndDisposeCts()
            {
                if (Cts == null) return;
                Cts.Cancel();
                Cts.Dispose();
                Cts = null;
            }

            public void ReleaseHandle()
            {
                if (Handle.IsValid())
                    Addressables.Release(Handle);
                Handle = default;
            }
        }
    }
}
