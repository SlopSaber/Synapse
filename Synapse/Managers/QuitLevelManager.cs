using System;
using IPA.Utilities.Async;
using JetBrains.Annotations;

namespace Synapse.Managers;

internal class QuitLevelManager : IDisposable
{
    private readonly NetworkManager _networkManager;
    private readonly PrepareLevelCompletionResults _prepareLevelCompletionResults;
    private readonly StandardLevelScenesTransitionSetupData _standardLevelScenesTransitionSetupData;

    [UsedImplicitly]
    internal QuitLevelManager(
        NetworkManager networkManager,
        PrepareLevelCompletionResults prepareLevelCompletionResults,
        StandardLevelScenesTransitionSetupData standardLevelScenesTransitionSetupData)
    {
        _networkManager = networkManager;
        _prepareLevelCompletionResults = prepareLevelCompletionResults;
        _standardLevelScenesTransitionSetupData = standardLevelScenesTransitionSetupData;
        networkManager.StopLevelReceived += OnStopLevelReceived;
        networkManager.Disconnected += OnDisconnected;
    }

    public void Dispose()
    {
        _networkManager.StopLevelReceived -= OnStopLevelReceived;
        _networkManager.Disconnected -= OnDisconnected;
    }

    private void OnDisconnected(string _)
    {
        StopLevel();
    }

    private void OnStopLevelReceived()
    {
        StopLevel();
    }

    private void StopLevel()
    {
        LevelCompletionResults levelCompletionResults = _prepareLevelCompletionResults.FillLevelCompletionResults(
            LevelCompletionResults.LevelEndStateType.Incomplete,
            LevelCompletionResults.LevelEndAction.None);
        UnityMainThreadTaskScheduler.Factory.StartNew(
            () => { _standardLevelScenesTransitionSetupData.Finish(levelCompletionResults); });
    }
}
