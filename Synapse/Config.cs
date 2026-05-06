using System;
using System.Runtime.CompilerServices;
using IPA.Config.Stores;
using JetBrains.Annotations;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]

namespace Synapse;

// ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public class Config
{
    public event Action? Updated;

    public virtual bool DisableMenuTakeover { get; set; }

    public virtual bool DisableMenuTakeoverAudio { get; set; }

    public bool? JoinChat { get; set; }

    public EventInfo LastEvent { get; set; } = new();

    public virtual bool DisableLobbyAudio { get; set; }

    public bool ProfanityFilter { get; set; } = true;

    public bool ShowJoinLeaveMessages { get; set; } = true;

    public bool ShowEliminated { get; set; } = true;

    public virtual string? Temp { get; set; } = string.Empty;

    public string Url { get; set; } = "https://synapse.totalbs.dev/api/v1/directory";

    public virtual void Changed()
    {
        Updated?.Invoke();
    }
}

public class EventInfo
{
    public string Title { get; set; } = string.Empty;

    public bool SeenIntro { get; set; }

    public bool SeenOutro { get; set; }

    public int? Division { get; set; }
}
