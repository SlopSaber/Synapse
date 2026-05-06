using System.Collections.Generic;
using JetBrains.Annotations;

namespace Synapse.Networking.Models;

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public interface IStageStatus
{
    public string Name { get; }
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record Status
{
    public string Motd { get; init; } = string.Empty;

    public IStageStatus Stage { get; init; } = new InvalidStatus();
}

public record InvalidStatus : IStageStatus
{
    public string Name => "invalid";
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record IntroStatus : IStageStatus
{
    public string Name => "intro";

    public string Url { get; init; } = string.Empty;

    public float StartTime { get; init; } = float.MinValue;
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record PlayStatus : IStageStatus
{
    public string Name => "play";

    public int Index { get; init; } = -1;

    public float StartTime { get; init; } = float.MinValue;

    public bool Eliminated { get; init; }

    public PlayerScore? PlayerScore { get; init; }

    public Map Map { get; init; } = new();
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record FinishStatus : IStageStatus
{
    public string Name => "finish";

    public string Url { get; init; } = string.Empty;

    public int MapCount { get; init; }
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record PlayerScore
{
    public int Score { get; init; }

    public float Percentage { get; init; }
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record Map
{
    public string Name { get; init; } = string.Empty;

    public string? AltCoverUrl { get; init; } = string.Empty;

    public Ruleset? Ruleset { get; init; }

    public List<Key> Keys { get; init; } = [];

    public List<Download> Downloads { get; init; } = [];
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record Ruleset
{
    public bool? AllowOverrideColors { get; init; }

    public string[]? Modifiers { get; init; } = [];

    public bool? AllowLeftHand { get; init; }

    public bool? AllowResubmission { get; init; }
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record Key
{
    public string Characteristic { get; init; } = string.Empty;

    public int Difficulty { get; init; }
}

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
public record Download
{
    public string GameVersion { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Hash { get; init; } = string.Empty;

    public string? Key { get; init; }
}
