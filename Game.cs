using System.Threading;
using System.Threading.Tasks;

namespace Heartwood
{
    public abstract class Game
    {
        public abstract Task StartAsync(CancellationToken ct);

        public virtual void Tick() { }

        public void Start() => Core.FireAndForget(StartAsync(Core.Instance.Token));
    }
}
