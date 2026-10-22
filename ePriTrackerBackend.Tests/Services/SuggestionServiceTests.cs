using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Suggestions;
using ePriTrackerBackend.Tests.Helpers;
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
            _service = new SuggestionService(new ProductRepository(ctx), new SuggestionRepository(ctx), _finder, new EfUnitOfWork(ctx), new SingleScraperResolver(new FakePlatformScraper()), NullLogger<SuggestionService>.Instance);
        }

        public void Dispose() => _db.Dispose();

        private static SuggestionProduct Suggestion(Product product, string name, decimal price) => new()
        {
            SuggestionProductId = Guid.NewGuid(), ProductId = product.ProductId, ProductName = name, Price = price, ImageURL = "i", ProductLink = "l"
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
