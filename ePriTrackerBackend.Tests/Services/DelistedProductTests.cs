using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Notification;
using ePriTrackerBackend.Services.PriceCronJob;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    /// <summary>Sản phẩm bị gỡ khỏi sàn: 5 lần "không còn" cách đều 6 giờ, trải trên ít nhất 24 giờ → đánh dấu, ngưng cào, báo người theo dõi.</summary>
    public class DelistedProductTests : IDisposable
    {
        private static readonly DateTimeOffset T0 = new(2026, 11, 17, 8, 0, 0, TimeSpan.FromHours(7));

        private readonly TestDb _db = new();
        private readonly FakePlatformScraper _scraper = new();
        private readonly RecordingNotifications _notifications = new();

        public void Dispose() => _db.Dispose();

        private sealed class RecordingNotifications : INotificationService
        {
            public List<Guid> Delisted { get; } = new();
            public Task ProcessPriceDropNotificationsAsync(Product product, decimal newPrice) => Task.CompletedTask;
            public Task ProcessDelistedNotificationsAsync(Product product)
            {
                Delisted.Add(product.ProductId);
                return Task.CompletedTask;
            }
        }

        private PriceCrawlerWorker Job()
        {
            var ctx = _db.Context;
            return new PriceCrawlerWorker(new ProductRepository(ctx), new PriceHistoryRepository(ctx), new TrackedItemRepository(ctx), new EfUnitOfWork(ctx),
                new SingleScraperResolver(_scraper), _notifications, NullLogger<PriceCrawlerWorker>.Instance);
        }

        private Product Tracked(string id, decimal price = 1000)
        {
            var user = _db.Context.User.FirstOrDefault() ?? _db.AddUser("u@test.vn");
            var product = _db.AddProduct(FakePlatformScraper.Normalized(id), price: price);
            _db.Track(user, product);
            return product;
        }

        private Product Reload(Product p) => _db.Fresh().Product.Single(x => x.ProductId == p.ProductId);

        // ===== Quy tắc đếm (hàm thuần, đồng hồ cố định) =====

        [Fact]
        public void FiveChecks_SixHoursApart_DelistOnTheFifth()
        {
            var product = new Product();
            var now = T0;

            for (int i = 1; i <= 4; i++)
            {
                Assert.False(PriceCrawlerWorker.MarkNotFound(product, now));
                Assert.Equal(i, product.NotFoundCount);
                Assert.Equal(now + PriceCrawlerWorker.NotFoundRecheckInterval, product.NextCrawlAt); // hẹn lại sau 6 giờ
                now = product.NextCrawlAt!.Value;
            }

            Assert.Equal(T0.AddHours(24), now);                       // lần 5 đúng 24 giờ sau lần 1
            Assert.True(PriceCrawlerWorker.MarkNotFound(product, now));
            Assert.True(product.IsDelisted);
            Assert.Equal(now, product.DelistedAt);
            Assert.Null(product.NextCrawlAt);                          // ngưng cào
            Assert.Equal(T0, product.FirstNotFoundAt);
        }

        [Fact]
        public void FiveChecks_ButLessThan24Hours_NotYet()
        {
            var product = new Product { NotFoundCount = 4, FirstNotFoundAt = T0 };

            Assert.False(PriceCrawlerWorker.MarkNotFound(product, T0.AddHours(20)));
            Assert.False(product.IsDelisted);
            Assert.Equal(5, product.NotFoundCount);
            Assert.Equal(T0.AddHours(26), product.NextCrawlAt);

            Assert.True(PriceCrawlerWorker.MarkNotFound(product, T0.AddHours(26)));
        }

        // ===== Job giá =====

        [Fact]
        public async Task NotFound_CountsOnce_KeepsPrice_RechecksInSixHours()
        {
            var product = Tracked("1", 1000);
            _scraper.NotFoundUrls.Add(FakePlatformScraper.Normalized("1"));
            var before = DateTimeOffset.UtcNow;

            await Job().ProcessPendingProductsAsync();

            var p = Reload(product);
            Assert.Equal(1, p.NotFoundCount);
            Assert.NotNull(p.FirstNotFoundAt);
            Assert.False(p.IsDelisted);
            Assert.Equal(1000m, p.LatestPrice);
            Assert.True(p.IsInStock);                                  // không phải hết hàng
            Assert.InRange(p.NextCrawlAt!.Value - before, TimeSpan.FromHours(5.9), TimeSpan.FromHours(6.1));
            Assert.Empty(_notifications.Delisted);
        }

        [Fact]
        public async Task FifthNotFound_After24Hours_Delists_StopsCrawling_Notifies()
        {
            var product = Tracked("1");
            var stored = _db.Context.Product.Find(product.ProductId)!;
            stored.NotFoundCount = 4;
            stored.FirstNotFoundAt = DateTimeOffset.UtcNow.AddHours(-24.5);
            _db.Context.SaveChanges();
            _scraper.NotFoundUrls.Add(FakePlatformScraper.Normalized("1"));

            await Job().ProcessPendingProductsAsync();

            var p = Reload(product);
            Assert.True(p.IsDelisted);
            Assert.NotNull(p.DelistedAt);
            Assert.Null(p.NextCrawlAt);
            Assert.Equal(new[] { product.ProductId }, _notifications.Delisted);

            // Lượt sau không cào nữa
            _scraper.RequestedUrls.Clear();
            await Job().ProcessPendingProductsAsync();
            Assert.Empty(_scraper.RequestedUrls);
        }

        [Fact]
        public async Task DelistedProduct_NotPickedEvenIfDue()
        {
            var product = Tracked("1");
            var stored = _db.Context.Product.Find(product.ProductId)!;
            stored.IsDelisted = true;
            stored.NextCrawlAt = DateTimeOffset.UtcNow.AddMinutes(-5);  // dữ liệu lạ: vẫn có lịch
            _db.Context.SaveChanges();
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 1 };

            await Job().ProcessPendingProductsAsync();

            Assert.Empty(_scraper.RequestedUrls);
        }

        [Fact]
        public async Task SuccessfulScrape_ResetsCounter()
        {
            var product = Tracked("1", 1000);
            var stored = _db.Context.Product.Find(product.ProductId)!;
            stored.NotFoundCount = 3;
            stored.FirstNotFoundAt = DateTimeOffset.UtcNow.AddHours(-13);
            _db.Context.SaveChanges();
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 900 };

            await Job().ProcessPendingProductsAsync();

            var p = Reload(product);
            Assert.Equal(0, p.NotFoundCount);
            Assert.Null(p.FirstNotFoundAt);
            Assert.Equal(900m, p.LatestPrice);
            Assert.False(p.IsDelisted);
        }

        [Fact]
        public async Task OtherErrors_DoNotCount()
        {
            var product = Tracked("1");
            var stored = _db.Context.Product.Find(product.ProductId)!;
            stored.NotFoundCount = 2;
            stored.FirstNotFoundAt = DateTimeOffset.UtcNow.AddHours(-7);
            _db.Context.SaveChanges();
            // không có trong Products và không phải 404 → lỗi mạng ("Lấy dữ liệu thất bại.")

            var before = DateTimeOffset.UtcNow;
            await Job().ProcessPendingProductsAsync();

            var p = Reload(product);
            Assert.Equal(2, p.NotFoundCount);
            Assert.InRange(p.NextCrawlAt!.Value - before, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31)); // thử lại 30 phút như cũ
        }

        [Fact]
        public async Task TikiDiscontinued_CountsAsNotFound_AndMarksOutOfStock()
        {
            var product = Tracked("1", 1000);
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 1000, IsAvailable = false, IsDiscontinued = true };

            await Job().ProcessPendingProductsAsync();

            var p = Reload(product);
            Assert.Equal(1, p.NotFoundCount);
            Assert.False(p.IsInStock);
            Assert.False(p.IsDelisted);
        }
    }
}

