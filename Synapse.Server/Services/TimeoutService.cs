using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Synapse.Server.Services;

public interface ITimeoutService
{
    public void Timeout(Action action, int milliseconds, [CallerMemberName] string memberName = "");
}

public class TimeoutService(ILogger<RoleService> log) : ITimeoutService
{
    public static readonly ConcurrentDictionary<string, bool> _timeoutFinished = new();

    public void Timeout(Action action, int milliseconds, string memberName = "")
    {
        if (_timeoutFinished.AddOrUpdate(memberName, false, (_, _) => true))
        {
            return;
        }

        try
        {
            action();
            _ = TimeoutTask(memberName, action, milliseconds);
        }
        catch (Exception e)
        {
            log.LogCritical(e, "An exception occurred while trying to start timeout");
        }
    }

    private async Task TimeoutTask(string index, Action action, int milliseconds)
    {
        await Task.Delay(milliseconds);
        if (_timeoutFinished.TryRemove(index, out bool timeout) && timeout)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                log.LogCritical(e, "An exception occurred while trying to finish timeout");
            }
        }
    }
}
