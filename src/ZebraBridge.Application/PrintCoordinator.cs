using System.Threading;

namespace ZebraBridge.Application;

public sealed class PrintCoordinator
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public async Task<T> RunLockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _mutex.Release();
        }
    }
}
