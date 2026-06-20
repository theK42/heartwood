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

        private AsyncOperationHandle<GameObject> _handle;

        // Load the prefab via Addressables and run the subclass's SetupAsync. Idempotent:
        // a Screen can be Dispose'd and re-Prepared (that's the whole point of the
        // hide-by-unload model — covered screens release their assets and reload on reveal).
        public async Task PrepareAsync(Transform parent, CancellationToken ct)
        {
            if (IsPrepared) return;

            // Addressables.InstantiateAsync doesn't accept a CancellationToken — if we
            // get cancelled mid-load, we let the load finish, then release the instance.
            _handle = Addressables.InstantiateAsync(Address, parent);
            await _handle.Task;

            if (_handle.Status != AsyncOperationStatus.Succeeded)
                throw _handle.OperationException ?? new Exception($"Failed to load {Address}");

            Root = _handle.Result;
            Root.SetActive(false);

            if (ct.IsCancellationRequested)
            {
                ReleaseHandle();
                ct.ThrowIfCancellationRequested();
            }

            try
            {
                await SetupAsync(ct);
            }
            catch
            {
                ReleaseHandle();
                throw;
            }

            IsPrepared = true;
        }

        protected virtual Task SetupAsync(CancellationToken ct) => Task.CompletedTask;

        // Optional intro animation, played by ScreenManager after Push completes (i.e.,
        // after PrepareAsync and Show). Default is a no-op. Not played when the screen
        // is revealed by a Pop.
        public virtual Task IntroAsync(CancellationToken ct) => Task.CompletedTask;

        // Optional outro animation, played by ScreenManager during Pop before Dispose.
        // Default is a no-op. Not played when the screen is hidden (unloaded) by another
        // screen being pushed on top.
        public virtual Task OutroAsync(CancellationToken ct) => Task.CompletedTask;

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
        public virtual void Dispose() => ReleaseHandle();

        private void ReleaseHandle()
        {
            if (_handle.IsValid())
                Addressables.ReleaseInstance(_handle);
            Root = null;
            IsPrepared = false;
        }
    }
}
