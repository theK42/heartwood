using System.Threading;
using System.Threading.Tasks;

namespace Heartwood.UI
{
    public class GenericModal : Modal
    {
        private readonly string _title;
        private readonly string _message;

        public GenericModal(string title, string message)
        {
            _title = title;
            _message = message;
        }

        protected override string Address => "GenericModal.prefab";

        protected override Task SetupAsync(CancellationToken ct)
        {
            var view = Root.GetComponent<View>();
            view.SetText("Title", _title);
            view.SetText("Message", _message);
            view.SetText("ButtonText", "OK");
            view.SetAction("Button", () =>
            {
                ScreenManager.Instance.Pop();
            });
            return Task.CompletedTask;
        }
    }
}
