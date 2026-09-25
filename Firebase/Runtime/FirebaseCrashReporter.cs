using System;
using Firebase;
using Firebase.Crashlytics;
using UnityEngine;

namespace Heartwood
{
    public class FirebaseCrashReporter : ICrashReporter
    {
        public void Initialize()
        {
            // Continuation runs on a threadpool thread — don't touch Unity APIs from it without dispatching.
            FirebaseApp.CheckAndFixDependenciesAsync().ContinueWith(task =>
            {
                if (task.Result == DependencyStatus.Available)
                {
                    // Crashlytics' auto-handler is hooked on logMessageReceivedThreaded too; with
                    // this set it reports exceptions that flow through Core's handlers as fatal.
                    Crashlytics.ReportUncaughtExceptionsAsFatal = true;
                    Debug.Log("Firebase ready; Crashlytics active.");
                }
                else
                {
                    Debug.LogError($"Firebase dependency check failed: {task.Result}");
                }
            });
        }

        public void RecordHandlerFailure(Exception e)
        {
            Crashlytics.SetCustomKey("error_handler_failure", "true");
            Crashlytics.LogException(new Exception("ERROR HANDLER EXCEPTION", e));
        }
    }
}