namespace ePriTrackerBackend.Tests.Services
{
    using ePriTrackerBackend.Models.DTOs;
    using ePriTrackerBackend.Services.Products;

    /// <summary>Thêm lại sản phẩm đã đánh dấu "không còn trên sàn".</summary>
    public class DelistedRetrackTests : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly FakePlatformScraper _scraper = new();
        private readonly ProductTrackingService _tracking;
        private readonly Product _product;
        private static readonly string Link = FakePlatformScraper.Normalized("1");

        public DelistedRetrackTests()
        {
            var ctx = _db.Context;
            _tracking = new ProductTrackingService(
                new UserRepository(ctx), new ProductRepository(ctx), new TrackedItemRepository(ctx), new EventRepository(ctx), new PriceHistoryRepository(ctx),
                new SingleScraperResolver(_scraper), new EfUnitOfWork(ctx), NullLogger<ProductTrackingService>.Instance);

            _db.AddUser("new@test.vn");
            _product = _db.AddProduct(Link, price: 1000);
            _product.IsDelisted = true;
            _product.DelistedAt = DateTimeOffset.UtcNow.AddDays(-1);
            _product.NotFoundCount = 5;
            _product.FirstNotFoundAt = DateTimeOffset.UtcNow.AddDays(-2);
            _product.NextCrawlAt = null;
            _db.Context.SaveChanges();
        }

