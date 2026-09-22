namespace Heartwood.UI
{
    // Not part of the user-facing stack — ScreenManager keeps the spinner separate
    // and shows it above everything while a Push is waiting on PrepareAsync.
    public abstract class LoadingSpinner : Screen
    {
        public sealed override bool IsModal => true;
    }
}
