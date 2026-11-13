using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Suggestions;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    public class SuggestionServiceTests : IDisposable
    {
        private sealed class FakeFinder : ISuggestionFinder
        {
            public Func<Product, List<SuggestionProduct>> Behavior { get; set; } = _ => new();
            public int Calls { get; private set; }

            public Task<List<SuggestionProduct>> FindAsync(Product product, FetchOptions? options = null, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(Behavior(product));
            }
        }

        private readonly TestDb _db = new();
        private readonly FakeFinder _finder = new();
        private readonly SuggestionService _service;

        public SuggestionServiceTests()
        {
            var ctx = _db.Context;
            _service = new SuggestionService(new ProductRepository(ctx), new SuggestionRepository(ctx), _finder, new EfUnitOfWork(ctx), new SingleScraperResolver(new FakePlatformScraper()),
                new ConfigurationBuilder().Build(), NullLogger<SuggestionService>.Instance); // không cấu hình → mặc định 30 phút
        }

        public void Dispose() => _db.Dispose();

        /// <param name="ageMinutes">Gợi ý được cào cách đây bao nhiêu phút (mặc định: vừa cào).</param>
        private static SuggestionProduct Suggestion(Product product, string name, decimal price, int ageMinutes = 0) => new()
        {
            SuggestionProductId = Guid.NewGuid(), ProductId = product.ProductId, ProductName = name, Price = price, ImageURL = "i", ProductLink = "l",
            LastUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes)
        };

        [Fact]
        public async Task Dto_HasPlatform_DerivedFromLink()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            var onFake = Suggestion(product, "A", 500);
            onFake.ProductLink = "https://fake.vn/b-p2.html";
            var unknown = Suggestion(product, "B", 600); // link "l" – không thuộc sàn nào
            _db.Context.SuggestionProduct.AddRange(onFake, unknown);
            _db.Context.SaveChanges();

            var result = await _service.GetSuggestionsAsync(product.ProductId);

            Assert.Equal("Fake", result.Single(s => s.ProductName == "A").Platform);
            Assert.Null(result.Single(s => s.ProductName == "B").Platform);
        }

        [Fact]
        public async Task Get_StoredSuggestions_ReturnedCheapestFirst_WithoutScraping()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Context.SuggestionProduct.AddRange(Suggestion(product, "B", 800), Suggestion(product, "A", 500));
            _db.Context.SaveChanges();

            var result = await _service.GetSuggestionsAsync(product.ProductId);

            Assert.Equal(new[] { "A", "B" }, result.Select(s => s.ProductName));
            Assert.Equal(0, _finder.Calls);
            Assert.All(result, s => Assert.Equal(TimeSpan.FromHours(7), s.LastUpdatedAt.Offset));
        }

        [Fact]
        public async Task Get_NoStoredSuggestions_LazyLoadsAndSaves()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _finder.Behavior = p => new() { Suggestion(p, "C", 700) };

            var result = await _service.GetSuggestionsAsync(product.ProductId);

            Assert.Equal("C", Assert.Single(result).ProductName);
            Assert.Single(_db.Fresh().SuggestionProduct);
        }

        [Fact]
        public async Task Get_FinderFails_ReturnsEmpty_InsteadOfError()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _finder.Behavior = _ => throw new HttpRequestException("403");

            Assert.Empty(await _service.GetSuggestionsAsync(product.ProductId));
            Assert.Empty(_db.Fresh().SuggestionProduct);
        }

        [Fact]
        public async Task Get_StaleSuggestions_ScrapedAgain_AndReplaced()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Context.SuggestionProduct.Add(Suggestion(product, "Cũ", 600, ageMinutes: 31));
            _db.Context.SaveChanges();
            _finder.Behavior = p => new() { Suggestion(p, "Mới", 550) };

            var result = await _service.GetSuggestionsAsync(product.ProductId);

            Assert.Equal("Mới", Assert.Single(result).ProductName);
            Assert.Equal("Mới", Assert.Single(_db.Fresh().SuggestionProduct).ProductName);
        }

        [Fact]
        public async Task Get_SuggestionsYoungerThan30Minutes_NotScrapedAgain()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Context.SuggestionProduct.Add(Suggestion(product, "Còn mới", 600, ageMinutes: 29));
            _db.Context.SaveChanges();

            var result = await _service.GetSuggestionsAsync(product.ProductId);

            Assert.Equal("Còn mới", Assert.Single(result).ProductName);
            Assert.Equal(0, _finder.Calls);
        }

        [Theory]
        [InlineData(false)] // lỗi mạng / bị chặn
        [InlineData(true)]  // cào được nhưng không ra gợi ý nào
        public async Task Get_StaleSuggestions_ScrapeFailsOrEmpty_KeepsOldOnes(bool emptyResult)
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Context.SuggestionProduct.Add(Suggestion(product, "Cũ", 600, ageMinutes: 120));
            _db.Context.SaveChanges();
            _finder.Behavior = emptyResult ? _ => new() : _ => throw new HttpRequestException("403");

            var result = await _service.GetSuggestionsAsync(product.ProductId);

            Assert.Equal("Cũ", Assert.Single(result).ProductName);
            Assert.Single(_db.Fresh().SuggestionProduct);
        }

        [Fact]
        public async Task Get_ParallelRequests_ScrapeOnlyOnce()
        {
            // React StrictMode (dev) gọi API 2 lần cùng lúc khi mở trang
            // (Request 1 dừng trong lúc cào – không đụng DB – để request 2 kịp tới và phải chờ khoá; SQLite test dùng chung một kết nối.)
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            using var scraping = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            _finder.Behavior = p => { scraping.Set(); release.Wait(TimeSpan.FromSeconds(5)); return new() { Suggestion(p, "Mới", 550) }; };
            var ctx2 = _db.Fresh();
            var second = new SuggestionService(new ProductRepository(ctx2), new SuggestionRepository(ctx2), _finder, new EfUnitOfWork(ctx2),
                new SingleScraperResolver(new FakePlatformScraper()), new ConfigurationBuilder().Build(), NullLogger<SuggestionService>.Instance);

            var first = Task.Run(() => _service.GetSuggestionsAsync(product.ProductId));
            Assert.True(scraping.Wait(TimeSpan.FromSeconds(5)));
            var secondCall = Task.Run(() => second.GetSuggestionsAsync(product.ProductId));
            await Task.Delay(200); // request 2 đọc DB (chưa có gợi ý) rồi đứng chờ khoá
            release.Set();
            var results = await Task.WhenAll(first, secondCall);

            Assert.Equal(1, _finder.Calls);
            Assert.All(results, r => Assert.Equal("Mới", Assert.Single(r).ProductName));
            Assert.Single(_db.Fresh().SuggestionProduct);
        }

        [Fact]
        public async Task Get_CooldownComesFromConfiguration()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Suggestions:RefreshCooldownMinutes"] = "0" }).Build();
            var ctx = _db.Context;
            var alwaysScrape = new SuggestionService(new ProductRepository(ctx), new SuggestionRepository(ctx), _finder, new EfUnitOfWork(ctx),
                new SingleScraperResolver(new FakePlatformScraper()), config, NullLogger<SuggestionService>.Instance);
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Context.SuggestionProduct.Add(Suggestion(product, "Vừa cào", 600));
            _db.Context.SaveChanges();
            _finder.Behavior = p => new() { Suggestion(p, "Mới", 550) };

            await alwaysScrape.GetSuggestionsAsync(product.ProductId);

            Assert.Equal(1, _finder.Calls);
        }

        [Fact]
        public async Task Get_UnknownProduct_ReturnsEmpty()
        {
            Assert.Empty(await _service.GetSuggestionsAsync(Guid.NewGuid()));
            Assert.Equal(0, _finder.Calls);
        }

        [Fact]
        public async Task Refresh_ReplacesOldSuggestions()
        {
            var product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Context.SuggestionProduct.Add(Suggestion(product, "Cũ", 600));
            _db.Context.SaveChanges();
            _finder.Behavior = p => new() { Suggestion(p, "Mới 2", 900), Suggestion(p, "Mới 1", 400) };

            var result = await _service.RefreshSuggestionsAsync(product.ProductId);

            Assert.Equal(new[] { "Mới 1", "Mới 2" }, result.Select(s => s.ProductName));
            Assert.Equal(new[] { "Mới 1", "Mới 2" }, _db.Fresh().SuggestionProduct.Select(s => s.ProductName).OrderBy(n => n));
        }

        [Fact]
        public async Task Refresh_UnknownProduct_ThrowsLegacyMessage()
        {
            var ex = await Assert.ThrowsAsync<Exception>(() => _service.RefreshSuggestionsAsync(Guid.NewGuid()));
            Assert.Equal("Không tìm thấy thông tin sản phẩm gốc.", ex.Message);
        }
    }
}