        public void Dispose() => _db.Dispose();

        [Fact]
        public async Task StillNotFound_Rejected_WithDelistedMessage()
        {
            _scraper.NotFoundUrls.Add(Link);

            var ex = await Assert.ThrowsAsync<Exception>(() => _tracking.AddProductAsync(Link, "new@test.vn"));

            Assert.Equal(ProductTrackingService.DelistedMessage, ex.Message);
            Assert.Empty(_db.Fresh().Item);
            Assert.True(_db.Fresh().Product.Single().IsDelisted);
        }

        [Fact]
        public async Task TikiStillDiscontinued_Rejected()
        {
            _scraper.Products[Link] = new ScrapedProduct { Price = 1000, IsAvailable = false, IsDiscontinued = true };

            var ex = await Assert.ThrowsAsync<Exception>(() => _tracking.AddProductAsync(Link, "new@test.vn"));
            Assert.Equal(ProductTrackingService.DelistedMessage, ex.Message);
        }

        [Fact]
        public async Task BackOnPlatform_Reactivated_AndTracked()
        {
            _scraper.Products[Link] = new ScrapedProduct { Name = "SP", Price = 900 };

            await _tracking.AddProductAsync(Link, "new@test.vn");

            var p = _db.Fresh().Product.Single();
            Assert.False(p.IsDelisted);
            Assert.Null(p.DelistedAt);
            Assert.Equal(0, p.NotFoundCount);
            Assert.NotNull(p.NextCrawlAt); // job giá cào lại ngay
            Assert.Single(_db.Fresh().Item);
        }

        [Fact]
        public async Task TrackDeal_FromLiveEvent_Reactivates()
        {
            var user = _db.Context.User.Single();

            await _tracking.TrackDealAsync(user.UserId, new TrackDealDto { ProductLink = Link, ProductName = "SP", CurrentPrice = 800 });

            var p = _db.Fresh().Product.Single();
            Assert.False(p.IsDelisted);
            Assert.NotNull(p.NextCrawlAt);
        }

        [Fact]
        public void Dto_ExposesDelistedFields()
        {
            var dto = TrackedProductDTO.From(_product, null);
            Assert.True(dto.IsDelisted);
            Assert.Equal(_product.DelistedAt, dto.DelistedAt);
        }
    }
}
