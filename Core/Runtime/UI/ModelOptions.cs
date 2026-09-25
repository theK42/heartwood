using UnityEngine;

namespace Heartwood.UI
{
    // Optional per-model overrides for View.SetModel. Every field has a sensible
    // default, so a caller can set any subset — or pass null to SetModel to take all
    // defaults. Stored on the model's slot so it can be re-applied verbatim when a
    // layout change invalidates the render texture size and framing.
    public class ModelOptions
    {
        // Camera projection. Perspective (false) is the default; set true for an
        // orthographic camera. AutoFit works differently for the two: orthographic fits
        // by zoom, perspective fits by distance (see AutoFit).
        public bool Orthographic = false;

        // Framing box to use instead of the model's real renderer bounds, in the
        // model's local space. Null => the box is measured from the model's meshes.
        public Bounds? BoundingBox;

        // Padding multiplier on the framing box. >1 leaves a margin around the model
        // inside the image; the default 1.1 gives a little breathing room.
        public float BoundingBoxScale = 1.1f;

        // When true (default) the model is auto-fitted into the image, then PositionOffset
        // and ModelScale are applied on top:
        //  * Orthographic: the camera zoom is fitted to the (padded) box and the model is
        //    centered — fit sizes via zoom, and ModelScale then adjusts size on top.
        //  * Perspective: the model is dollied to the distance at which its (padded) box
        //    fills the frustum and centered — fit sizes via distance, not scale, but
        //    ModelScale is still applied to the model.
        // When false, no fitting or centering happens: the model simply uses ModelScale
        // and PositionOffset as given (orthographic uses a fixed default zoom).
        public bool AutoFit = true;

        // Added to the fitted position when AutoFit is true; used as the camera-local
        // position directly when AutoFit is false.
        public Vector3 PositionOffset = Vector3.zero;

        // Model orientation relative to the camera. Null => rotated to face the camera.
        public Quaternion? Rotation;

        // Multiplier applied to the model's scale. Under orthographic AutoFit it layers a
        // size adjustment on top of the zoom fit; under perspective AutoFit it changes the
        // model's physical size (the fit distance adapts, so use BoundingBoxScale for
        // margin). When AutoFit is false it's simply the model's scale.
        public float ModelScale = 1f;

        // Camera clear color. Defaults to fully transparent so only the model shows
        // through the image.
        public Color ClearColor = Color.clear;

        // Animated models must be redrawn every frame (the default). Set false to draw
        // a single frame — an optimization for static, non-animated models. Even a
        // render-once model is redrawn once more whenever a layout change rebuilds its
        // render texture.
        public bool RenderEveryFrame = true;
    }
}
