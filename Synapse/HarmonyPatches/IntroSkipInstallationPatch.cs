using System;
using System.Reflection;
using HarmonyLib;
using IPA.Loader;
using JetBrains.Annotations;

namespace Synapse.HarmonyPatches;

// Auros's Intro Skip has no public api for disabling itself for one map
// And i highly doubt it will ever be updated to include one
// So lets make one ourselves!
[HarmonyPatch]
internal static class IntroSkipInstallationPatch
{
    private static readonly PluginMetadata? _introSkip = PluginManager.GetPlugin("Intro Skip");

    internal static bool SkipNext { get; set; }

    [UsedImplicitly]
    [HarmonyPrepare]
    private static bool Prepare()
    {
        return _introSkip != null;
    }

    [UsedImplicitly]
    [HarmonyPrefix]
    private static bool Skip()
    {
        bool run = !SkipNext;
        SkipNext = false;
        return run;
    }

    // this would be easier if affinity supported targetmethods, but alas
    [UsedImplicitly]
    [HarmonyTargetMethod]
#pragma warning disable CA1859
    private static MethodBase TargetMethod()
#pragma warning restore CA1859
    {
        Type? type = _introSkip?.Assembly.GetType("IntroSkip.Installers.IntroSkipGameInstaller");
        MethodInfo? method = type?.GetMethod("InstallBindings", BindingFlags.Instance | BindingFlags.Public);
        if (type == null)
        {
            throw new InvalidOperationException("Could not find [IntroSkip.Installers.IntroSkipGameInstaller] type");
        }

        return method ?? throw new InvalidOperationException("Could not find [InstallBindings] method");
    }
}
