using System;
using System.Threading;
using System.Threading.Tasks;
using Firebase;
using Firebase.Crashlytics;
using UnityEngine;

namespace Heartwood
{
    public class Core : MonoBehaviour
    {
        private static Core _instance;
        public static Core Instance => _instance;

        public Game Game { get; private set; }

        // Set from a [RuntimeInitializeOnLoadMethod(BeforeSplashScreen)] in the game
        // project, which runs before Core's BeforeSceneLoad bootstrap.
        private static Func<Game> _gameFactory;
        public static void RegisterGame(Func<Game> factory) => _gameFactory = factory;

        private CancellationTokenSource _rootCts;
        public CancellationToken Token => _rootCts.Token;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            var go = new GameObject(nameof(Core));
            _instance = go.AddComponent<Core>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            _rootCts = new CancellationTokenSource();

            // Subscribe before Firebase init so early-startup exceptions are still caught.
            // Crashlytics' auto-handler is hooked on logMessageReceivedThreaded too; with
            // ReportUncaughtExceptionsAsFatal=true (set in the continuation below) it will
            // report exceptions that flow through here as fatal.
            Application.logMessageReceivedThreaded += OnLogMessage;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // Continuation runs on a threadpool thread — don't touch Unity APIs from it without dispatching.
            FirebaseApp.CheckAndFixDependenciesAsync().ContinueWith(task =>
            {
                if (task.Result == DependencyStatus.Available)
                {
                    Crashlytics.ReportUncaughtExceptionsAsFatal = true;
                    Debug.Log("Firebase ready; Crashlytics active.");
                }
                else
                {
                    Debug.LogError($"Firebase dependency check failed: {task.Result}");
                }
            });
        }

        private void OnDestroy()
        {
            Application.logMessageReceivedThreaded -= OnLogMessage;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

            _rootCts?.Cancel();
            _rootCts?.Dispose();

            // Clear the static slot so a fresh Bootstrap can install a new Core (the
            // restart-from-Bootstrap path). Guarded in case some other Core has taken
            // over the slot already.
            if (_instance == this) _instance = null;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            try
            {
                // TODO: kick off the error-handler flow — halt as much running behavior
                // as possible, show a popup, on OK tear down and restart from Bootstrap.
                // Crashlytics' auto-handler is already reporting these as fatal.

                ScreenManager.Instance.PushModal(new GenericModal("Error", "An unhandled exception occurred."));
            }
            catch (Exception e)
            {
                HandleErrorHandlerException(e);
            }
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            try
            {
                args.SetObserved();
                // Re-log so it flows through Crashlytics' auto-handler (fatal) and OnLogMessage.
                Debug.LogException(args.Exception);
            }
            catch (Exception e)
            {
                HandleErrorHandlerException(e);
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                    Debug.LogException(ex);
                else
                    Debug.LogError($"Unhandled non-Exception object: {args.ExceptionObject}");
            }
            catch (Exception e)
            {
                HandleErrorHandlerException(e);
            }
        }

        private static void HandleErrorHandlerException(Exception e)
        {
            // The safety net itself broke. Best-effort log, then bring the app down hard —
            // we can't trust that anything else is still working.
            try
            {
                Crashlytics.SetCustomKey("error_handler_failure", "true");
                Crashlytics.LogException(new Exception("ERROR HANDLER EXCEPTION", e));
            }
            catch
            {
                // Nothing more we can do here.
            }

#if UNITY_EDITOR
            // Force-closing the editor is a terrible idea; dispatch a dialog + playmode exit
            // to the main thread (this handler may have fired from a background thread).
            UnityEditor.EditorApplication.delayCall += () =>
            {
                UnityEditor.EditorUtility.DisplayDialog(
                    "Fatal: error handler crashed",
                    $"The error-handling system itself threw:\n\n{e.GetType().Name}: {e.Message}\n\nExiting play mode.",
                    "OK");
                UnityEditor.EditorApplication.isPlaying = false;
            };
#else
            Environment.FailFast("Error-handler exception", e);
#endif
        }

        // The only sanctioned async void in the project: bridges void callers into the
        // async/await world, swallows OperationCanceledException, routes everything else
        // through Debug.LogException (Crashlytics-fatal + the OnLogMessage flow).
        public static async void FireAndForget(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation; intentionally silent.
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void Start()
        {
            if (_gameFactory == null)
                throw new InvalidOperationException(
                    $"No {nameof(Game)} factory registered. Call {nameof(Core)}.{nameof(RegisterGame)} " +
                    "from a [RuntimeInitializeOnLoadMethod(BeforeSplashScreen)] method before scene load.");

            Game = _gameFactory();
            Game.Start();
        }

        private void Update() => Game?.Tick();
    }
}
