using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Heartwood
{
    public class ScreenManager
    {
        private static readonly Lazy<ScreenManager> _instance = new(() => new ScreenManager());
        public static ScreenManager Instance => _instance.Value;

        private Canvas RootCanvas { get; }

        private readonly List<Screen> _stack = new List<Screen>();
        private LoadingSpinner _spinner;

        private ScreenManager()
        {
            // Single root canvas; screens are sibling children. Screen prefabs typically
            // bring their own nested Canvas for per-screen sorting/perf — the manager
            // doesn't need to know.
            var canvasGo = new GameObject("ScreenCanvas");
            RootCanvas = canvasGo.AddComponent<Canvas>();
            RootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;

            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            Object.DontDestroyOnLoad(canvasGo);
        }

        // Load + setup a Screen without putting it on the stack. Caller holds the Screen
        // and later calls PushScreen/PushModal — that push sees IsPrepared and skips the
        // spinner-blocked load step.
        public Task PrepareAsync(Screen s, CancellationToken ct)
            => s.PrepareAsync(RootCanvas.transform, ct);

        public void Prepare(Screen s) => Core.FireAndForget(PrepareAsync(s, Core.Instance.Token));

        public async Task PushScreenAsync(Screen s, CancellationToken ct)
        {
            // Snapshot what's currently loaded — these get unloaded after the intro plays.
            // Since the new top is a Screen, the new visible set is just s; everything
            // currently in the stack falls below the new topmost Screen and must be released.
            var toUnload = new List<Screen>();
            foreach (var existing in _stack)
                if (existing.IsPrepared) toUnload.Add(existing);

            await PrepareWithSpinnerAsync(s, ct);

            s.Root.transform.SetAsLastSibling();
            s.Show();
            _stack.Add(s);

            // Intro plays while previously-visible screens are still up. After the intro
            // completes, the screens underneath are released.
            await s.IntroAsync(ct);

            foreach (var p in toUnload) p.Dispose();
        }

        public void PushScreen(Screen s) => Core.FireAndForget(PushScreenAsync(s, Core.Instance.Token));

        public async Task PushModalAsync(Modal m, CancellationToken ct)
        {
            await PrepareWithSpinnerAsync(m, ct);

            m.Root.transform.SetAsLastSibling();
            m.Show();
            _stack.Add(m);

            await m.IntroAsync(ct);

            // Modals don't hide anything — visible set just gains m. No unloads.
        }

        public void PushModal(Modal m) => Core.FireAndForget(PushModalAsync(m, Core.Instance.Token));

        public async Task PopAsync(CancellationToken ct)
        {
            if (_stack.Count == 0) return;

            var top = _stack[_stack.Count - 1];
            var revealSet = ComputeRevealSet(_stack.Count - 2);

            await PrepareRevealSetWithSpinnerAsync(revealSet, ct);
            PositionForReveal(top, revealSet);

            await top.OutroAsync(ct);

            top.Dispose();
            _stack.RemoveAt(_stack.Count - 1);
        }

        public void Pop() => Core.FireAndForget(PopAsync(Core.Instance.Token));

        // Pops the stack down until `target` is the top. Only the current top plays its
        // outro; intermediate items between target and top are removed silently (and
        // unloaded if somehow still loaded — they shouldn't be, by the visibility rule).
        public async Task PopToAsync(Screen target, CancellationToken ct)
        {
            var targetIdx = _stack.IndexOf(target);
            if (targetIdx < 0)
                throw new InvalidOperationException(
                    $"PopTo target {target.GetType().Name} is not on the stack");

            if (targetIdx == _stack.Count - 1) return; // already on top

            var top = _stack[_stack.Count - 1];

            // Invariant: only the current top is visible. Equivalent to "top is a Screen"
            // — a Modal on top means everything beneath it is also visible, which makes
            // the silent-drop of intermediates ambiguous.
            if (top.IsModal)
                throw new InvalidOperationException(
                    $"PopTo requires the current top to be the only visible item, but the top " +
                    $"({top.GetType().Name}) is a Modal; screens beneath it are also visible. " +
                    "Pop the Modals first.");

            var revealSet = ComputeRevealSet(targetIdx);

            await PrepareRevealSetWithSpinnerAsync(revealSet, ct);
            PositionForReveal(top, revealSet);

            await top.OutroAsync(ct);

            top.Dispose();
            _stack.RemoveAt(_stack.Count - 1);

            // Silently drop the intermediates between target and the original top.
            while (_stack.Count - 1 > targetIdx)
            {
                var middle = _stack[_stack.Count - 1];
                if (middle.IsPrepared) middle.Dispose();
                _stack.RemoveAt(_stack.Count - 1);
            }
        }

        public void PopTo(Screen target) => Core.FireAndForget(PopToAsync(target, Core.Instance.Token));

        // Inserts `toInsert` just below `anchor` in the stack. No animation, no load —
        // toInsert starts unloaded. Throws if the insert would change the visibility set
        // (e.g., putting a Screen above the current topmost). Use Push if you want to
        // make something visible.
        public void InsertBefore(Screen toInsert, Screen anchor)
        {
            var anchorIdx = _stack.IndexOf(anchor);
            if (anchorIdx < 0)
                throw new InvalidOperationException(
                    $"InsertBefore anchor {anchor.GetType().Name} is not on the stack");

            // The insert changes the visibility set iff toInsert itself ends up visible
            // after the insertion. (If it doesn't, the existing topmost Screen identity
            // is unchanged and every existing item keeps the same visible/hidden state.)
            var simulated = new List<Screen>(_stack);
            simulated.Insert(anchorIdx, toInsert);
            if (IsVisibleInStack(simulated, anchorIdx))
                throw new InvalidOperationException(
                    $"InsertBefore would put {toInsert.GetType().Name} in the visible set. " +
                    "Use PushScreen/PushModal if you mean to make it visible.");

            _stack.Insert(anchorIdx, toInsert);
        }

        // Removes `target` from the stack. No animation. If `target` was loaded, its
        // assets are released. Intended for screens that are already hidden (unloaded)
        // by the visibility rule — removing the current visible top is undefined here;
        // use Pop for that.
        public Task RemoveAsync(Screen target, CancellationToken ct)
        {
            var idx = _stack.IndexOf(target);
            if (idx < 0)
                throw new InvalidOperationException(
                    $"Remove target {target.GetType().Name} is not on the stack");

            if (target.IsPrepared) target.Dispose();
            _stack.RemoveAt(idx);
            return Task.CompletedTask;
        }

        public void Remove(Screen target) => Core.FireAndForget(RemoveAsync(target, Core.Instance.Token));

        // Removes the inclusive range [bottom .. top] from the stack. Same constraints
        // as Remove: intended for screens that are already hidden; doesn't animate.
        public Task RemoveBetweenAsync(Screen top, Screen bottom, CancellationToken ct)
        {
            var topIdx = _stack.IndexOf(top);
            var bottomIdx = _stack.IndexOf(bottom);

            if (topIdx < 0)
                throw new InvalidOperationException(
                    $"RemoveBetween top {top.GetType().Name} is not on the stack");
            if (bottomIdx < 0)
                throw new InvalidOperationException(
                    $"RemoveBetween bottom {bottom.GetType().Name} is not on the stack");
            if (bottomIdx > topIdx)
                throw new InvalidOperationException(
                    "RemoveBetween: bottom is above top in the stack");

            for (var i = topIdx; i >= bottomIdx; i--)
            {
                if (_stack[i].IsPrepared) _stack[i].Dispose();
                _stack.RemoveAt(i);
            }

            return Task.CompletedTask;
        }

        public void RemoveBetween(Screen top, Screen bottom)
            => Core.FireAndForget(RemoveBetweenAsync(top, bottom, Core.Instance.Token));

        // Computes which screens should be visible (loaded) when the screen at `newTopIdx`
        // is the new top of the stack. Range = (next-topmost-Screen-at-or-below-newTopIdx)
        // through newTopIdx inclusive. Empty list if newTopIdx < 0.
        private List<Screen> ComputeRevealSet(int newTopIdx)
        {
            var result = new List<Screen>();
            if (newTopIdx < 0) return result;

            var topmostScreenIdx = -1;
            for (var i = newTopIdx; i >= 0; i--)
            {
                if (!_stack[i].IsModal) { topmostScreenIdx = i; break; }
            }

            // No Screen at or below newTopIdx — reveal everything from the bottom up.
            var rangeStart = topmostScreenIdx >= 0 ? topmostScreenIdx : 0;
            for (var i = rangeStart; i <= newTopIdx; i++)
                result.Add(_stack[i]);
            return result;
        }

        // True if the item at `idx` in the given stack is visible under the rule:
        // topmost Screen + everything above = visible; empty-of-Screens stack = all visible.
        private static bool IsVisibleInStack(IList<Screen> stack, int idx)
        {
            if (idx < 0 || idx >= stack.Count) return false;

            var topmost = -1;
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                if (!stack[i].IsModal) { topmost = i; break; }
            }

            return topmost < 0 || idx >= topmost;
        }

        // Prepares everything in revealSet that isn't already prepared, showing the
        // spinner if any load is needed.
        private async Task PrepareRevealSetWithSpinnerAsync(List<Screen> revealSet, CancellationToken ct)
        {
            var anyNeedsLoad = false;
            foreach (var r in revealSet)
                if (!r.IsPrepared) { anyNeedsLoad = true; break; }

            if (anyNeedsLoad) await ShowSpinnerAsync(ct);
            try
            {
                foreach (var r in revealSet)
                    if (!r.IsPrepared)
                        await r.PrepareAsync(RootCanvas.transform, ct);
            }
            finally
            {
                if (anyNeedsLoad) HideSpinner();
            }
        }

        // After preparing reveals, their Roots were instantiated as last siblings, which
        // would put them above the popped top. Restoring the popped top to last sibling
        // arranges the hierarchy as [..., reveal[0], reveal[1], ..., poppedTop] — reveals
        // render behind the popped top during its outro.
        private void PositionForReveal(Screen poppedTop, List<Screen> revealSet)
        {
            poppedTop.Root.transform.SetAsLastSibling();
            foreach (var r in revealSet) r.Show();
        }

        private async Task PrepareWithSpinnerAsync(Screen s, CancellationToken ct)
        {
            var needsLoad = !s.IsPrepared;
            if (needsLoad) await ShowSpinnerAsync(ct);
            try
            {
                await s.PrepareAsync(RootCanvas.transform, ct);
            }
            finally
            {
                if (needsLoad) HideSpinner();
            }
        }

        private async Task ShowSpinnerAsync(CancellationToken ct)
        {
            if (_spinner == null)
                _spinner = Core.Instance.Game.CreateLoadingSpinner();

            // Game opted out of a spinner — silently skip.
            if (_spinner == null) return;

            if (!_spinner.IsPrepared)
                await _spinner.PrepareAsync(RootCanvas.transform, ct);

            _spinner.Root.transform.SetAsLastSibling();
            _spinner.Show();
        }

        private void HideSpinner() => _spinner?.Hide();
    }
}
