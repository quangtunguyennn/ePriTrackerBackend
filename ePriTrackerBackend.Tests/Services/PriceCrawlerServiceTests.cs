using ePriTrackerBackend.Models.Entities;
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

        private PriceCrawlerService Job()
        {
            var ctx = _db.Context;
            return new PriceCrawlerService(new ProductRepository(ctx), new PriceHistoryRepository(ctx), new TrackedItemRepository(ctx), new EfUnitOfWork(ctx),
                new SingleScraperResolver(_scraper), NullLogger<PriceCrawlerService>.Instance);
        }

        /// <summary>Một sản phẩm đang được theo dõi (link sàn giả "fake.vn").</summary>
        private Product Tracked(string id, decimal price, bool inStock = true)
        {
            var user = _db.Context.User.FirstOrDefault() ?? _db.AddUser("u@test.vn");
            var product = _db.AddProduct(FakePlatformScraper.Normalized(id), price: price);
            if (!inStock)
            {
                product.IsInStock = false;
                product.OutOfStockSince = DateTimeOffset.UtcNow.AddDays(-3);
                _db.Context.SaveChanges();
            }
            _db.Track(user, product);
            return product;
        }

        private Product Reload(Product p) => _db.Fresh().Product.Single(x => x.ProductId == p.ProductId);

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

            await Job().UpdateAllTrackedProductPricesAsync();

            using var fresh = _db.Fresh();
            Assert.Equal(900m, fresh.Product.Single(p => p.ProductId == changed.ProductId).LatestPrice);
            Assert.Equal(1000m, fresh.Item.Single(i => i.ProductId == changed.ProductId).InitialPrice); // job không đổi giá ban đầu
            Assert.Equal(50m, fresh.Product.Single(p => p.ProductId == untracked.ProductId).LatestPrice);
            Assert.All(fresh.Product, p => Assert.True(p.IsInStock)); // lỗi mạng / sàn lạ không bị coi là hết hàng

            var history = Assert.Single(fresh.PriceHistory);
            Assert.Equal(changed.ProductId, history.ProductId);
            Assert.Equal(900m, history.Price);
            Assert.DoesNotContain(FakePlatformScraper.Normalized("5"), _scraper.RequestedUrls);
        }

        [Theory]
        [InlineData(false, 16390000)] // sàn báo hết hàng nhưng vẫn hiện giá
        [InlineData(null, null)]      // sàn không có giá (CellphoneS trả 0 → null)
        [InlineData(false, null)]
        public async Task OutOfStock_KeepsLatestPrice_FlagsProduct_NoHistory(bool? available, int? price)
        {
            var product = Tracked("1", 20000000);
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = price, IsAvailable = available };
            var before = DateTimeOffset.UtcNow.AddSeconds(-1);

            await Job().UpdateAllTrackedProductPricesAsync();

            var stored = Reload(product);
            Assert.False(stored.IsInStock);
            Assert.True(stored.OutOfStockSince >= before);
            Assert.Equal(20000000m, stored.LatestPrice); // giữ giá gần nhất
            Assert.Empty(_db.Fresh().PriceHistory);
            Assert.Single(_db.Fresh().Item);             // không tự bỏ theo dõi
        }

        [Fact]
        public async Task StillOutOfStock_KeepsOriginalOutOfStockSince()
        {
            var product = Tracked("1", 1000, inStock: false);
            var since = Reload(product).OutOfStockSince;
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 900, IsAvailable = false };

            await Job().UpdateAllTrackedProductPricesAsync();

            var stored = Reload(product);
            Assert.False(stored.IsInStock);
            Assert.Equal(since, stored.OutOfStockSince);
            Assert.Equal(1000m, stored.LatestPrice);
        }

        [Fact]
        public async Task BackInStock_ClearsFlag_AndUpdatesPrice()
        {
            var product = Tracked("1", 1000, inStock: false);
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 950, IsAvailable = true };

            await Job().UpdateAllTrackedProductPricesAsync();

            var stored = Reload(product);
            Assert.True(stored.IsInStock);
            Assert.Null(stored.OutOfStockSince);
            Assert.Equal(950m, stored.LatestPrice);
            Assert.Equal(950m, Assert.Single(_db.Fresh().PriceHistory).Price);
        }

        [Fact]
        public async Task BackInStock_SamePrice_ClearsFlag_WithoutHistory()
        {
            var product = Tracked("1", 1000, inStock: false);
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 1000 };

            await Job().UpdateAllTrackedProductPricesAsync();

            Assert.True(Reload(product).IsInStock);
            Assert.Empty(_db.Fresh().PriceHistory);
        }

        [Fact]
        public async Task NewDescription_IsSanitizedAndSaved_WithoutPriceHistory()
        {
            var product = Tracked("1", 1000);
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct
            {
                Price = 1000,
                Description = "<p style=\"color:red\">Bài viết đầy đủ</p><script>alert(1)</script>"
            };

            await Job().UpdateAllTrackedProductPricesAsync();

            Assert.Equal("<p>Bài viết đầy đủ</p>", Reload(product).Description);
            Assert.Empty(_db.Fresh().PriceHistory);
        }

        [Fact]
        public async Task MissingDescription_KeepsStoredOne()
        {
            var product = Tracked("1", 1000);
            product.Description = "<p>Mô tả cũ</p>";
            _db.Context.SaveChanges();
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 1000 };

            await Job().UpdateAllTrackedProductPricesAsync();

            Assert.Equal("<p>Mô tả cũ</p>", Reload(product).Description);
        }

        /// <summary>Sàn giả có lấy giá theo lô; sản phẩm lẻ vẫn qua <see cref="FakePlatformScraper"/>.</summary>
        private sealed class FakeBatchScraper : IBatchPriceScraper
        {
            private readonly FakePlatformScraper _inner;
            public FakeBatchScraper(FakePlatformScraper inner) => _inner = inner;

            public Dictionary<string, BatchPriceResult> Prices { get; } = new();
            public Exception? Failure { get; set; }
            public List<BatchPriceRequest> Requests { get; } = new();

            public string Platform => _inner.Platform;
            public bool CanHandle(string productUrl) => _inner.CanHandle(productUrl);
            public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl) => _inner.TryNormalizeProductUrl(productUrl, out normalizedUrl);
            public Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default) => _inner.GetProductAsync(productUrl, cancellationToken);

            public Task<IReadOnlyDictionary<string, BatchPriceResult>> GetCurrentPricesAsync(IReadOnlyList<BatchPriceRequest> items, CancellationToken cancellationToken = default)
            {
                Requests.AddRange(items);
                if (Failure != null) throw Failure;
                return Task.FromResult<IReadOnlyDictionary<string, BatchPriceResult>>(
                    items.Where(i => Prices.ContainsKey(i.ProductUrl)).ToDictionary(i => i.ProductUrl, i => Prices[i.ProductUrl]));
            }
        }

        private PriceCrawlerService Job(IPlatformScraper scraper)
        {
            var ctx = _db.Context;
            return new PriceCrawlerService(new ProductRepository(ctx), new PriceHistoryRepository(ctx), new TrackedItemRepository(ctx), new EfUnitOfWork(ctx),
                new SingleScraperResolver(scraper), NullLogger<PriceCrawlerService>.Instance);
        }

        [Fact]
        public async Task BatchPrices_UsedFirst_MissingOnesFetchedSingly_PlatformIdSaved()
        {
            var inBatch = Tracked("1", 1000);
            var notInBatch = Tracked("2", 500);
            var batch = new FakeBatchScraper(_scraper);
            batch.Prices[FakePlatformScraper.Normalized("1")] = new BatchPriceResult { Product = new ScrapedProduct { Price = 900 }, PlatformProductId = "131954" };
            _scraper.Products[FakePlatformScraper.Normalized("2")] = new ScrapedProduct { Price = 450 };

            await Job(batch).UpdateAllTrackedProductPricesAsync();

            var first = Reload(inBatch);
            Assert.Equal(900m, first.LatestPrice);
            Assert.Equal("131954", first.PlatformProductId); // lần sau khỏi tra mã
            Assert.Equal(450m, Reload(notInBatch).LatestPrice);
            Assert.Equal(new[] { FakePlatformScraper.Normalized("2") }, _scraper.RequestedUrls); // chỉ sản phẩm thiếu mới cào lẻ
            Assert.Equal(2, _db.Fresh().PriceHistory.Count());
        }

        [Fact]
        public async Task BatchPrices_SendsStoredPlatformId()
        {
            var product = Tracked("1", 1000);
            product.PlatformProductId = "42";
            _db.Context.SaveChanges();
            var batch = new FakeBatchScraper(_scraper);
            batch.Prices[FakePlatformScraper.Normalized("1")] = new BatchPriceResult { Product = new ScrapedProduct { Price = 1000 }, PlatformProductId = "42" };

            await Job(batch).UpdateAllTrackedProductPricesAsync();

            Assert.Equal("42", Assert.Single(batch.Requests).PlatformProductId);
        }

        [Fact]
        public async Task BatchFails_FallsBackToSingleFetch()
        {
            var product = Tracked("1", 1000);
            var batch = new FakeBatchScraper(_scraper) { Failure = new HttpRequestException("API đổi") };
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 800 };

            await Job(batch).UpdateAllTrackedProductPricesAsync();

            Assert.Equal(800m, Reload(product).LatestPrice);
            Assert.Null(Reload(product).PlatformProductId);
        }

        [Fact]
        public async Task BatchResultWithoutDescription_KeepsStoredDescription()
        {
            var product = Tracked("1", 1000);
            product.Description = "<p>Mô tả đầy đủ</p>";
            _db.Context.SaveChanges();
            var batch = new FakeBatchScraper(_scraper);
            batch.Prices[FakePlatformScraper.Normalized("1")] = new BatchPriceResult { Product = new ScrapedProduct { Price = 990 }, PlatformProductId = "1" };

            await Job(batch).UpdateAllTrackedProductPricesAsync();

            Assert.Equal("<p>Mô tả đầy đủ</p>", Reload(product).Description);
        }

        [Fact]
        public async Task ProductAddedWithoutPrice_FirstRealPriceBecomesInitialPrice()
        {
            var product = Tracked("1", 0, inStock: false);
            _scraper.Products[FakePlatformScraper.Normalized("1")] = new ScrapedProduct { Price = 4890000, IsAvailable = true };

            await Job().UpdateAllTrackedProductPricesAsync();

            var stored = Reload(product);
            Assert.Equal(4890000m, _db.Fresh().Item.Single(i => i.ProductId == product.ProductId).InitialPrice);
            Assert.Equal(4890000m, stored.LatestPrice);
            Assert.True(stored.IsInStock);
        }
    }
}
