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
