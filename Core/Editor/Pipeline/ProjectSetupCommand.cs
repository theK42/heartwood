using System;
using Unity.Pipeline.Commands;

namespace Heartwood
{
    // Exposes ProjectSetup to the Unity CLI. Only compiled when the project has
    // com.unity.pipeline (which `unity projects create` adds), via this assembly's define
    // constraint. Headless:   unity run <project> --command heartwood setup -- --game <Name> --git_hooks
    // Running Editor:         unity command heartwood setup --game <Name> --git_hooks
    internal static class ProjectSetupCommand
    {
        [CliCommand("heartwood", "setup", "Set up a new Heartwood project: starter Game class, optional PlayFab/Firebase modules, git hooks. Never overwrites existing files.")]
        private static object Setup(
            [CliArg("game", "Name for the starter Game class (namespace + assembly). Defaults to the Product Name.")] string game = null,
            [CliArg("no_game", "Don't write a starter Game class.")] bool noGame = false,
            [CliArg("playfab_title_id", "Add the PlayFab module and set this Title ID. The PlayFab SDK must already be imported.")] string playFabTitleId = null,
            [CliArg("firebase", "Add the Firebase module. The Firebase SDK must already be imported.")] bool firebase = false,
            [CliArg("git_hooks", "Install the pre-commit hook and set core.hooksPath. The project must be a git repository.")] bool gitHooks = false)
        {
            var options = new ProjectSetup.Options
            {
                UsePlayFab = playFabTitleId != null,
                TitleId = playFabTitleId ?? "",
                UseFirebase = firebase,
                CreateGame = !noGame,
                GameName = game ?? ProjectSetup.SuggestGameName(),
                InstallGitHooks = gitHooks,
            };

            var issues = ProjectSetup.BlockingIssues(options, ProjectSetup.Inspect());
            if (issues.Count > 0)
                throw new InvalidOperationException(string.Join("\n", issues));

            return new { applied = ProjectSetup.Apply(options) };
        }
    }
}
