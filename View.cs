using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AYellowpaper.SerializedCollections;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace Heartwood
{
    // Setup/teardown recipe for a pooled table's elements. Passed to View.SetTable.
    // OnAttach is called each time a pooled element becomes visible, with the index it
    // now represents and a CancellationToken tied to that element's active binding —
    // pass the token to any async work (SetImageAsync, sub-tables) so it's cancelled
    // when the element scrolls off or the table is torn down. OnDetach is optional
    // and fires after the element's token has already been cancelled.
    public class TableAdapter
    {
        public Action<View, int, CancellationToken> OnAttach;
        public Action<View, int> OnDetach;
    }

    public class View : MonoBehaviour
    {
        [SerializeField] private SerializedDictionary<string, GameObject> references;

        // Created lazily on the first SetImageAsync for each reference — so references
        // whose Image is decorative (buttons, static sprites) stay at their prefab color
        // and never get touched. One slot owns the per-Image load state (CTS, handle,
        // task, address, original color) so loads survive multi-frame gaps, can be
        // reassigned or cancelled independently, and are all released in OnDestroy.
        private readonly Dictionary<string, ImageSlot> _imageSlots = new();

        // Same lazy-per-reference pattern as _imageSlots, but for pooled table views
        // hosted on a ScrollRect. One slot owns the prefab handle, the active/free
        // pools, the scroll subscription, and the CTS chain — all released either
        // when SetTable is called again on the same reference or in OnDestroy.
        private readonly Dictionary<string, TableSlot> _tableSlots = new();

        // Extra bound elements to keep on each side of the visible range so scrolling
        // reveals already-attached rows instead of instantiating on the same frame.
        private const int TableOverscan = 1;

        private void OnDestroy()
        {
            foreach (var slot in _imageSlots.Values)
            {
                slot.CancelAndDisposeCts();
                slot.ReleaseHandle();
            }
            foreach (var slot in _tableSlots.Values)
            {
                slot.TearDown();
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

        // Fire-and-forget entry point. Chains to Core.Instance.Token so a Core-level
        // cancel aborts the load; the returned task is stored on the slot for tracking.
        public void SetTable(string referenceName, string elementAddress, int count, TableAdapter adapter)
            => Core.FireAndForget(SetTableAsync(referenceName, elementAddress, count, adapter, Core.Instance.Token));

        // Configures a pooled table view on the referenced ScrollRect. Every call is a
        // full rebuild — prior instances, prefab handle, and scroll subscription are
        // torn down before the new configuration takes over. Element prefabs must have
        // a View at their root and a fixed size on the scroll axis.
        public Task SetTableAsync(string referenceName, string elementAddress, int count,
            TableAdapter adapter, CancellationToken ct)
        {
            if (adapter == null)
                throw new ArgumentNullException(nameof(adapter));
            if (adapter.OnAttach == null)
                throw new ArgumentException(
                    $"{nameof(TableAdapter)}.{nameof(TableAdapter.OnAttach)} must be set.", nameof(adapter));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");
            if (count > 0 && string.IsNullOrEmpty(elementAddress))
                throw new ArgumentException(
                    "Element address must be a non-empty string when count > 0.", nameof(elementAddress));

            var slot = GetOrCreateTableSlot(referenceName);

            // A rebuild is unconditional — see the class comment on _tableSlots. This
            // cancels the prior CTS, releases the prefab handle, destroys pooled
            // instances, and restores the layout group's authored padding.
            slot.TearDown();

            slot.Count = count;
            slot.Adapter = adapter;
            slot.ElementAddress = elementAddress;
            slot.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (count == 0)
            {
                // Empty table: nothing to load, nothing to bind. The slot is now in
                // the same shape it would be after a fresh setup with zero elements.
                slot.CurrentTask = Task.CompletedTask;
                return Task.CompletedTask;
            }

            slot.CurrentTask = LoadAndPopulateTableAsync(slot, slot.Cts, slot.Cts.Token);
            return slot.CurrentTask;
        }

        // Resolve the referenced GameObject to a ScrollRect + Content + layout group,
        // caching the derived data on the slot so we don't re-lookup on every rebuild.
        // The layout group's authored padding is captured here (once, on first setup)
        // so TearDown can always restore it, even after many rebuilds mutate it.
        private TableSlot GetOrCreateTableSlot(string referenceName)
        {
            if (_tableSlots.TryGetValue(referenceName, out var slot))
                return slot;

            var go = GetReference(referenceName);
            var scrollRect = go.GetComponent<ScrollRect>();
            if (scrollRect == null)
                throw new MissingComponentException(
                    $"View on '{name}' has no ScrollRect on reference '{referenceName}'.");
            var content = scrollRect.content;
            if (content == null)
                throw new MissingReferenceException(
                    $"ScrollRect on reference '{referenceName}' has no content assigned.");

            var vLayout = content.GetComponent<VerticalLayoutGroup>();
            var hLayout = content.GetComponent<HorizontalLayoutGroup>();
            HorizontalOrVerticalLayoutGroup layoutGroup = vLayout != null ? (HorizontalOrVerticalLayoutGroup)vLayout : hLayout;
            if (layoutGroup == null)
                throw new MissingComponentException(
                    $"Content of ScrollRect '{referenceName}' has no HorizontalLayoutGroup or VerticalLayoutGroup.");

            var isVertical = vLayout != null;
            slot = new TableSlot
            {
                ReferenceName = referenceName,
                ScrollRect = scrollRect,
                Content = content,
                LayoutGroup = layoutGroup,
                IsVertical = isVertical,
                OriginalLeadingPad = isVertical ? layoutGroup.padding.top : layoutGroup.padding.left,
                OriginalTrailingPad = isVertical ? layoutGroup.padding.bottom : layoutGroup.padding.right,
                OriginalContentSize = content.sizeDelta,
            };
            slot.OnScrollListener = slot.HandleScroll;

            _tableSlots[referenceName] = slot;
            return slot;
        }

        // Loads the element prefab, uses the first instance as a size probe (so the
        // measurement reflects whatever LayoutElement/ContentSizeFitter setup the
        // prefab uses under our layout group), subscribes to the ScrollRect, and
        // triggers the first visibility pass. Follows the same cancel/identity-check
        // pattern as LoadAndApplyAsync for images.
        private async Task LoadAndPopulateTableAsync(TableSlot slot, CancellationTokenSource myCts, CancellationToken token)
        {
            var handle = Addressables.LoadAssetAsync<GameObject>(slot.ElementAddress);
            var applied = false;
            try
            {
                await handle.Task;

                if (slot.Cts != myCts || token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    return;
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw handle.OperationException ?? new Exception(
                        $"Failed to load prefab '{slot.ElementAddress}'.");

                if (slot.Content == null) return;

                var probe = Instantiate(handle.Result, slot.Content);
                var probeRt = (RectTransform)probe.transform;
                LayoutRebuilder.ForceRebuildLayoutImmediate(probeRt);
                slot.ElementSize = slot.IsVertical ? probeRt.rect.height : probeRt.rect.width;
                if (slot.ElementSize <= 0f)
                {
                    Destroy(probe);
                    throw new InvalidOperationException(
                        $"Prefab '{slot.ElementAddress}' has non-positive size on the scroll axis " +
                        $"({(slot.IsVertical ? "height" : "width")}={slot.ElementSize}). " +
                        "Pooled tables require fixed-size elements.");
                }

                var probeView = probe.GetComponent<View>();
                if (probeView == null)
                {
                    Destroy(probe);
                    throw new MissingComponentException(
                        $"Prefab '{slot.ElementAddress}' has no View component at its root.");
                }

                slot.Spacing = slot.LayoutGroup.spacing;
                slot.PrefabHandle = handle;
                applied = true;

                // Probe becomes the first pooled element — no wasted instantiation.
                probe.SetActive(false);
                slot.FreePool.Push(new PooledElement { Go = probe, View = probeView, Index = -1 });

                slot.ResizeContent();
                slot.ScrollRect.onValueChanged.AddListener(slot.OnScrollListener);

                slot.UpdateVisibleRange();
            }
            finally
            {
                if (!applied && handle.IsValid())
                    Addressables.Release(handle);
            }
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

        // Owns everything a pooled table needs at runtime. All lifetime-bearing state
        // (CTS, handles, spawned instances, scroll listener) is confined here so the
        // outer View just needs to iterate _tableSlots to release everything.
        private class TableSlot
        {
            // Resolved once in GetOrCreateTableSlot; the ScrollRect + Content pair is
            // stable across rebuilds so we don't re-query it.
            public string ReferenceName;
            public ScrollRect ScrollRect;
            public RectTransform Content;
            public HorizontalOrVerticalLayoutGroup LayoutGroup;
            public bool IsVertical;
            public int OriginalLeadingPad;
            public int OriginalTrailingPad;
            public Vector2 OriginalContentSize;
            public UnityAction<Vector2> OnScrollListener;

            // Per-configuration state. Reset by TearDown before each SetTable takes
            // effect, so a stale Adapter can never fire against a new prefab.
            public string ElementAddress;
            public int Count;
            public TableAdapter Adapter;
            public CancellationTokenSource Cts;
            public AsyncOperationHandle<GameObject> PrefabHandle;
            public float ElementSize;
            public float Spacing;
            public Task CurrentTask;

            public readonly Dictionary<int, PooledElement> ActiveByIndex = new();
            public readonly Stack<PooledElement> FreePool = new();
            public int FirstActiveIndex = -1;
            public int LastActiveIndex = -1;

            private float Stride => ElementSize + Spacing;

            // Cached method reference so AddListener/RemoveListener see the same
            // delegate instance across calls — belt-and-suspenders vs. Delegate.Equals.
            public void HandleScroll(Vector2 _) => UpdateVisibleRange();

            // Total virtual content size along the scroll axis, including the authored
            // leading/trailing padding. Only the actual instances plus their padding
            // fake this total; setting sizeDelta directly ensures the ScrollRect knows
            // the full scrollable range up front.
            public void ResizeContent()
            {
                if (Content == null) return;
                var total = Count <= 0
                    ? (IsVertical ? OriginalContentSize.y : OriginalContentSize.x)
                    : OriginalLeadingPad + Count * ElementSize + Mathf.Max(0, Count - 1) * Spacing + OriginalTrailingPad;
                var size = Content.sizeDelta;
                if (IsVertical) size.y = total; else size.x = total;
                Content.sizeDelta = size;
            }

            // Recomputes which indices should be bound, detaches anything that left
            // the visible range, attaches anything new that entered, reorders siblings
            // so the layout group renders them in index order, and adjusts the
            // leading/trailing padding to reserve space for the still-hidden virtual
            // elements. No-op when the visible range hasn't changed.
            public void UpdateVisibleRange()
            {
                if (Count <= 0 || ElementSize <= 0f || ScrollRect == null || ScrollRect.viewport == null) return;

                var viewportSize = IsVertical
                    ? ScrollRect.viewport.rect.height
                    : ScrollRect.viewport.rect.width;
                var totalSize = OriginalLeadingPad + Count * ElementSize
                                + Mathf.Max(0, Count - 1) * Spacing + OriginalTrailingPad;

                // Use the normalized position so we don't have to reason about pivot /
                // anchor variations. Vertical is inverted in Unity — position 1 means
                // the top is visible.
                float offset;
                if (totalSize <= viewportSize)
                {
                    offset = 0f;
                }
                else if (IsVertical)
                {
                    offset = (totalSize - viewportSize) * (1f - ScrollRect.verticalNormalizedPosition);
                }
                else
                {
                    offset = (totalSize - viewportSize) * ScrollRect.horizontalNormalizedPosition;
                }

                var stride = Stride;
                var adjustedOffset = offset - OriginalLeadingPad;

                var first = Mathf.FloorToInt(adjustedOffset / stride);
                var last = Mathf.CeilToInt((adjustedOffset + viewportSize) / stride) - 1;
                first = Mathf.Clamp(first - TableOverscan, 0, Count - 1);
                last = Mathf.Clamp(last + TableOverscan, 0, Count - 1);

                if (first == FirstActiveIndex && last == LastActiveIndex) return;

                // Snapshot the current range's active indices before mutating the
                // dictionary — we can't iterate ActiveByIndex.Values while detaching.
                if (FirstActiveIndex >= 0)
                {
                    for (int i = FirstActiveIndex; i <= LastActiveIndex; i++)
                    {
                        if (i >= first && i <= last) continue;
                        if (ActiveByIndex.TryGetValue(i, out var el))
                            Detach(el);
                    }
                }

                for (int i = first; i <= last; i++)
                {
                    if (!ActiveByIndex.ContainsKey(i))
                        Attach(i);
                }

                FirstActiveIndex = first;
                LastActiveIndex = last;

                // LayoutGroup lays out by sibling order; keep active elements packed
                // at the front of Content in index order. Sibling positions >= K
                // (where K is the number of active elements) are unused by us.
                for (int i = first; i <= last; i++)
                {
                    if (ActiveByIndex.TryGetValue(i, out var el))
                        el.Go.transform.SetSiblingIndex(i - first);
                }

                // Reserve virtual space for hidden indices via layout-group padding.
                // Each hidden index contributes one full stride (element + spacing).
                var leadingExtra = Mathf.RoundToInt(first * stride);
                var trailingExtra = Mathf.RoundToInt((Count - 1 - last) * stride);
                if (IsVertical)
                {
                    LayoutGroup.padding.top = OriginalLeadingPad + leadingExtra;
                    LayoutGroup.padding.bottom = OriginalTrailingPad + trailingExtra;
                }
                else
                {
                    LayoutGroup.padding.left = OriginalLeadingPad + leadingExtra;
                    LayoutGroup.padding.right = OriginalTrailingPad + trailingExtra;
                }
                LayoutRebuilder.MarkLayoutForRebuild(Content);
            }

            // Pull from the free pool if we can — otherwise instantiate. A fresh CTS
            // is created per binding so the adapter's OnAttach receives a token that's
            // scoped exactly to this element's active lifetime (not the whole table's).
            private void Attach(int index)
            {
                PooledElement el;
                if (FreePool.Count > 0)
                {
                    el = FreePool.Pop();
                }
                else
                {
                    var go = UnityEngine.Object.Instantiate(PrefabHandle.Result, Content);
                    var view = go.GetComponent<View>();
                    el = new PooledElement { Go = go, View = view };
                }

                el.Index = index;
                el.Cts = CancellationTokenSource.CreateLinkedTokenSource(Cts.Token);
                el.Go.SetActive(true);
                ActiveByIndex[index] = el;

                try
                {
                    Adapter.OnAttach(el.View, index, el.Cts.Token);
                }
                catch (Exception e)
                {
                    // Adapter is user code — a throw shouldn't leave the pool in a
                    // half-bound state. Cancel, log, and return the element to the
                    // free pool so the next scroll pass can retry with a fresh CTS.
                    Debug.LogException(e);
                    el.Cts.Cancel();
                    el.Cts.Dispose();
                    el.Cts = null;
                    ActiveByIndex.Remove(index);
                    el.Go.SetActive(false);
                    el.Index = -1;
                    FreePool.Push(el);
                }
            }

            // Cancel the element's CTS first — that lets any in-flight image loads on
            // the element's inner View observe cancellation and release their handles
            // before OnDetach runs (which, if the caller wants to do anything, can
            // rely on the token already being cancelled).
            private void Detach(PooledElement el)
            {
                var idx = el.Index;
                ActiveByIndex.Remove(idx);

                if (el.Cts != null)
                {
                    el.Cts.Cancel();
                    el.Cts.Dispose();
                    el.Cts = null;
                }

                try
                {
                    Adapter?.OnDetach?.Invoke(el.View, idx);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                el.Go.SetActive(false);
                el.Index = -1;
                FreePool.Push(el);
            }

            // Fully returns the slot to its pre-SetTable shape: no active bindings,
            // no pooled instances, no prefab loaded, authored padding restored. Safe
            // to call on a freshly-created slot (no-ops on unset state). Called both
            // by every SetTableAsync (before installing the new config) and by
            // View.OnDestroy.
            public void TearDown()
            {
                if (ScrollRect != null && OnScrollListener != null)
                    ScrollRect.onValueChanged.RemoveListener(OnScrollListener);

                foreach (var el in ActiveByIndex.Values)
                {
                    if (el.Cts != null)
                    {
                        el.Cts.Cancel();
                        el.Cts.Dispose();
                        el.Cts = null;
                    }
                    try { Adapter?.OnDetach?.Invoke(el.View, el.Index); }
                    catch (Exception e) { Debug.LogException(e); }
                    if (el.Go != null) UnityEngine.Object.Destroy(el.Go);
                }
                ActiveByIndex.Clear();

                while (FreePool.Count > 0)
                {
                    var el = FreePool.Pop();
                    if (el.Go != null) UnityEngine.Object.Destroy(el.Go);
                }

                if (Cts != null)
                {
                    Cts.Cancel();
                    Cts.Dispose();
                    Cts = null;
                }

                if (PrefabHandle.IsValid())
                {
                    Addressables.Release(PrefabHandle);
                    PrefabHandle = default;
                }

                if (LayoutGroup != null)
                {
                    if (IsVertical)
                    {
                        LayoutGroup.padding.top = OriginalLeadingPad;
                        LayoutGroup.padding.bottom = OriginalTrailingPad;
                    }
                    else
                    {
                        LayoutGroup.padding.left = OriginalLeadingPad;
                        LayoutGroup.padding.right = OriginalTrailingPad;
                    }
                    LayoutRebuilder.MarkLayoutForRebuild(Content);
                }
                if (Content != null)
                    Content.sizeDelta = OriginalContentSize;

                FirstActiveIndex = -1;
                LastActiveIndex = -1;
                CurrentTask = null;
                Adapter = null;
                Count = 0;
                ElementAddress = null;
                ElementSize = 0f;
                Spacing = 0f;
            }
        }

        private class PooledElement
        {
            public GameObject Go;
            public View View;
            public int Index = -1;
            public CancellationTokenSource Cts;
        }
    }
}
