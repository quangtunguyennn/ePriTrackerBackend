using System.Text.Json;
using ePriTrackerBackend.Scraping;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Configuration;
using ePriTrackerBackend.Scraping.Tiki;
using ePriTrackerBackend.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class ScraperMetricsServiceTests
    {
        private static JsonElement Snapshot(ScraperMetricsService metrics)
            => JsonSerializer.SerializeToElement(metrics.GetMetrics());

        [Fact]
        public void Empty_ReturnsZeroAndSameJsonShapeAsBefore()
        {
            var json = Snapshot(new ScraperMetricsService());

            // Shape cũ của GET /api/admin/metrics/scraper (FE trang Admin đọc các trường này)
            Assert.Equal(0, json.GetProperty("TotalRequests").GetInt32());
            Assert.Equal(0, json.GetProperty("HttpClientSuccess").GetInt32());
            Assert.Equal(0, json.GetProperty("PlaywrightSuccess").GetInt32());
            Assert.Equal(0, json.GetProperty("Failed").GetInt32());
            Assert.Equal("0%", json.GetProperty("HttpClientOptimizationRate").GetString());
            Assert.Equal(5, json.EnumerateObject().Count());
        }

        [Fact]
        public void CountsByStrategy_AndComputesRate()
        {
            var metrics = new ScraperMetricsService();
            metrics.RecordSuccess(FetchStrategyNames.HttpClient);
            metrics.RecordSuccess(FetchStrategyNames.HttpClient);
            metrics.RecordSuccess(FetchStrategyNames.Playwright);
            metrics.RecordFailure();

            var json = Snapshot(metrics);

            Assert.Equal(4, json.GetProperty("TotalRequests").GetInt32());
            Assert.Equal(2, json.GetProperty("HttpClientSuccess").GetInt32());
            Assert.Equal(1, json.GetProperty("PlaywrightSuccess").GetInt32());
            Assert.Equal(1, json.GetProperty("Failed").GetInt32());
            Assert.Equal("50%", json.GetProperty("HttpClientOptimizationRate").GetString());
        }

        [Fact]
        public async Task IsThreadSafe()
        {
            var metrics = new ScraperMetricsService();
            await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(() => metrics.RecordSuccess(FetchStrategyNames.HttpClient))));

            Assert.Equal(1000, Snapshot(metrics).GetProperty("HttpClientSuccess").GetInt32());
        }

        private sealed class ManualTime : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 3, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        [Fact]
        public void Record_GroupsByPlatformAndOperation_WithAverageDuration()
        {
            var time = new ManualTime();
            var metrics = new ScraperMetricsService(time);

            metrics.Record(new ScrapeRecord { Platform = "Tiki", Operation = ScrapeOperations.Product, Success = true, Duration = TimeSpan.FromMilliseconds(100) });
            metrics.Record(new ScrapeRecord { Platform = "Tiki", Operation = ScrapeOperations.Product, Success = true, UsedFallback = true, Duration = TimeSpan.FromMilliseconds(300) });
            time.Now = time.Now.AddMinutes(1);
            metrics.Record(new ScrapeRecord { Platform = "Tiki", Operation = ScrapeOperations.Product, Success = false, Duration = TimeSpan.FromMilliseconds(200), Target = "/p/1", Error = "HTTP 429" });
            metrics.Record(new ScrapeRecord { Platform = "HACOM", Operation = ScrapeOperations.Search, Success = true, Duration = TimeSpan.FromSeconds(4) });

            var snapshot = metrics.GetSnapshot();

            Assert.Equal(new DateTimeOffset(2026, 11, 14, 3, 0, 0, TimeSpan.Zero), snapshot.StartedAt);
            Assert.Equal(2, snapshot.Operations.Count);
            var tiki = snapshot.Operations.Single(o => o.Platform == "Tiki");
            Assert.Equal(3, tiki.Requests);
            Assert.Equal(2, tiki.Successes);
            Assert.Equal(1, tiki.Failures);
            Assert.Equal(1, tiki.FallbackSuccesses);
            Assert.Equal(200, tiki.AverageDurationMs);
            Assert.Equal(time.Now, tiki.LastFailureAt);

            var error = Assert.Single(snapshot.RecentErrors);
            Assert.Equal("Tiki", error.Platform);
            Assert.Equal("/p/1", error.Target);
            Assert.Equal("HTTP 429", error.Error);

            // Bộ đếm cũ (/api/admin/metrics/scraper) không bị ảnh hưởng
            Assert.Equal(0, Snapshot(metrics).GetProperty("TotalRequests").GetInt32());
        }

        [Fact]
        public void RecentErrors_KeepsNewestFifty_AndShortensLongText()
        {
            var metrics = new ScraperMetricsService();
            for (int i = 0; i < 60; i++)
                metrics.Record(new ScrapeRecord { Platform = "GearVN", Operation = ScrapeOperations.Product, Success = false, Target = $"/p/{i}", Error = new string('x', 1000) });

            var errors = metrics.GetSnapshot().RecentErrors;

            Assert.Equal(ScraperMetricsService.MaxRecentErrors, errors.Count);
            Assert.Equal("/p/59", errors[0].Target);
            Assert.True(errors[0].Error.Length <= 301);
            Assert.Equal(60, metrics.GetSnapshot().Operations.Single().Failures);
        }

        [Fact]
        public async Task AddScraping_WiresMetrics_TikiStrategy_AndResolver()
        {
            // TikiBrowserService chỉ có DisposeAsync → container phải dispose bất đồng bộ (giống host ASP.NET)
            await using var provider = new ServiceCollection()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()) // HacomScraper đọc cấu hình (như host thật)
                .AddLogging()
                .AddScraping()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            // Ghi ở HybridFetcher và đọc ở MetricsController phải là CÙNG một bộ đếm
            Assert.Same(provider.GetRequiredService<IScraperMetrics>(), provider.GetRequiredService<IScraperMetricsReader>());

            // Mỗi sàn có HybridFetcher riêng theo key
            Assert.NotNull(provider.GetRequiredKeyedService<IHybridFetcher>(TikiScraper.PlatformName));

            // Resolver chọn đúng TikiScraper, và TikiScraper cũng chính là nguồn sự kiện (một instance)
            Assert.True(provider.GetRequiredService<IPlatformScraperResolver>().TryResolve("https://tiki.vn/a-p1.html", out var scraper));
            Assert.IsType<TikiScraper>(scraper);
            Assert.Same(scraper, provider.GetRequiredService<IEventScraper>());

            // Tiki hỗ trợ tìm kiếm (nguồn gợi ý); thiếu cấu hình → không giới hạn tốc độ (giữ hành vi cũ)
            Assert.Contains(scraper, provider.GetRequiredService<IPlatformScraperResolver>().SearchablePlatforms);
            Assert.Equal(TimeSpan.Zero, provider.GetRequiredKeyedService<MinIntervalGate>(TikiScraper.PlatformName).MinInterval);
        }

        [Fact]
        public async Task AddScraping_ReadsRateLimitFromConfiguration()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scraping:Platforms:Tiki:MinIntervalMs"] = "1500"
            }).Build();

            await using var provider = new ServiceCollection()
                .AddSingleton<IConfiguration>(config)
                .AddLogging()
                .AddScraping()
                .BuildServiceProvider();

            Assert.Equal(TimeSpan.FromMilliseconds(1500), provider.GetRequiredKeyedService<MinIntervalGate>(TikiScraper.PlatformName).MinInterval);
        }
    }
}
