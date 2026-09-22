using System;
using System.Threading;

namespace Heartwood.UI
{
    // Setup/teardown recipe for a pooled table's elements. Passed to View.SetTable.
    // OnAttach is called each time a pooled element becomes visible, with the index it
    // now represents and a CancellationToken tied to that element's active binding —
    // pass the token to any async work (SetImageAsync, sub-tables) so it's cancelled
    // when the element scrolls off or the table is torn down. OnDetach is optional
    // and fires after the element's token has already been cancelled.
    public class TableAdapter
    {
        public Action<View, int, CancellationToken> OnAttach;
        public Action<View, int> OnDetach;
    }
}
