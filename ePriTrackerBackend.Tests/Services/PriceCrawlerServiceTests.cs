using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    public class PriceCrawlerServiceTests : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly FakePlatformScraper _scraper = new();

        public void Dispose() => _db.Dispose();

        [Fact]
        public async Task UpdatesOnlyChangedPrices_AddsHistory_SkipsUntrackedAndFailed()
        {
            var user = _db.AddUser("u@test.vn");
            var changed = _db.AddProduct(FakePlatformScraper.Normalized("1"), price: 1000);
            var same = _db.AddProduct(FakePlatformScraper.Normalized("2"), price: 500);
            var failing = _db.AddProduct(FakePlatformScraper.Normalized("3"), price: 300);   // scraper không có → lỗi
            var otherPlatform = _db.AddProduct("https://other.vn/x-p4.html", price: 100);    // sàn chưa hỗ trợ
            var untracked = _db.AddProduct(FakePlatformScraper.Normalized("5"), price: 50);  // không ai theo dõi
            foreach (var p in new[] { changed, same, failing, otherPlatform }) _db.Track(user, p);

            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 900 };
            _scraper.Products[FakePlatformScraper.Normalized("2")] = new ScrapedProduct { Price = 500 };
            _scraper.Products[FakePlatformScraper.Normalized("5")] = new ScrapedProduct { Price = 1 };

            var ctx = _db.Context;
            var job = new PriceCrawlerService(new ProductRepository(ctx), new PriceHistoryRepository(ctx), new EfUnitOfWork(ctx),
                new SingleScraperResolver(_scraper), NullLogger<PriceCrawlerService>.Instance);

            await job.UpdateAllTrackedProductPricesAsync();

            using var fresh = _db.Fresh();
            Assert.Equal(900m, fresh.Product.Single(p => p.ProductId == changed.ProductId).LatestPrice);
            Assert.Equal(1000m, fresh.Product.Single(p => p.ProductId == changed.ProductId).InitialPrice);
            Assert.Equal(50m, fresh.Product.Single(p => p.ProductId == untracked.ProductId).LatestPrice);

            var history = Assert.Single(fresh.PriceHistory);
            Assert.Equal(changed.ProductId, history.ProductId);
            Assert.Equal(900m, history.Price);
            Assert.DoesNotContain(FakePlatformScraper.Normalized("5"), _scraper.RequestedUrls);
        }
    }
}
