using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services;
using ePriTrackerBackend.Services.Admin;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Services
{
    public class AdminMonitoringServiceTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 14, 3, 0, 0, TimeSpan.Zero);

        private sealed class ManualTime : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class FakeJobs : IJobStatusReader
        {
            public bool Fail { get; set; }
            public IReadOnlyList<RecurringJobStatusDTO> GetRecurringJobs()
                => Fail ? throw new InvalidOperationException("JobStorage.Current chưa khởi tạo")
                        : new[] { new RecurringJobStatusDTO { Id = "adaptive-price-crawler-job", Cron = "* * * * *", LastState = "Succeeded" } };
            public JobCountsDTO GetJobCounts() => new() { Succeeded = 10, Failed = 2 };
        }

        private readonly TestDb _db = new();
        private readonly FakePlatformScraper _scraper = new();
        private readonly ScraperMetricsService _metrics = new(new ManualTime());
        private readonly FakeJobs _jobs = new();

        public void Dispose() => _db.Dispose();

        private readonly PlatformBlockTracker _blocks = new(new ManualTime());

        private AdminMonitoringService Create()
            => new(new AdminRepository(_db.Context), _metrics, _jobs, new SingleScraperResolver(_scraper), new[] { _scraper },
                NullLogger<AdminMonitoringService>.Instance, new ManualTime(), _blocks);

        private void SetCrawlState(Guid productId, DateTimeOffset? nextCrawlAt, DateTimeOffset? lastUpdatedAt, bool inStock = true)
        {
            var product = _db.Context.Product.Find(productId)!;
            product.NextCrawlAt = nextCrawlAt;
            product.LastUpdatedAt = lastUpdatedAt;
            product.IsInStock = inStock;
            _db.Context.SaveChanges();
        }

        [Fact]
        public async Task DataHealth_CountsOnlyTrackedProducts_ByPlatform()
        {
            var user = _db.AddUser("u@test.vn");
            var healthy = _db.AddProduct("https://fake.vn/a-p1.html");
            var overdue = _db.AddProduct("https://fake.vn/b-p2.html");
            var stale = _db.AddProduct("https://fake.vn/c-p3.html");
            var untracked = _db.AddProduct("https://fake.vn/d-p4.html");
            var otherSite = _db.AddProduct("https://shop-cu.vn/x");
            foreach (var p in new[] { healthy, overdue, stale, otherSite }) _db.Track(user, p);

            SetCrawlState(healthy.ProductId, Now.AddMinutes(30), Now.AddHours(-1));
            SetCrawlState(overdue.ProductId, Now.AddHours(-1), Now.AddHours(-3), inStock: false);
            SetCrawlState(stale.ProductId, Now.AddMinutes(5), Now.AddDays(-3));
            SetCrawlState(untracked.ProductId, Now.AddDays(-10), Now.AddDays(-10));

            var result = await Create().GetMonitoringAsync();

            var fake = result.Platforms.Single(p => p.Platform == "Fake");
            Assert.Equal(3, fake.TrackedProducts);
            Assert.Equal(1, fake.OverdueProducts);
            Assert.Equal(1, fake.StaleProducts);
            Assert.Equal(1, fake.OutOfStockProducts);

            // Link không còn sàn hỗ trợ vẫn được đếm, gom vào "Khác"
            Assert.Equal(1, result.Platforms.Single(p => p.Platform == AdminMonitoringService.UnknownPlatform).TrackedProducts);
        }

        [Fact]
        public async Task Stale_UsesLastSuccessfulCrawl_NotLastPriceChange()
        {
            var user = _db.AddUser("u@test.vn");
            var steadyPrice = _db.AddProduct("https://fake.vn/a-p1.html"); // giá đứng yên 10 ngày nhưng vừa cào được
            var failing = _db.AddProduct("https://fake.vn/b-p2.html");     // 3 ngày không cào được
            _db.Track(user, steadyPrice);
            _db.Track(user, failing);

            var a = _db.Context.Product.Find(steadyPrice.ProductId)!;
            a.LastUpdatedAt = Now.AddDays(-10);
            a.LastCheckedAt = Now.AddHours(-3);
            a.NextCrawlAt = Now.AddHours(10);
            var b = _db.Context.Product.Find(failing.ProductId)!;
            b.LastUpdatedAt = Now.AddDays(-10);
            b.LastCheckedAt = Now.AddDays(-3);
            b.NextCrawlAt = Now.AddMinutes(10);
            _db.Context.SaveChanges();

            var fake = (await Create().GetMonitoringAsync()).Platforms.Single(p => p.Platform == "Fake");

            Assert.Equal(1, fake.StaleProducts);
        }

        [Fact]
        public async Task DelistedProducts_CountedSeparately_NotOverdueOrStale()
        {
            var user = _db.AddUser("u@test.vn");
            var delisted = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Track(user, delisted);
            var stored = _db.Context.Product.Find(delisted.ProductId)!;
            stored.IsDelisted = true;
            stored.NextCrawlAt = null;
            stored.LastUpdatedAt = Now.AddDays(-10);
            stored.IsInStock = false;
            _db.Context.SaveChanges();

            var fake = (await Create().GetMonitoringAsync()).Platforms.Single(p => p.Platform == "Fake");

            Assert.Equal(1, fake.TrackedProducts);
            Assert.Equal(1, fake.DelistedProducts);
            Assert.Equal(0, fake.StaleProducts);
            Assert.Equal(0, fake.OverdueProducts);
            Assert.Equal(0, fake.OutOfStockProducts);
        }

        [Fact]
        public async Task BlockedPlatform_ShowsPauseAndBlockCount()
        {
            for (int i = 0; i < PlatformBlockTracker.BlocksToPause; i++) _blocks.RecordBlocked("Fake", "403 (Forbidden)");

            var fake = (await Create().GetMonitoringAsync()).Platforms.Single(p => p.Platform == "Fake");

            Assert.Equal(Now.Add(PlatformBlockTracker.FirstPause), fake.PausedUntil);
            Assert.Equal(PlatformBlockTracker.BlocksToPause, fake.ConsecutiveBlocks);
        }

        [Fact]
        public async Task HealthyPlatform_NoPause()
        {
            var fake = (await Create().GetMonitoringAsync()).Platforms.Single(p => p.Platform == "Fake");

            Assert.Null(fake.PausedUntil);
            Assert.Equal(0, fake.ConsecutiveBlocks);
        }

        [Fact]
        public async Task IncludesMetricsJobsAndErrors()
        {
            _metrics.Record(new ScrapeRecord { Platform = "Fake", Operation = ScrapeOperations.Search, Success = true });
            _metrics.Record(new ScrapeRecord { Platform = "Fake", Operation = ScrapeOperations.Product, Success = false, Error = "HTTP 403" });

            var result = await Create().GetMonitoringAsync();

            Assert.Equal(Now, result.MetricsSince);
            Assert.Equal(2, result.Platforms.Single(p => p.Platform == "Fake").Operations.Count);
            Assert.Equal("HTTP 403", Assert.Single(result.RecentErrors).Error);
            Assert.Equal("adaptive-price-crawler-job", Assert.Single(result.Jobs).Id);
            Assert.Equal(2, result.JobCounts.Failed);
            Assert.Null(result.JobsError);
        }

        [Fact]
        public async Task HangfireUnavailable_StillReturnsTheRest()
        {
            _jobs.Fail = true;

            var result = await Create().GetMonitoringAsync();

            Assert.Empty(result.Jobs);
            Assert.Contains("Hangfire", result.JobsError);
            Assert.Single(result.Platforms);
        }
    }
}
