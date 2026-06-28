using System;
using System.Threading;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.ClientModels;
using PlayFab.CloudScriptModels;
using PlayFab.Json;
using UnityEngine;

namespace Heartwood
{
    public class ServerAPI
    {
        // TODO: Before shipping, replace LoginWithCustomID with a platform login
        // (LoginWithGoogleAccount / LoginWithApple for full OAuth, or
        // LoginWithGooglePlayGamesServices / LoginWithGameCenter for game-services auth).
        // Reason: client-side CreateAccount=true lets anyone with the (public) TitleId
        // mint unlimited accounts, inflating MAU/quota and farming starter rewards.
        // Platform tokens are gated by the platform's own anti-abuse, so PlayFab can
        // trust them. Alternative: gate creation behind a CloudScript / Azure Function
        // that validates a Play Integrity / App Attest token.
        //
        // Dev bypass: Play Integrity fails on sideloaded APKs / most emulators, and
        // App Attest is unavailable in the iOS Simulator, so attestation can't run
        // during local dev. Standard pattern is a two-path login: production builds
        // require a real attestation token, debug builds present a dev-only secret
        // (or skip straight to a dev login path on the CloudScript / Azure Function).
        // Guard the dev path with #if UNITY_EDITOR || DEVELOPMENT_BUILD so it is
        // stripped from production binaries, and ideally point dev builds at a
        // separate function URL that does not exist in prod.
        public async Task LoginAsync(CancellationToken ct)
        {
            var request = new LoginWithCustomIDRequest
            {
                CustomId = SystemInfo.deviceUniqueIdentifier,
                CreateAccount = true
            };

            var result = await ToTask<LoginResult>(
                (onSuccess, onError) => PlayFabClientAPI.LoginWithCustomID(request, onSuccess, onError),
                ct);

            Debug.Log($"PlayFab login successful. PlayFabId: {result.PlayFabId}");
        }

        public void Login() => Core.FireAndForget(LoginAsync(Core.Instance.Token));

        // Wraps PlayFab's success/error callback pair into a Task. RunContinuationsAsynchronously
        // keeps the awaiter off PlayFab's callback thread so we don't re-enter the SDK inline.
        protected static Task<TResult> ToTask<TResult>(
            Action<Action<TResult>, Action<PlayFabError>> call,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));

            call(
                result =>
                {
                    registration.Dispose();
                    tcs.TrySetResult(result);
                },
                err =>
                {
                    registration.Dispose();
                    tcs.TrySetException(new PlayFabException(err));
                });

            return tcs.Task;
        }

        // Calls a PlayFab-registered Azure Function via ExecuteFunction, deserializing the
        // FunctionResult into T. Transport errors come back as PlayFabException (via ToTask);
        // function-runtime errors come back on the success path with result.Error populated
        // and are surfaced as AzureFunctionException.
        protected static async Task<T> ExecuteFunctionAsync<T>(string name, object args, CancellationToken ct)
        {
            var request = new ExecuteFunctionRequest
            {
                FunctionName = name,
                FunctionParameter = args,
                GeneratePlayStreamEvent = true,
            };
            var result = await ToTask<ExecuteFunctionResult>(
                (onSuccess, onError) => PlayFabCloudScriptAPI.ExecuteFunction(request, onSuccess, onError),
                ct);
            if (result.Error != null)
                throw new AzureFunctionException(result.Error);

            var json = PlayFabSimpleJson.SerializeObject(result.FunctionResult);
            return PlayFabSimpleJson.DeserializeObject<T>(json);
        }

        public class PlayFabException : Exception
        {
            public PlayFabError Error { get; }
            public PlayFabException(PlayFabError error) : base(error.GenerateErrorReport()) => Error = error;
        }

        public class AzureFunctionException : Exception
        {
            public FunctionExecutionError Error { get; }
            public AzureFunctionException(FunctionExecutionError error)
                : base($"{error.Error} - {error.Message}\n{error.StackTrace}") => Error = error;
        }
    }
}
