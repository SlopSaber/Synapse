using System.Reflection;
using System.Threading.Tasks;

namespace Synapse.Extras;

internal static class EmbeddedSpriteResourceBytes
{
    private static Task<byte[]?>? _promoBack;
    private static Task<byte[]?>? _promoPlaceholder;
    private static Task<byte[]?>? _finishPlaceholder;
    private static bool _started;

    internal static void Prepare(Assembly assembly)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _promoBack = CountdownFileWorker.ReadResource(assembly, "Synapse.Resources.promo_back.png");
        _promoPlaceholder = CountdownFileWorker.ReadResource(assembly, "Synapse.Resources.promo_placeholder.png");
        _finishPlaceholder = CountdownFileWorker.ReadResource(assembly, "Synapse.Resources.finish_placeholder.png");
    }

    internal static bool TryGet(string path, out byte[] bytes)
    {
        bytes = null!;
        if (Plugin.GameVersion != "1.45.2")
        {
            return false;
        }

        Task<byte[]?>? preparation;
        switch (path)
        {
            case "Synapse.Resources.promo_back.png":
                preparation = _promoBack;
                break;
            case "Synapse.Resources.promo_placeholder.png":
                preparation = _promoPlaceholder;
                break;
            case "Synapse.Resources.finish_placeholder.png":
                preparation = _finishPlaceholder;
                break;
            default:
                return false;
        }

        if (preparation?.Status != TaskStatus.RanToCompletion)
        {
            return false;
        }

        byte[]? prepared = preparation.Result;
        if (prepared == null)
        {
            return false;
        }

        bytes = prepared;
        return true;
    }
}
