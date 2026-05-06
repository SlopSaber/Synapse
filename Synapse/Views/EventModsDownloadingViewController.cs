using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using IPA.Utilities.Async;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Extras;
using Synapse.Networking.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace Synapse.Views;

// ReSharper disable FieldCanBeMadeReadOnly.Local
////[HotReload(RelativePathToLayout = @"../Resources/ModsDownloading.bsml")]
[ViewDefinition("Synapse.Resources.ModsDownloading.bsml")]
internal class EventModsDownloadingViewController : EventDownloadingViewController
{
    [UIComponent("loadingbar")]
    private readonly VerticalLayoutGroup _barGroup = null!;

    [UIObject("downloading")]
    private readonly GameObject _downloadingGroup = null!;

    [UIComponent("downloadingtext")]
    private readonly TMP_Text _downloadingTMP = null!;

    [UIObject("error")]
    private readonly GameObject _error = null!;

    [UIComponent("errortext")]
    private readonly TMP_Text _errorTMP = null!;

    [UIComponent("percentage")]
    private readonly TMP_Text _percentageTMP = null!;

    [UIObject("quit")]
    private readonly GameObject _quitGroup = null!;

    [UIComponent("quittext")]
    private readonly TMP_Text _quitTMP = null!;

    private SiraLog _log = null!;
    private List<ModInfo> _requiredMods = null!;

    internal bool DownloadFinished { get; private set; } // ReSharper disable ConvertToAutoProperty

    protected override TMP_Text PercentageTMP => _percentageTMP;

    protected override TMP_Text DownloadingTMP => _downloadingTMP;

    protected override TMP_Text ErrorTMP => _errorTMP;

    protected override TMP_Text QuitTMP => _quitTMP;

    protected override VerticalLayoutGroup BarGroup => _barGroup;

    protected override GameObject QuitGroup => _quitGroup;

    protected override GameObject DownloadingGroup => _downloadingGroup;

    protected override GameObject Error => _error;

    internal void Init(List<ModInfo> requiredMods)
    {
        DownloadFinished = false;
        _requiredMods = requiredMods;
    }

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);

        if (!addedToHierarchy)
        {
            return;
        }

        if (!DownloadFinished)
        {
            UnityMainThreadTaskScheduler.Factory.StartNew(DownloadAndSave);
        }
        else
        {
            NewView = View.Quit;
        }
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        if (removedFromHierarchy)
        {
            Cancel();
        }
    }

    [UsedImplicitly]
    [Inject]
    private void Construct(SiraLog log)
    {
        _log = log;
    }

    private async Task DownloadAndSave()
    {
        CancellationToken token = ResetToken();
        int count = _requiredMods.Count;
        string unzipPath = Directory.CreateDirectory(
                (Path.GetDirectoryName(Application.dataPath) ?? throw new InvalidOperationException()) +
                $"{Path.DirectorySeparatorChar}IPA{Path.DirectorySeparatorChar}Pending")
            .FullName;
        for (int i = 0; i < count; i++)
        {
            ModInfo mod = _requiredMods[i];
            string url = mod.Url;
            DownloadText = $"Downloading {mod.Id}... ({i + 1}/{count})";
            _log.Debug($"Attempting to download [{mod.Id}] from [{url}]");
            int iteration = i;
            try
            {
                using MemoryStream stream = await MediaExtensions.DownloadHash(
                    url,
                    mod.Hash,
                    n => DownloadProgress = (iteration + (n * 0.5f)) / count,
                    token);
                DownloadText = $"Unzipping {mod.Id}... ({iteration + 1}/{count})";
                await MediaExtensions.Unzip(
                    stream,
                    unzipPath,
                    n => DownloadProgress = (iteration + 0.5f + (n * 0.5f)) / count);
            }
            catch (Exception e)
            {
                LastError = $"Error while downloading [{mod}].\nDownload has been cancelled.";
                NewView = View.Error;
                _log.Error($"Error while downloading [{mod}]\n{e}");
                return;
            }

            _log.Debug($"Successfully downloaded [{mod.Id}]");
        }

        QuitText = $"{count} mod(s) successfully downloaded.\nQuit and manually restart to complete installation.";
        NewView = View.Quit;
        DownloadFinished = true;
    }

    [UsedImplicitly]
    [UIAction("accept-click")]
#pragma warning disable CA1822
    private void OnAcceptClick()
#pragma warning restore CA1822
    {
        Application.Quit();
    }
}
