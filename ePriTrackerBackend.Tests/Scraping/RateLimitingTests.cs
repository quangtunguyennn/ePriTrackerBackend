using System.Diagnostics;
using System.Net;
using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>Đo thời gian thật với khoảng ngắn; ngưỡng để rộng tay để không chập chờn trên máy chậm.</summary>
    public class RateLimitingTests
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

        [Fact]
        public async Task ZeroInterval_DoesNotWait()
        {
            var gate = new MinIntervalGate(TimeSpan.Zero);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < 5; i++) await gate.WaitTurnAsync();

            Assert.True(sw.ElapsedMilliseconds < 100, $"mất {sw.ElapsedMilliseconds} ms");
        }

        [Fact]
        public async Task SequentialCalls_AreSpacedByInterval()
        {
            var gate = new MinIntervalGate(Interval);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < 3; i++) await gate.WaitTurnAsync(); // lần 1 ngay; lần 2, 3 chờ

            Assert.True(sw.ElapsedMilliseconds >= 380, $"chỉ mất {sw.ElapsedMilliseconds} ms");
        }

        [Fact]
        public async Task ConcurrentCalls_AreQueued()
        {
            var gate = new MinIntervalGate(Interval);
            var sw = Stopwatch.StartNew();

            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => gate.WaitTurnAsync()));

            Assert.True(sw.ElapsedMilliseconds >= 380, $"chỉ mất {sw.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void NegativeInterval_TreatedAsZero()
        {
            Assert.Equal(TimeSpan.Zero, new MinIntervalGate(TimeSpan.FromSeconds(-1)).MinInterval);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<long> SentAtMs { get; } = new();
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                SentAtMs.Add(_clock.ElapsedMilliseconds);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        [Fact]
        public async Task Handler_WaitsForTurn_BeforeSending()
        {
            var inner = new RecordingHandler();
            using var client = new HttpClient(new RateLimitingHandler(new MinIntervalGate(Interval)) { InnerHandler = inner });

            await client.GetAsync("https://shop.vn/a.html");
            await client.GetAsync("https://shop.vn/b.html");

            Assert.Equal(2, inner.SentAtMs.Count);
            Assert.True(inner.SentAtMs[1] - inner.SentAtMs[0] >= 180, $"2 request cách nhau {inner.SentAtMs[1] - inner.SentAtMs[0]} ms");
        }
    }
}
