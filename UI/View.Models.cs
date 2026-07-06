using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace Heartwood.UI
{
    public partial class View
    {
        // Same lazy-per-reference pattern as _imageSlots, but for 3D models rendered to
        // a texture and shown through a RawImage. One slot owns the off-screen camera,
        // the instantiated model, the render texture, the CTS chain, and the Addressables
        // handle — all released either when SetModel is called again on the same
        // reference or in CleanupModels. Models and images share the reference namespace:
        // taking one over a reference cancels the other (see CancelImageSlot / the call
        // to CancelModelSlot from SetImageAsync) so the two never drive one target at once.
        private readonly Dictionary<string, ModelSlot> _modelSlots = new();

        // The off-screen models live under one auto-created scene-root object. Shared
        // across every View; each slot destroys only its own camera subtree on teardown.
        private static GameObject _modelsRoot;

        // NameToLayer results are cached once (they never change at runtime). -2 is the
        // "not yet looked up" sentinel; -1 is Unity's "layer does not exist" result.
        private static int _uiModelLayer = -2;
        private static int _doNotRenderLayer = -2;

        // Bumped per built camera purely to fan the off-screen cameras out along X so
        // they're distinguishable in the Scene view; layer culling — not spatial
        // separation — is what actually keeps them from seeing each other.
        private static int _modelCameraSerial;

        // Distance in front of the camera at which an orthographic framed model's box
        // center is placed. Orthographic, so this only affects clipping, not size — kept
        // comfortably inside the near/far planes set in BuildModel. Also the fallback
        // distance when there are no bounds to fit.
        private const float ModelDepth = 100f;

        // Vertical field of view for the (default) perspective camera. Moderately long to
        // keep perspective distortion gentle on centered models.
        private const float PerspectiveFieldOfView = 30f;

        // Orthographic half-height used when AutoFit is off (the caller sizes the model
        // via ModelScale / PositionOffset instead of a fitted zoom).
        private const float DefaultOrthographicSize = 1f;

        // Default orientation: turned 180° so the model's front faces the camera.
        private static readonly Quaternion ModelFacing = Quaternion.Euler(0f, 180f, 0f);

        private static Transform ModelsRoot
        {
            // == null catches a destroyed object (Unity's overloaded null) as well as
            // first use, so a scene unload that took the root with it self-heals.
            get
            {
                if (_modelsRoot == null)
                    _modelsRoot = new GameObject("UIModels");
                return _modelsRoot.transform;
            }
        }

        // Called from OnDestroy in the main partial. Every model slot destroys its
        // camera subtree, releases its render texture and prefab handle, and cancels
        // its CTS.
        private void CleanupModels()
        {
            foreach (var slot in _modelSlots.Values)
                slot.TearDownInstance();
        }

        // Contributes this View's in-flight model loads to the shared WhenAllLoadsAsync
        // gather. See WhenAllLoadsAsync in the main partial for the cascade it feeds.
        private void CollectModelLoads(ref List<Task> tasks)
        {
            foreach (var slot in _modelSlots.Values)
                AddInFlightLoad(ref tasks, slot.CurrentTask);
        }

        // Fire-and-forget entry point. Chains to Core.Instance.Token so a Core-level
        // cancel aborts the load; the returned task is stored on the slot for tracking.
        public void SetModel(string referenceName, string address, ModelOptions options = null)
            => Core.FireAndForget(SetModelAsync(referenceName, address, options, Core.Instance.Token));

        // Loads a model prefab and renders it, animated, into the referenced RawImage.
        // Every call is a full rebuild — the prior camera/model/render texture are torn
        // down before the new configuration takes over — matching SetTable's semantics
        // rather than SetImage's in-place swap. Options may be null for all defaults.
        public Task SetModelAsync(string referenceName, string address, ModelOptions options, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(address))
                throw new ArgumentException("Address must be a non-empty string.", nameof(address));

            EnsureLayers();

            // Resolve (and validate the RawImage) before mutating anything, so a bad
            // reference throws without disturbing an image already on the same target.
            var slot = GetOrCreateModelSlot(referenceName);

            // A model taking over this reference cancels any in-flight image load and
            // hides its Image; SetImageAsync does the mirror via CancelModelSlot.
            CancelImageSlot(referenceName);

            // Full rebuild — tear down the prior camera/model/render texture first.
            slot.TearDownInstance();

            slot.Options = options ?? new ModelOptions();
            slot.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slot.CurrentTask = LoadAndSetupModelAsync(slot, address, slot.Cts, slot.Cts.Token);
            return slot.CurrentTask;
        }

        // Called from SetImageAsync in View.Images.cs when an image takes over a
        // reference, so a model never keeps rendering over an incoming image. Tears the
        // model instance down (which also clears + hides the RawImage) but keeps the
        // slot so a later SetModel reuses the captured original color.
        private void CancelModelSlot(string referenceName)
        {
            if (_modelSlots.TryGetValue(referenceName, out var slot))
                slot.TearDownInstance();
        }

        // Look up or create the slot for `referenceName`. Slot creation captures the
        // RawImage's original color and immediately hides it via alpha=0, so any
        // prefab-authored placeholder disappears the moment we commit to loading —
        // the color is restored once the first frame has been rendered.
        private ModelSlot GetOrCreateModelSlot(string referenceName)
        {
            if (_modelSlots.TryGetValue(referenceName, out var slot))
                return slot;

            var go = GetReference(referenceName);
            if (go == null)
                throw new KeyNotFoundException(
                    $"View on '{name}' has no reference '{referenceName}'.");
            var rawImage = go.GetComponent<RawImage>();
            if (rawImage == null)
                throw new MissingComponentException(
                    $"View on '{name}' has no RawImage component on reference '{referenceName}'. " +
                    "3D model targets render to a texture and require a RawImage (an Image renders " +
                    "a Sprite, which cannot wrap a RenderTexture).");

            slot = new ModelSlot
            {
                ReferenceName = referenceName,
                RawImage = rawImage,
                OriginalColor = rawImage.color,
            };

            var c = rawImage.color;
            c.a = 0f;
            rawImage.color = c;

            _modelSlots[referenceName] = slot;
            return slot;
        }

        private async Task LoadAndSetupModelAsync(ModelSlot slot, string address,
            CancellationTokenSource myCts, CancellationToken token)
        {
            // Addressables.LoadAssetAsync doesn't accept a CancellationToken — if we get
            // cancelled mid-load, we let it finish and release the just-loaded handle in
            // the finally block below. Mirrors LoadAndApplyAsync in View.Images.cs.
            var handle = Addressables.LoadAssetAsync<GameObject>(address);
            var applied = false;
            try
            {
                await handle.Task;

                // Reassigned/torn down by a newer SetModel on this slot? A reassign
                // cancels myCts, so the identity check is belt-and-suspenders.
                if (slot.Cts != myCts || token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    return;
                }

                if (handle.Status != AsyncOperationStatus.Succeeded)
                    throw handle.OperationException ?? new Exception(
                        $"Failed to load model '{address}'.");

                // View or the RawImage was destroyed between kickoff and completion.
                if (slot.RawImage == null) return;

                BuildModel(slot, handle.Result);
                ApplyLayout(slot);   // sizes the render texture and frames the camera
                RenderSlot(slot);    // first frame before we reveal, so no blank flash
                slot.NeedsRender = false;

                // Take ownership of the handle and reveal the (now populated) RawImage.
                slot.Handle = handle;
                applied = true;
                slot.RawImage.color = slot.OriginalColor;
            }
            finally
            {
                if (!applied && handle.IsValid())
                    Addressables.Release(handle);
            }
        }

        // Per-frame driver for every model on this View. Cheap early-out when the View
        // hosts no models. Detects layout-driven size changes on each RawImage and
        // rebuilds the render texture + framing, then redraws animated models (and any
        // model flagged NeedsRender by a rebuild).
        private void Update()
        {
            if (_modelSlots.Count == 0) return;

            foreach (var slot in _modelSlots.Values)
            {
                if (slot.Camera == null || slot.RawImage == null || slot.Options == null)
                    continue;

                var rt = (RectTransform)slot.RawImage.transform;
                var w = Mathf.RoundToInt(rt.rect.width);
                var h = Mathf.RoundToInt(rt.rect.height);
                if (w > 0 && h > 0 && (w != slot.LastWidth || h != slot.LastHeight))
                    ApplyLayout(slot);

                if (slot.Options.RenderEveryFrame || slot.NeedsRender)
                {
                    RenderSlot(slot);
                    slot.NeedsRender = false;
                }
            }
        }

        // Creates the off-screen camera and instantiates the model as its child. The
        // camera sees only the UIModelLayer and clears to the requested color; the model
        // starts on DoNotRenderLayer and is flipped to UIModelLayer only for the instant
        // of its own render (see RenderSlot).
        private void BuildModel(ModelSlot slot, GameObject prefab)
        {
            var camGo = new GameObject($"UIModelCamera:{name}:{slot.ReferenceName}");
            camGo.transform.SetParent(ModelsRoot, false);
            camGo.transform.localPosition = new Vector3(_modelCameraSerial++ * 50f, 0f, 0f);
            camGo.layer = _doNotRenderLayer;

            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;   // rendered manually via Camera.Render each frame
            cam.orthographic = slot.Options.Orthographic;
            cam.fieldOfView = PerspectiveFieldOfView;   // used only when perspective
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = slot.Options.ClearColor;
            cam.cullingMask = 1 << _uiModelLayer;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 10000f;
            cam.allowMSAA = false;

            var model = Instantiate(prefab, camGo.transform);
            slot.Camera = cam;
            slot.CameraGo = camGo;
            slot.Model = model;
            slot.BaseScale = model.transform.localScale;
            slot.ModelTransforms = model.GetComponentsInChildren<Transform>(true);
            SetHierarchyLayer(slot, _doNotRenderLayer);
        }

        // (Re)creates the render texture at the RawImage's exact pixel size — no square /
        // power-of-two rounding — and reframes the camera. Called at load and again from
        // Update whenever a layout change alters the RawImage's size.
        private void ApplyLayout(ModelSlot slot)
        {
            var rt = (RectTransform)slot.RawImage.transform;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

            var w = Mathf.Max(1, Mathf.RoundToInt(rt.rect.width));
            var h = Mathf.Max(1, Mathf.RoundToInt(rt.rect.height));

            if (slot.RenderTexture == null || slot.RenderTexture.width != w || slot.RenderTexture.height != h)
            {
                slot.ReleaseRenderTexture();
                var tex = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
                {
                    name = $"UIModelRT:{name}:{slot.ReferenceName}",
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                tex.Create();
                slot.RenderTexture = tex;
                slot.Camera.targetTexture = tex;
                slot.RawImage.texture = tex;
            }

            slot.LastWidth = w;
            slot.LastHeight = h;
            FitAndPosition(slot, w, h);
            slot.NeedsRender = true;
        }

        // Frames the model in the camera. Behavior forks on projection and AutoFit:
        //  * AutoFit off: no fitting/centering — the model just takes ModelScale and
        //    PositionOffset as given (orthographic falls back to a fixed default zoom).
        //  * Orthographic + AutoFit: zoom is fitted to the base (pre-ModelScale) padded
        //    box, then ModelScale is applied and the model re-centered — ModelScale reads
        //    as a size knob layered over the zoom fit.
        //  * Perspective + AutoFit: the model (already at ModelScale) is dollied to the
        //    distance at which its padded box fills the frustum and centered — perspective
        //    sizes via distance, not zoom.
        // PositionOffset is added to the fitted position in both AutoFit paths.
        private void FitAndPosition(ModelSlot slot, int w, int h)
        {
            var model = slot.Model.transform;
            var opt = slot.Options;
            var cam = slot.Camera;
            var aspect = (float)w / h;

            model.localRotation = opt.Rotation ?? ModelFacing;

            if (!opt.AutoFit)
            {
                if (opt.Orthographic) cam.orthographicSize = DefaultOrthographicSize;
                model.localScale = slot.BaseScale * opt.ModelScale;
                model.localPosition = opt.PositionOffset;
                return;
            }

            if (opt.Orthographic)
            {
                // Fit the zoom to the base box, then apply ModelScale and re-center.
                model.localScale = slot.BaseScale;
                model.localPosition = Vector3.zero;
                if (TryComputeCameraLocalBounds(slot, out var baseBounds))
                {
                    var ext = baseBounds.extents * Mathf.Max(0.0001f, opt.BoundingBoxScale);
                    cam.orthographicSize = Mathf.Max(Mathf.Max(ext.y, ext.x / aspect), 0.0001f);
                }
                else
                {
                    cam.orthographicSize = DefaultOrthographicSize;
                    WarnNoBounds(slot);
                }

                model.localScale = slot.BaseScale * opt.ModelScale;
                model.localPosition = Vector3.zero;
                var has = TryComputeCameraLocalBounds(slot, out var finalBounds);
                var c = has ? finalBounds.center : Vector3.zero;
                // Push far enough that a deep model's front face stays in front of the near
                // plane (orthographic, so depth affects clipping only, not size).
                var depth = has ? Mathf.Max(ModelDepth, finalBounds.extents.z + 1f) : ModelDepth;
                model.localPosition = new Vector3(-c.x, -c.y, depth - c.z) + opt.PositionOffset;
            }
            else
            {
                // Perspective fit: size the model by ModelScale, then place it at the
                // distance where its padded box fills the frustum (front face fitted, so
                // the whole box is guaranteed inside), and center laterally.
                model.localScale = slot.BaseScale * opt.ModelScale;
                model.localPosition = Vector3.zero;
                if (TryComputeCameraLocalBounds(slot, out var b))
                {
                    var pad = Mathf.Max(0.0001f, opt.BoundingBoxScale);
                    var tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    var tanH = tanV * aspect;
                    var distance = b.extents.z +
                        Mathf.Max(b.extents.y * pad / tanV, b.extents.x * pad / tanH);
                    var c = b.center;
                    model.localPosition = new Vector3(-c.x, -c.y, distance - c.z) + opt.PositionOffset;
                }
                else
                {
                    model.localPosition = new Vector3(0f, 0f, ModelDepth) + opt.PositionOffset;
                    WarnNoBounds(slot);
                }
            }
        }

        private void WarnNoBounds(ModelSlot slot) => Debug.LogWarning(
            $"View on '{name}': model '{slot.ReferenceName}' has no mesh bounds to frame; " +
            "using a default. Provide ModelOptions.BoundingBox to override.");

        // The layer-swap render: flip the whole model hierarchy onto the visible layer,
        // render this one camera, then flip it back to DoNotRenderLayer. Because every
        // other model sits on DoNotRenderLayer and each camera only sees UIModelLayer,
        // no camera ever picks up another model — even though they share world space.
        private void RenderSlot(ModelSlot slot)
        {
            SetHierarchyLayer(slot, _uiModelLayer);
            slot.Camera.Render();
            SetHierarchyLayer(slot, _doNotRenderLayer);
        }

        // Set every GameObject under the model (root plus all descendants, so child
        // renderers are covered) to `layer`. The transform list is cached at build time.
        private static void SetHierarchyLayer(ModelSlot slot, int layer)
        {
            var transforms = slot.ModelTransforms;
            if (transforms == null) return;
            foreach (var t in transforms)
                if (t != null)
                    t.gameObject.layer = layer;
        }

        // Combined axis-aligned bounds of the model in the camera's local space, honoring
        // the current rotation/scale/position of the model. Uses the override box when one
        // is set, otherwise every mesh under the model. Returns false when there's nothing
        // to measure (no override, no meshes).
        private static bool TryComputeCameraLocalBounds(ModelSlot slot, out Bounds bounds)
        {
            bounds = default;
            var toCameraLocal = slot.Camera.transform.worldToLocalMatrix;
            var has = false;

            if (slot.Options.BoundingBox is Bounds ob)
            {
                Encapsulate(ref bounds, ref has, ob, toCameraLocal * slot.Model.transform.localToWorldMatrix);
                return has;
            }

            foreach (var mf in slot.Model.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh != null)
                    Encapsulate(ref bounds, ref has, mesh.bounds, toCameraLocal * mf.transform.localToWorldMatrix);
            }
            foreach (var smr in slot.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh != null)
                    Encapsulate(ref bounds, ref has, mesh.bounds, toCameraLocal * smr.transform.localToWorldMatrix);
            }
            return has;
        }

        // Transform the 8 corners of `local` by `m` and grow `bounds` to contain them.
        private static void Encapsulate(ref Bounds bounds, ref bool has, Bounds local, Matrix4x4 m)
        {
            var c = local.center;
            var e = local.extents;
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                var p = m.MultiplyPoint3x4(corner);
                if (!has)
                {
                    bounds = new Bounds(p, Vector3.zero);
                    has = true;
                }
                else
                {
                    bounds.Encapsulate(p);
                }
            }
        }

        // Layers are a project-level setup step (the caller creates UIModelLayer and
        // DoNotRenderLayer). Fail loudly and early if they're missing rather than
        // silently rendering onto the wrong layer.
        private static void EnsureLayers()
        {
            if (_uiModelLayer == -2)
            {
                _uiModelLayer = LayerMask.NameToLayer("UIModelLayer");
                _doNotRenderLayer = LayerMask.NameToLayer("DoNotRenderLayer");
            }
            if (_uiModelLayer < 0 || _doNotRenderLayer < 0)
                throw new InvalidOperationException(
                    "View 3D models require the layers 'UIModelLayer' and 'DoNotRenderLayer' to exist. " +
                    "Add them in Project Settings > Tags and Layers.");
        }

        // Owns everything one model needs at runtime. All lifetime-bearing state (CTS,
        // Addressables handle, spawned camera/model, render texture) is confined here so
        // the outer View just iterates _modelSlots to release everything. The RawImage
        // and its captured original color persist across rebuilds — only TearDownInstance
        // resets the transient rendering state.
        private class ModelSlot
        {
            // Resolved once in GetOrCreateModelSlot; stable across rebuilds.
            public string ReferenceName;
            public RawImage RawImage;
            public Color OriginalColor;

            // Per-configuration state. Reset by TearDownInstance before each SetModel.
            public ModelOptions Options;
            public CancellationTokenSource Cts;
            public AsyncOperationHandle<GameObject> Handle;
            public Task CurrentTask;

            public GameObject CameraGo;
            public Camera Camera;
            public GameObject Model;
            public Transform[] ModelTransforms;
            public Vector3 BaseScale = Vector3.one;

            public RenderTexture RenderTexture;
            public int LastWidth;
            public int LastHeight;
            public bool NeedsRender;

            public void ReleaseRenderTexture()
            {
                if (RenderTexture == null) return;
                RenderTexture.Release();
                UnityEngine.Object.Destroy(RenderTexture);
                RenderTexture = null;
            }

            // Returns the slot to its pre-SetModel shape: in-flight load cancelled,
            // camera subtree destroyed, handle + render texture released, RawImage
            // cleared and hidden. Keeps RawImage/OriginalColor so a later SetModel (or a
            // fresh reveal) reuses them. Safe to call on an already-empty slot.
            public void TearDownInstance()
            {
                if (Cts != null)
                {
                    Cts.Cancel();
                    Cts.Dispose();
                    Cts = null;
                }

                if (CameraGo != null)
                    UnityEngine.Object.Destroy(CameraGo);
                CameraGo = null;
                Camera = null;
                Model = null;
                ModelTransforms = null;
                BaseScale = Vector3.one;

                if (Handle.IsValid())
                {
                    Addressables.Release(Handle);
                    Handle = default;
                }

                ReleaseRenderTexture();

                if (RawImage != null)
                {
                    RawImage.texture = null;
                    var c = RawImage.color;
                    c.a = 0f;
                    RawImage.color = c;
                }

                Options = null;
                CurrentTask = null;
                LastWidth = 0;
                LastHeight = 0;
                NeedsRender = false;
            }
        }
    }
}
