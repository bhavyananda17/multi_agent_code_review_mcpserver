using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MultiAgentCodeReview.Core.RateLimiting;

public class TokenBudget
{
    private readonly int _rpmLimit;
    private readonly int _tpmLimit;
    private readonly ILogger<TokenBudget>? _logger;
    
    private readonly ConcurrentQueue<DateTime> _requestTimestamps = new();
    private readonly ConcurrentQueue<(DateTime Timestamp, int Tokens)> _tokenTimestamps = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public TokenBudget(int rpmLimit, int tpmLimit, ILogger<TokenBudget>? logger = null)
    {
        _rpmLimit = rpmLimit;
        _tpmLimit = tpmLimit;
        _logger = logger;
    }

    public async Task WaitForBudgetAsync(int estimatedTokens, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                CleanOldEntries();

                var currentRpm = _requestTimestamps.Count;
                var currentTpm = _tokenTimestamps.Sum(x => x.Tokens);

                if (currentRpm < _rpmLimit && currentTpm + estimatedTokens <= _tpmLimit)
                {
                    _requestTimestamps.Enqueue(DateTime.UtcNow);
                    _tokenTimestamps.Enqueue((DateTime.UtcNow, estimatedTokens));
                    return;
                }

                var waitTime = CalculateWaitTime(currentRpm, currentTpm, estimatedTokens);
                _logger?.LogDebug("Rate limit reached. RPM: {Rpm}/{RpmLimit}, TPM: {Tpm}/{TpmLimit}. Waiting {WaitTime}ms",
                    currentRpm, _rpmLimit, currentTpm, _tpmLimit, waitTime.TotalMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public void RecordUsage(int actualTokens)
    {
        _semaphore.Wait();
        try
        {
            CleanOldEntries();
            // Update the last entry with actual tokens
            if (_tokenTimestamps.TryDequeue(out var last))
            {
                _tokenTimestamps.Enqueue((last.Timestamp, actualTokens));
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void CleanOldEntries()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        
        while (_requestTimestamps.TryPeek(out var ts) && ts < cutoff)
            _requestTimestamps.TryDequeue(out _);
            
        while (_tokenTimestamps.TryPeek(out var ts) && ts.Timestamp < cutoff)
            _tokenTimestamps.TryDequeue(out _);
    }

    private TimeSpan CalculateWaitTime(int currentRpm, int currentTpm, int estimatedTokens)
    {
        var waitTimes = new List<TimeSpan>();

        if (currentRpm >= _rpmLimit && _requestTimestamps.TryPeek(out var oldestRequest))
        {
            waitTimes.Add(oldestRequest.AddMinutes(1) - DateTime.UtcNow);
        }

        if (currentTpm + estimatedTokens > _tpmLimit && _tokenTimestamps.TryPeek(out var oldestToken))
        {
            waitTimes.Add(oldestToken.Timestamp.AddMinutes(1) - DateTime.UtcNow);
        }

        return waitTimes.Any() ? waitTimes.Max() : TimeSpan.FromSeconds(1);
    }
}