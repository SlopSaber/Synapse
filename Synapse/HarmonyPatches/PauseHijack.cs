using BeatSaberMarkupLanguage;
using JetBrains.Annotations;
using SiraUtil.Affinity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace Synapse.HarmonyPatches;

internal class PauseHijack : IAffinity, IInitializable
{
    private readonly PauseMenuManager _pauseMenuManager;
    private readonly SaberManager _saberManager;
    private bool _warningShown;

    private GameObject _warningText = null!;

    [UsedImplicitly]
    private PauseHijack(SaberManager saberManager, PauseMenuManager pauseMenuManager)
    {
        _saberManager = saberManager;
        _pauseMenuManager = pauseMenuManager;
    }

    public void Initialize()
    {
        const string warning =
            "<color=\"red\">WARNING\nQuitting will submit your current score.\nAre you sure you want to quit?</color>";
        TextMeshProUGUI textMesh = Synapse.Extras.UICompatibility.CreateText(
            (RectTransform)_pauseMenuManager.transform.Find("Wrapper/MenuWrapper/Canvas"),
            warning,
            Vector2.zero);
        textMesh.alignment = TextAlignmentOptions.Center;
        textMesh.fontSize = 6;
        textMesh.transform.position = new Vector3(0, 2f, 2f);
        _warningText = textMesh.gameObject;
        _warningText.SetActive(false);
    }

    [AffinityPrefix]
    [AffinityPatch(typeof(PauseMenuManager), nameof(PauseMenuManager.MenuButtonPressed))]
    private bool ConfirmQuit()
    {
        if (_warningShown)
        {
            return true;
        }

        _warningShown = true;
        _warningText.SetActive(true);
        return false;
    }

    [AffinityPrefix]
    [AffinityPatch(typeof(GamePause), nameof(GamePause.Pause))]
    private bool DisablePause(ref bool ____pause)
    {
        ____pause = true;
        _saberManager.disableSabers = true;
        return false;
    }

    [AffinityPrefix]
    [AffinityPatch(typeof(PauseMenuManager), nameof(PauseMenuManager.RestartButtonPressed))]
    private bool DisableRestart()
    {
        return false;
    }

    [AffinityPostfix]
    [AffinityPatch(typeof(PauseMenuManager), nameof(PauseMenuManager.Start))]
    private void DisableRestartButton(Button ____restartButton)
    {
        if (____restartButton)
        {
            ____restartButton.gameObject.SetActive(false);
        }
    }

    [AffinityPostfix]
    [AffinityPatch(typeof(PauseMenuManager), nameof(PauseMenuManager.ShowMenu))]
    private void ResetWarning(TextMeshProUGUI ____backButtonText)
    {
        _warningShown = false;
        _warningText.SetActive(false);
    }

    [AffinityPostfix]
    [AffinityPatch(typeof(PauseController), nameof(PauseController.HandlePauseMenuManagerDidStartResumeAnimation))]
    private void InstantResume(GamePause ____gamePause)
    {
        ____gamePause.Resume();
    }
}
