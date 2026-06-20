using System.Threading;
using System.Threading.Tasks;

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

        public void Start() => Core.FireAndForget(StartAsync(Core.Instance.Token));
    }
}
