namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>
    /// Bảo đảm các request tới cùng một sàn cách nhau tối thiểu <see cref="MinInterval"/> (tính từ lúc bắt đầu request).
    /// Một instance cho mỗi sàn, dùng chung giữa mọi HttpClient của sàn đó. MinInterval = 0 → không giới hạn.
    /// </summary>
    public sealed class MinIntervalGate
    {
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

        public MinIntervalGate(TimeSpan minInterval, TimeProvider timeProvider = null)
        {
            MinInterval = minInterval < TimeSpan.Zero ? TimeSpan.Zero : minInterval;
            _time = timeProvider ?? TimeProvider.System;
        }

        public TimeSpan MinInterval { get; }

        public async Task WaitTurnAsync(CancellationToken cancellationToken = default)
        {
            if (MinInterval == TimeSpan.Zero) return;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                var wait = _nextAllowed - _time.GetUtcNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, cancellationToken);
                _nextAllowed = _time.GetUtcNow() + MinInterval;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>Gắn vào pipeline của named HttpClient: chờ tới lượt rồi mới gửi request.</summary>
    public sealed class RateLimitingHandler : DelegatingHandler
    {
        private readonly MinIntervalGate _gate;

        public RateLimitingHandler(MinIntervalGate gate)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _gate.WaitTurnAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
