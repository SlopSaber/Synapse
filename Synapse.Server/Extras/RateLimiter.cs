using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Synapse.Server.Extras;

public static class RateLimiter
{
    public static readonly ConcurrentDictionary<string, int> _rateLimit = new();

    public static bool RateLimit(object id, int max, int milliseconds, [CallerMemberName] string memberName = "")
    {
        string index = $"{id.GetHashCode()}/{memberName}";

        int count = _rateLimit.AddOrUpdate(index, 1, (_, n) => n + 1);
        if (count > max)
        {
            return true;
        }

        _ = Task
            .Delay(milliseconds)
            .ContinueWith(_ => { _rateLimit.TryRemove(index, out int _); });
        return false;
    }
}
