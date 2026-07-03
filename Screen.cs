using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Heartwood
{
    public abstract class Screen
    {
        protected abstract string Address { get; }

        public bool IsPrepared { get; private set; }
        public virtual bool IsModal => false;
        public GameObject Root { get; private set; }

        // Linked to Core.Instance.Token, created in PrepareAsync and cancelled+disposed
        // in Dispose. Not exposed as a property — the public entry points below snapshot
        // the token before forwarding, so subclasses receive it as a parameter and can
        // hold a local copy across awaits without worrying about _cts going null.
        private CancellationTokenSource _cts;

        private AsyncOperationHandle<GameObject> _handle;

        // Load the prefab via Addressables and run the subclass's SetupAsync. Idempotent:
        // a Screen can be Dispose'd and re-Prepared (that's the whole point of the
        // hide-by-unload model — covered screens release their assets and reload on reveal).
        public async Task PrepareAsync(Transform parent)
        {
            if (IsPrepared) return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(Core.Instance.Token);
            var token = _cts.Token;

            // Addressables.InstantiateAsync doesn't accept a CancellationToken — if we
            // get cancelled mid-load, we let the load finish, then release the instance.
            _handle = Addressables.InstantiateAsync(Address, parent);
            await _handle.Task;

            if (_handle.Status != AsyncOperationStatus.Succeeded)
            {
                DisposeCts();
                throw _handle.OperationException ?? new Exception($"Failed to load {Address}");
            }

            Root = _handle.Result;
            Root.SetActive(false);

            if (token.IsCancellationRequested)
            {
                ReleaseHandle();
                DisposeCts();
                token.ThrowIfCancellationRequested();
            }

            try
            {
                await SetupAsync(token);
            }
            catch
            {
                ReleaseHandle();
                DisposeCts();
                throw;
            }

            IsPrepared = true;
        }

        protected virtual Task SetupAsync(CancellationToken ct) => Task.CompletedTask;

        // Public entry points below snapshot _cts.Token before forwarding to the virtual
        // overload. Subclasses override the CT-taking overload and hold a local copy of
        // the token — that way the token stays observable across awaits even if the
        // screen is Disposed mid-flight (which nulls _cts).

        // Optional intro animation, played by ScreenManager after Push completes (i.e.,
        // after PrepareAsync and Show). Default is a no-op. Not played when the screen
        // is revealed by a Pop.
        public Task IntroAsync()
        {
            if (_cts == null)
                throw new InvalidOperationException(
                    $"{GetType().Name}.IntroAsync called while not prepared.");
            return IntroAsync(_cts.Token);
        }

        protected virtual Task IntroAsync(CancellationToken ct) => Task.CompletedTask;

        // Optional outro animation, played by ScreenManager during Pop before Dispose.
        // Default is a no-op. Not played when the screen is hidden (unloaded) by another
        // screen being pushed on top.
        public Task OutroAsync()
        {
            if (_cts == null)
                throw new InvalidOperationException(
                    $"{GetType().Name}.OutroAsync called while not prepared.");
            return OutroAsync(_cts.Token);
        }

        protected virtual Task OutroAsync(CancellationToken ct) => Task.CompletedTask;

        public void Show()
        {
            if (Root != null) Root.SetActive(true);
        }

        public void Hide()
        {
            Root?.SetActive(false);
        }

        // Release Addressables handle and destroy the instantiated GameObject. After
        // Dispose, IsPrepared is false and the Screen object can be PrepareAsync'd again
        // (reverting it to a loaded state).
        public virtual void Dispose()
        {
            ReleaseHandle();
            DisposeCts();
        }

        private void ReleaseHandle()
        {
            if (_handle.IsValid())
                Addressables.ReleaseInstance(_handle);
            Root = null;
            IsPrepared = false;
        }

        private void DisposeCts()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }
}
