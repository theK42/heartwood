using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Heartwood.UI
{
    // Global inline TMP sprite icons, loaded by Addressables address.
    //
    // TMP resolves <sprite name="..."> against a text component's own spriteAsset, or —
    // when it has none — TMP_Settings.defaultSpriteAsset, then recursively through that
    // asset's fallbackSpriteAssets. We lean on the last hop: each registered TMP_SpriteAsset
    // is appended to the default asset's fallback list, so ANY TMP_Text anywhere can write
    // <sprite name="energy"> with zero per-component wiring.
    //
    // Addresses must point at TMP_SpriteAsset assets authored in the editor (Asset > Create >
    // Text > Sprite Asset, or the Sprite Importer), NOT at raw sprites or textures. Building
    // a sprite asset from a Sprite at runtime is a dead end — the atlas/material/lookup
    // wiring TMP expects only comes out of the import pipeline.
    //
    // The tag name is NOT ours to choose: it's the sprite character name baked into the
    // asset, so registering an asset publishes every icon inside it under whatever the
    // importer named them. Hence the address is the only key here — the registry's job is
    // load/hold/unhook, not naming. An icon that sits high or low against text is nudged
    // per-use with the tag's voffset/size attributes; there's nothing to tune on this side.
    //
    // Lifetime is app-global: handles are held until Unregister/Clear, not tied to any
    // View. We append to the default asset's fallback list at runtime and strip our entries
    // back out on Application.quitting (which also fires on play-mode exit in the editor) so
    // the shipped TMP Settings asset isn't left dirtied with dead references.
    public static class IconRegistry
    {
        private static readonly Dictionary<string, IconEntry> _entries = new();

        // The sprite asset we appended our fallbacks to — cached so teardown removes from
        // exactly the asset we mutated, even if the default were somehow swapped.
        private static TMP_SpriteAsset _hookedDefault;
        private static bool _initialized;

        // Reset static state at the start of every play session. Matters only when Enter
        // Play Mode has domain reload disabled: the fields below would otherwise still point
        // at the previous session's now-destroyed objects. Destroying isn't needed here (the
        // objects are already gone with the old scene) — just drop the stale references.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _entries.Clear();
            _hookedDefault = null;
            _initialized = false;
        }

        // Idempotent. Callable explicitly at startup, but every Register path also ensures
        // it, so calling it by hand is optional. Anchors our fallback chain onto the project
        // default sprite asset and arms the quitting-time teardown.
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _hookedDefault = TMP_Settings.defaultSpriteAsset;
            if (_hookedDefault == null)
            {
                // No global anchor exists, so <sprite name="..."> can't resolve without a
                // per-component spriteAsset. Set one in TMP Settings to enable global icons.
                Debug.LogError(
                    "IconRegistry: TMP_Settings has no default sprite asset; global icons " +
                    "won't resolve. Assign a Default Sprite Asset in TMP Settings.");
                return;
            }

            _hookedDefault.fallbackSpriteAssets ??= new List<TMP_SpriteAsset>();

            // Drop any dead entries a prior domain-reload-disabled session may have left in
            // the shared list before we start adding ours.
            _hookedDefault.fallbackSpriteAssets.RemoveAll(a => a == null);

            Application.quitting += Shutdown;
        }

        // Fire-and-forget entry point. Chains to Core.Instance.Token so a Core-level cancel
        // aborts the load; use RegisterAsync when you want to await a startup batch.
        public static void Register(string address)
            => Core.FireAndForget(RegisterAsync(address, Core.Instance.Token));

        // Loads the TMP_SpriteAsset at `address` and publishes its sprites globally.
        // Registering an address twice is a no-op that returns the existing load; a faulted
        // or cancelled prior load retries. Returns the load Task so a startup batch can be
        // awaited together:
        //   await Task.WhenAll(
        //       IconRegistry.RegisterAsync("Icons/Energy.asset", ct),
        //       IconRegistry.RegisterAsync("Icons/Coin.asset",   ct));
        public static Task RegisterAsync(string address, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(address))
                throw new ArgumentException("Address must be a non-empty string.", nameof(address));

            Initialize();

            if (!_entries.TryGetValue(address, out var entry))
            {
                entry = new IconEntry();
                _entries[address] = entry;
            }
            else if (entry.CurrentTask != null &&
                     !entry.CurrentTask.IsFaulted && !entry.CurrentTask.IsCanceled)
            {
                Debug.LogWarning(
                    $"IconRegistry: '{address}' is already registered or loading.");
                return entry.CurrentTask;
            }

            entry.CancelAndDisposeCts();
            entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            entry.CurrentTask = LoadAndRegisterAsync(entry, address, entry.Cts, entry.Cts.Token);
            return entry.CurrentTask;
        }

        // Removes an icon asset: cancels any in-flight load, pulls it out of the fallback
        // chain, and releases the Addressables handle. Text already rendered with its icons
        // keeps its glyphs until it next re-parses. Returns false if unknown.
        public static bool Unregister(string address)
        {
            if (!_entries.TryGetValue(address, out var entry)) return false;
            entry.CancelAndDisposeCts();
            RemoveAssetFromChain(entry);
            entry.ReleaseHandle();
            _entries.Remove(address);
            return true;
        }

        public static bool IsRegistered(string address) => _entries.ContainsKey(address);

        // Drops every registered asset but leaves the registry usable (fallback anchor and
        // quitting hook stay in place for future Registers).
        public static void Clear()
        {
            foreach (var entry in _entries.Values)
            {
                entry.CancelAndDisposeCts();
                RemoveAssetFromChain(entry);
                entry.ReleaseHandle();
            }
            _entries.Clear();
        }

        // Full teardown. Runs on Application.quitting (and editor play-mode exit) to make
        // sure our runtime-added fallbacks don't persist onto the shared TMP Settings asset.
        private static void Shutdown()
        {
            Clear();
            Application.quitting -= Shutdown;
            _hookedDefault = null;
            _initialized = false;
        }

        private static async Task LoadAndRegisterAsync(IconEntry entry, string address,
            CancellationTokenSource myCts, CancellationToken token)
        {
            // Addressables.LoadAssetAsync doesn't take a CancellationToken — on a mid-load
            // cancel we let it finish and release the just-loaded handle in the finally.
            var handle = Addressables.LoadAssetAsync<TMP_SpriteAsset>(address);
            var applied = false;
            try
            {
                await handle.Task;

                // Superseded by a retry on this address, or cancelled. A retry always
                // cancels myCts, so the identity check is belt-and-suspenders.
                if (entry.Cts != myCts || token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    return;
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw handle.OperationException ?? new Exception(
                        $"Failed to load sprite asset '{address}'.");

                var asset = handle.Result;
                if (asset.spriteCharacterTable.Count == 0)
                    Debug.LogWarning(
                        $"IconRegistry: sprite asset '{address}' contains no sprites, so it " +
                        "publishes no icons.");

                // An entry never swaps assets (the address IS the key), so there's nothing
                // prior to retire — a retry only ever follows a load that applied nothing.
                entry.Handle = handle;
                entry.Asset = asset;
                applied = true;

                _hookedDefault?.fallbackSpriteAssets.Add(asset);
            }
            finally
            {
                if (!applied && handle.IsValid())
                    Addressables.Release(handle);
            }
        }

        // Unhooks the entry's asset from the fallback chain. The asset itself belongs to
        // Addressables, not to us, so it's never destroyed here — ReleaseHandle is what
        // actually gives it up, and it must run after this so the chain never holds a
        // reference to an unloaded asset.
        private static void RemoveAssetFromChain(IconEntry entry)
        {
            if (entry.Asset == null) return;
            _hookedDefault?.fallbackSpriteAssets.Remove(entry.Asset);
            entry.Asset = null;
        }

        private class IconEntry
        {
            public CancellationTokenSource Cts;
            public AsyncOperationHandle<TMP_SpriteAsset> Handle;
            public TMP_SpriteAsset Asset;
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
