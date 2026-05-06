using System;
using System.Threading;
using JetBrains.Annotations;

namespace Synapse.Managers;

[UsedImplicitly]
internal sealed class CancellationTokenManager : IDisposable
{
    private CancellationTokenSource _tokenSource = new();

    private bool _disposed;

    public void Dispose()
    {
        _tokenSource.Dispose();
        _disposed = true;
    }

    internal void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        _tokenSource.Cancel();
    }

    internal CancellationToken Reset()
    {
        if (_disposed)
        {
            return CancellationToken.None;
        }

        _tokenSource.Cancel();
        _tokenSource.Dispose();
        _tokenSource = new CancellationTokenSource();
        return _tokenSource.Token;
    }
}
