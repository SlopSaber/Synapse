using System;
using System.Runtime.CompilerServices;
using Synapse.Networking.Models;

namespace Synapse.Extras;

internal static class LaunchModifierPreparation
{
#pragma warning disable SA1300
    internal enum GameplayModifier
    {
        noFailOn0Energy,
        instaFail,
        failOnSaberClash,
        noBombs,
        fastNotes,
        strictAngles,
        disappearingArrows,
        noArrows,
        ghostNotes,
        proMode,
        zenMode,
        smallCubes,
        noEnergy // custom modifier
    }
#pragma warning restore SA1300

    internal static void Prepare(Status? status)
    {
        try
        {
            if (status is null || status.GetType() != typeof(Status) ||
                status.Stage is not PlayStatus play || play.GetType() != typeof(PlayStatus) ||
                play.Map is not { } map || map.GetType() != typeof(Map) ||
                map.Ruleset is not { } ruleset || ruleset.GetType() != typeof(Ruleset) ||
                ruleset.Modifiers is not { } modifiers)
            {
                return;
            }

            string[] strings = (string[])modifiers.Clone();
            bool[] parsed = new bool[strings.Length];
            GameplayModifier[] values = new GameplayModifier[strings.Length];
            for (int index = 0; index < strings.Length; index++)
            {
                parsed[index] = Enum.TryParse(strings[index], true, out values[index]);
            }

            Cache.Entries.Add(ruleset, new PreparedModifiers(strings, parsed, values));
        }
        catch
        {
            // Optional preparation must not discard an otherwise valid status packet.
        }
    }

    internal static bool TryGet(
        Ruleset ruleset,
        int index,
        string? current,
        out bool parsed,
        out GameplayModifier value)
    {
        parsed = false;
        value = default;
        try
        {
            if (!Cache.Entries.TryGetValue(ruleset, out PreparedModifiers prepared) ||
                index < 0 || index >= prepared.Strings.Length ||
                !string.Equals(current, prepared.Strings[index], StringComparison.Ordinal))
            {
                return false;
            }

            parsed = prepared.Parsed[index];
            value = prepared.Values[index];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static class Cache
    {
        internal static ConditionalWeakTable<Ruleset, PreparedModifiers> Entries { get; } = new();
    }

    private sealed class PreparedModifiers
    {
        internal PreparedModifiers(string[] strings, bool[] parsed, GameplayModifier[] values)
        {
            Strings = strings;
            Parsed = parsed;
            Values = values;
        }

        internal string[] Strings { get; }

        internal bool[] Parsed { get; }

        internal GameplayModifier[] Values { get; }
    }
}
