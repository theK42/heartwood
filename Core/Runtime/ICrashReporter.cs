using System;

namespace Heartwood
{
    // Optional crash-reporting backend, registered via Core.RegisterCrashReporter
    // (e.g. FirebaseCrashReporter from com.thek42.heartwood.firebase).
    public interface ICrashReporter
    {
        // Called from Core.Awake once Core's own exception handlers are subscribed.
        void Initialize();

        // Last resort: the error handler itself threw. May be called from any thread; must not throw.
        void RecordHandlerFailure(Exception e);
    }
}
