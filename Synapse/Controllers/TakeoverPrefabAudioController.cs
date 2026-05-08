using UnityEngine;

namespace Synapse.Controllers;

internal class TakeoverPrefabAudioController : PrefabAudioController
{
    private bool _mute;

    protected override void OnConfigUpdated()
    {
        if (Config == null ||
            Config.DisableMenuTakeoverAudio == _mute)
        {
            return;
        }

        _mute = Config.DisableMenuTakeoverAudio;
        foreach (AudioSource audioSource in AudioSources)
        {
            audioSource.mute = _mute;
        }
    }
}
