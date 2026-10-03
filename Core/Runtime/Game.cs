using System.Threading;
using System.Threading.Tasks;
using Heartwood.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Heartwood
{
    public abstract class Game
    {
        public abstract Task StartAsync(CancellationToken ct);

        public virtual void Tick() { }

        // Game project returns a concrete LoadingSpinner whose AddressableKey points at
        // the game's spinner prefab. Return null to opt out (ScreenManager just won't
        // show a spinner during loads).
        public virtual LoadingSpinner CreateLoadingSpinner() => null;

        // How the root UI canvas scales across screen sizes. Override to set the game's own
        // reference resolution (e.g. portrait for a phone game).
        public virtual void ConfigureCanvasScaler(CanvasScaler scaler) => ConfigureDefaultCanvasScaler(scaler);

        // Scales UI with the screen relative to 1920x1080, splitting the difference between
        // width and height so it behaves in both orientations. At 1080p it matches the old
        // constant-pixel-size behavior exactly.
        public static void ConfigureDefaultCanvasScaler(CanvasScaler scaler)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
        }

        public void Start() => Core.FireAndForget(StartAsync(Core.Instance.Token));
    }
}
