using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class HybridFetcherTests
    {
        private sealed class FakeStrategy : IFetchStrategy
        {
            private readonly Func<JsonElement> _behavior;
            public FakeStrategy(string name, Func<JsonElement> behavior) { Name = name; _behavior = behavior; }

            public string Name { get; }
            public int Calls { get; private set; }
            public FetchOptions? LastOptions { get; private set; }

            public Task<JsonElement> FetchJsonAsync(string path, FetchOptions options, CancellationToken cancellationToken = default)
            {
                Calls++;
                LastOptions = options;
                return Task.FromResult(_behavior());
            }
        }

        private sealed class FakeMetrics : IScraperMetrics
        {
            public List<string> Successes { get; } = new();
            public int Failures { get; private set; }
            public void RecordSuccess(string strategyName) => Successes.Add(strategyName);
            public void RecordFailure() => Failures++;
            public List<ScrapeRecord> Records { get; } = new();
            public void Record(ScrapeRecord record) => Records.Add(record);
        }

        private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

        private static HybridFetcher Create(FakeMetrics metrics, params IFetchStrategy[] strategies)
            => new HybridFetcher(strategies, metrics, NullLogger<HybridFetcher>.Instance, "Tiki");

        [Fact]
        public async Task DetailedRecord_HasPlatform_Strategy_AndFallbackFlag()
        {
            var metrics = new FakeMetrics();
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => throw new HttpRequestException("403"));
            var browser = new FakeStrategy(FetchStrategyNames.Playwright, () => Json("{}"));

            await Create(metrics, http, browser).FetchJsonAsync("/api/v2/products/1");

            var record = Assert.Single(metrics.Records);
            Assert.Equal("Tiki", record.Platform);
            Assert.Equal(ScrapeOperations.Product, record.Operation);
            Assert.True(record.Success);
            Assert.Equal(FetchStrategyNames.Playwright, record.Strategy);
            Assert.True(record.UsedFallback);
            Assert.Equal("/api/v2/products/1", record.Target);
        }

        [Fact]
        public async Task DetailedRecord_OnFailure_KeepsErrorMessage()
        {
            var metrics = new FakeMetrics();
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => throw new HttpRequestException("HTTP 404"));

            await Assert.ThrowsAsync<HttpRequestException>(() => Create(metrics, http).FetchJsonAsync("/x"));

            var record = Assert.Single(metrics.Records);
            Assert.False(record.Success);
            Assert.Equal("HTTP 404", record.Error);
        }

        [Fact]
        public async Task FirstStrategySucceeds_ReturnsItsResult_AndSkipsFallback()
        {
            var metrics = new FakeMetrics();
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => Json("{\"price\":100}"));
            var browser = new FakeStrategy(FetchStrategyNames.Playwright, () => Json("{\"price\":999}"));

            var result = await Create(metrics, http, browser).FetchJsonAsync("/api/v2/products/1");

            Assert.Equal(100, result.GetProperty("price").GetInt32());
            Assert.Equal(0, browser.Calls);
            Assert.Equal(new[] { FetchStrategyNames.HttpClient }, metrics.Successes);
            Assert.Equal(0, metrics.Failures);
        }

        [Fact]
        public async Task FirstStrategyFails_FallsBackToSecond()
        {
            var metrics = new FakeMetrics();
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => throw new HttpRequestException("403"));
            var browser = new FakeStrategy(FetchStrategyNames.Playwright, () => Json("{\"price\":200}"));

            var result = await Create(metrics, http, browser).FetchJsonAsync("/api/v2/products/1");

            Assert.Equal(200, result.GetProperty("price").GetInt32());
            Assert.Equal(1, http.Calls);
            Assert.Equal(1, browser.Calls);
            Assert.Equal(new[] { FetchStrategyNames.Playwright }, metrics.Successes);
            Assert.Equal(0, metrics.Failures);
        }

        [Fact]
        public async Task AllStrategiesFail_RethrowsLastException_AndRecordsOneFailure()
        {
            var metrics = new FakeMetrics();
            var lastError = new Exception("Lấy dữ liệu Tiki thất bại.");
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => throw new HttpRequestException("403"));
            var browser = new FakeStrategy(FetchStrategyNames.Playwright, () => throw lastError);

            var thrown = await Assert.ThrowsAsync<Exception>(() => Create(metrics, http, browser).FetchJsonAsync("/x"));

            // Giữ nguyên lỗi gốc: ProductController trả ex.Message này cho FE
            Assert.Same(lastError, thrown);
            Assert.Empty(metrics.Successes);
            Assert.Equal(1, metrics.Failures);
        }

        [Theory]
        [InlineData(System.Net.HttpStatusCode.NotFound)]
        [InlineData(System.Net.HttpStatusCode.Gone)]
        public async Task NotFound_DoesNotFallBack_AndKeepsStatusCode(System.Net.HttpStatusCode status)
        {
            var metrics = new FakeMetrics();
            var notFound = new HttpRequestException("404", null, status);
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => throw notFound);
            var browser = new FakeStrategy(FetchStrategyNames.Playwright, () => Json("{}"));

            var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Create(metrics, http, browser).FetchJsonAsync("/api/v2/products/1"));

            Assert.Same(notFound, thrown);
            Assert.Equal(0, browser.Calls); // Playwright cũng chỉ nhận 404 sau ~18 giây
            Assert.Equal(1, metrics.Failures);
            Assert.False(Assert.Single(metrics.Records).Success);
        }

        [Fact]
        public async Task BlockedOrServerError_StillFallsBack()
        {
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => throw new HttpRequestException("403", null, System.Net.HttpStatusCode.Forbidden));
            var browser = new FakeStrategy(FetchStrategyNames.Playwright, () => Json("{\"price\":1}"));

            await Create(new FakeMetrics(), http, browser).FetchJsonAsync("/x");

            Assert.Equal(1, browser.Calls);
        }

        [Fact]
        public async Task PassesOptionsToEveryStrategy()
        {
            var options = new FetchOptions { Timeout = TimeSpan.FromSeconds(5) };
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => throw new TaskCanceledException());
            var browser = new FakeStrategy(FetchStrategyNames.Playwright, () => Json("{}"));

            await Create(new FakeMetrics(), http, browser).FetchJsonAsync("/x", options);

            Assert.Same(options, http.LastOptions);
            Assert.Same(options, browser.LastOptions);
        }

        [Fact]
        public async Task NullOptions_UsesDefault()
        {
            var http = new FakeStrategy(FetchStrategyNames.HttpClient, () => Json("{}"));

            await Create(new FakeMetrics(), http).FetchJsonAsync("/x");

            Assert.Same(FetchOptions.Default, http.LastOptions);
        }

        [Fact]
        public void NoStrategies_Throws()
        {
            Assert.Throws<ArgumentException>(() => Create(new FakeMetrics()));
        }
    }
}
