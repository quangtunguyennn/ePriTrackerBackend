using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Products;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    public class ProductTrackingServiceTests : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly FakePlatformScraper _scraper = new();
        private readonly ProductTrackingService _service;

        public ProductTrackingServiceTests()
        {
            var ctx = _db.Context;
            _service = new ProductTrackingService(
                new UserRepository(ctx), new ProductRepository(ctx), new TrackedItemRepository(ctx), new EventRepository(ctx),
                new SingleScraperResolver(_scraper), new EfUnitOfWork(ctx), NullLogger<ProductTrackingService>.Instance);

            _scraper.Products[FakePlatformScraper.Normalized("123")] = new ScrapedProduct
            {
                ExternalId = "123", Name = "iPhone 15", Price = 20000000, ImageUrl = "i.jpg", Description = "mô tả"
            };
        }

        public void Dispose() => _db.Dispose();

        [Fact]
        public async Task AddProduct_NewLink_ScrapesSavesNormalizedProduct_AndTracks()
        {
            var user = _db.AddUser("u@test.vn");

            await _service.AddProductAsync("https://fake.vn/iphone-15-p123.html?spid=9", "u@test.vn");

            using var fresh = _db.Fresh();
            var product = fresh.Product.Single();
            Assert.Equal("https://fake.vn/product-p123.html", product.ProductLink);
            Assert.Equal("iPhone 15", product.ProductName);
            Assert.Equal(20000000m, product.InitialPrice);
            Assert.Equal(20000000m, product.LatestPrice);
            Assert.Equal("mô tả", product.Description);
            Assert.Single(fresh.Item.Where(i => i.UserId == user.UserId && i.ProductId == product.ProductId));
            Assert.Equal(new[] { "https://fake.vn/product-p123.html" }, _scraper.RequestedUrls);
        }

        [Fact]
        public async Task AddProduct_SameProductTwice_NoDuplicateProductOrItem_AndNoSecondScrape()
        {
            _db.AddUser("u@test.vn");

            await _service.AddProductAsync("https://fake.vn/iphone-15-p123.html", "u@test.vn");
            await _service.AddProductAsync("https://fake.vn/khac-ten-p123.html?x=1", "u@test.vn");

            using var fresh = _db.Fresh();
            Assert.Single(fresh.Product);
            Assert.Single(fresh.Item);
            Assert.Single(_scraper.RequestedUrls);
        }

        [Fact]
        public async Task AddProduct_MissingScrapedFields_UsesLegacyDefaults()
        {
            _db.AddUser("u@test.vn");
            _scraper.Products[FakePlatformScraper.Normalized("7")] = new ScrapedProduct { ExternalId = "7" };

            await _service.AddProductAsync("https://fake.vn/x-p7.html", "u@test.vn");

            var product = _db.Fresh().Product.Single();
            Assert.Equal("Sản phẩm không xác định", product.ProductName);
            Assert.Equal(0m, product.InitialPrice);
            Assert.Equal("", product.ImageURL);
            Assert.False(product.IsInStock); // không có giá → tính là hết hàng, vẫn cho theo dõi
            Assert.NotNull(product.OutOfStockSince);
            Assert.Single(_db.Fresh().Item);
        }

        [Fact]
        public async Task AddProduct_OutOfStockWithPrice_IsTracked_KeepsShownPrice()
        {
            _db.AddUser("u@test.vn");
            _scraper.Products[FakePlatformScraper.Normalized("8")] = new ScrapedProduct { Name = "iPhone 15 Pro cũ", Price = 16390000, IsAvailable = false };

            await _service.AddProductAsync("https://fake.vn/iphone-p8.html", "u@test.vn");

            var product = _db.Fresh().Product.Single();
            Assert.False(product.IsInStock);
            Assert.Equal(16390000m, product.LatestPrice);
            Assert.Equal(16390000m, product.InitialPrice);
            Assert.Single(_db.Fresh().Item);
        }

        [Fact]
        public async Task AddProduct_InStock_HasNoOutOfStockDate()
        {
            _db.AddUser("u@test.vn");

            await _service.AddProductAsync("https://fake.vn/iphone-15-p123.html", "u@test.vn");

            var product = _db.Fresh().Product.Single();
            Assert.True(product.IsInStock);
            Assert.Null(product.OutOfStockSince);
        }

        [Theory]
        [InlineData("https://other.vn/x-p123.html")]   // sàn chưa hỗ trợ
        [InlineData("https://fake.vn/khuyen-mai")]     // không có product ID
        public async Task AddProduct_InvalidLink_ThrowsGenericMessage(string link)
        {
            _db.AddUser("u@test.vn");

            var ex = await Assert.ThrowsAsync<Exception>(() => _service.AddProductAsync(link, "u@test.vn"));
            // Câu chung cho mọi sàn (trước đây là "Link Tiki không hợp lệ..." – sai nghĩa khi có sàn thứ hai)
            Assert.Equal("Link sản phẩm không hợp lệ hoặc sàn chưa được hỗ trợ.", ex.Message);
        }

        [Fact]
        public async Task AddProduct_UnknownUser_ThrowsLegacyMessage()
        {
            var ex = await Assert.ThrowsAsync<Exception>(() => _service.AddProductAsync("https://fake.vn/x-p123.html", "ai@test.vn"));
            Assert.Equal("Không tìm thấy thông tin người dùng.", ex.Message);
        }

        [Fact]
        public async Task AddProduct_ScrapeFails_PropagatesError_AndSavesNothing()
        {
            _db.AddUser("u@test.vn");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _service.AddProductAsync("https://fake.vn/x-p999.html", "u@test.vn"));
            Assert.Equal("Lấy dữ liệu thất bại.", ex.Message);
            Assert.Empty(_db.Fresh().Product);
        }

        [Fact]
        public async Task AddProduct_WithCampaignLink_LinksProductToMatchingEvent()
        {
            _db.AddUser("u@test.vn");
            var evt = _db.AddEvent("Sale", "https://fake.vn/khuyen-mai/sale?itm_campaign=SALE_11");

            await _service.AddProductAsync("https://fake.vn/iphone-p123.html?itm_campaign=sale_11&src=x", "u@test.vn");

            using var fresh = _db.Fresh();
            var link = Assert.Single(fresh.EventProduct);
            Assert.Equal(evt.EventId, link.EventId);
            Assert.Equal(fresh.Product.Single().ProductId, link.ProductId);
        }

        [Fact]
        public async Task TrackDeal_NormalizesLink_UsesRegularPriceFromProductPage_ThenReportsAlreadyTracking()
        {
            var user = _db.AddUser("u@test.vn");
            // Giá flash sale 15tr – trang sản phẩm (giá bán thường) là 20tr
            var deal = new TrackDealDto { ProductLink = "https://fake.vn/iphone-p123.html?spid=1", ProductName = "iPhone", CurrentPrice = 15000000, ImageURL = "s.jpg" };

            Assert.Equal(TrackDealResult.Added, await _service.TrackDealAsync(user.UserId, deal));
            Assert.Equal(TrackDealResult.AlreadyTracking, await _service.TrackDealAsync(user.UserId, deal));

            using var fresh = _db.Fresh();
            var product = fresh.Product.Single();
            Assert.Equal(FakePlatformScraper.Normalized("123"), product.ProductLink); // cùng link với khi thêm bằng tay → không trùng
            Assert.Equal(20000000m, product.InitialPrice);
            Assert.Equal("iPhone 15", product.ProductName);
            Assert.Single(fresh.Item);
        }

        [Fact]
        public async Task TrackDeal_SameProductAddedManually_ReusesIt()
        {
            var user = _db.AddUser("u@test.vn");
            var other = _db.AddUser("o@test.vn");
            await _service.AddProductAsync("https://fake.vn/iphone-p123.html", "o@test.vn");

            await _service.TrackDealAsync(user.UserId, new TrackDealDto { ProductLink = "https://fake.vn/x-p123.html?itm=1", ProductName = "iPhone", CurrentPrice = 1 });

            using var fresh = _db.Fresh();
            Assert.Single(fresh.Product);
            Assert.Equal(2, fresh.Item.Count());
        }

        [Fact]
        public async Task TrackDeal_ScrapeFails_FallsBackToDealInfo()
        {
            var user = _db.AddUser("u@test.vn");
            var deal = new TrackDealDto { ProductLink = "https://fake.vn/sua-p5.html?spid=1", ProductName = "Sữa", CurrentPrice = 90000, ImageURL = "s.jpg" };

            Assert.Equal(TrackDealResult.Added, await _service.TrackDealAsync(user.UserId, deal)); // scraper giả không có p5 → lỗi

            var product = _db.Fresh().Product.Single();
            Assert.Equal(FakePlatformScraper.Normalized("5"), product.ProductLink);
            Assert.Equal("Sữa", product.ProductName);
            Assert.Equal(90000m, product.InitialPrice);
        }

        [Fact]
        public async Task TrackDeal_UnsupportedLink_Throws()
        {
            var user = _db.AddUser("u@test.vn");

            var ex = await Assert.ThrowsAsync<Exception>(() => _service.TrackDealAsync(user.UserId,
                new TrackDealDto { ProductLink = "https://shopee.vn/abc", ProductName = "x", CurrentPrice = 1 }));

            Assert.Equal(ProductTrackingService.InvalidLinkMessage, ex.Message);
            Assert.Empty(_db.Fresh().Product);
        }

        [Fact]
        public async Task GetTrackedProducts_OnlyUsersProducts_NewestFirst()
        {
            var user = _db.AddUser("u@test.vn");
            var other = _db.AddUser("o@test.vn");
            var older = _db.AddProduct("https://fake.vn/a-p1.html", addedAt: DateTime.UtcNow.AddDays(-2));
            var newer = _db.AddProduct("https://fake.vn/b-p2.html", addedAt: DateTime.UtcNow);
            var notMine = _db.AddProduct("https://fake.vn/c-p3.html");
            _db.Track(user, older);
            _db.Track(user, newer);
            _db.Track(other, notMine);

            var products = await _service.GetTrackedProductsAsync("u@test.vn");

            Assert.Equal(new[] { newer.ProductId, older.ProductId }, products.Select(p => p.ProductId));
            Assert.Empty(await _service.GetTrackedProductsAsync("ai@test.vn"));
        }

        [Fact]
        public async Task Untrack_RemovesItem_KeepsProduct()
        {
            var user = _db.AddUser("u@test.vn");
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Track(user, product);

            Assert.True(await _service.UntrackProductAsync(product.ProductId, "u@test.vn"));

            using var fresh = _db.Fresh();
            Assert.Empty(fresh.Item);
            Assert.Single(fresh.Product);
        }

        [Fact]
        public async Task Untrack_NotTracked_ThrowsLegacyMessage()
        {
            _db.AddUser("u@test.vn");

            var ex = await Assert.ThrowsAsync<Exception>(() => _service.UntrackProductAsync(Guid.NewGuid(), "u@test.vn"));
            Assert.Equal("Sản phẩm không tồn tại trong danh sách theo dõi.", ex.Message);
        }
    }
}
