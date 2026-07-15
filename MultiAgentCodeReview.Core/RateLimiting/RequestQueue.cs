using System.Collections.Concurrent;

namespace MultiAgentCodeReview.Core.RateLimiting;

public enum RequestPriority
{
    Critical = 0,   // Security
    High = 1,       // Synthesis
    Normal = 2,     // Specialists
    Low = 3         // Triage, Docs
}

public class RequestQueue
{
    private readonly PriorityQueue<QueuedRequest, int> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly int _maxConcurrent;

    public RequestQueue(int maxConcurrent = 1)
    {
        _maxConcurrent = maxConcurrent;
    }

    public async Task<T> EnqueueAsync<T>(
        Func<CancellationToken, Task<T>> factory,
        RequestPriority priority = RequestPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<T>();
        var request = new QueuedRequest
        {
            Factory = ct => factory(ct).ContinueWith(t =>
            {
                if (t.IsFaulted) tcs.TrySetException(t.Exception!);
                else if (t.IsCanceled) tcs.TrySetCanceled();
                else tcs.TrySetResult(t.Result);
            }, TaskScheduler.Default),
            Priority = priority,
            Completion = tcs
        };

        _queue.Enqueue(request, (int)priority);
        await ProcessQueueAsync(cancellationToken);
        return await tcs.Task;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        if (!_semaphore.Wait(0)) return;

        try
        {
            while (_queue.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                if (_semaphore.CurrentCount <= 0) break;

                var request = _queue.Dequeue();
                _ = request.Factory(cancellationToken);
                
                // Small delay to prevent tight loop
                await Task.Delay(50, cancellationToken);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private class QueuedRequest
    {
        public required Func<CancellationToken, Task> Factory { get; init; }
        public required RequestPriority Priority { get; init; }
        public required object Completion { get; init; }
    }
}