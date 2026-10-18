using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Tests.Helpers;

namespace ePriTrackerBackend.Tests.Services
{
    /// <summary>Thao tác dùng transaction + ExecuteDelete của job gợi ý.</summary>
    public class SuggestionRepositoryTests : IDisposable
    {
        private readonly TestDb _db = new();

        public void Dispose() => _db.Dispose();

        private static SuggestionProduct Suggestion(Guid productId, string name) => new()
        {
            SuggestionProductId = Guid.NewGuid(), ProductId = productId, ProductName = name, Price = 1, ImageURL = "", ProductLink = ""
        };

        [Fact]
        public async Task ReplaceForProducts_ReplacesOnlyGivenProducts()
        {
            var a = _db.AddProduct("https://fake.vn/a-p1.html");
            var b = _db.AddProduct("https://fake.vn/b-p2.html");
            var untouched = _db.AddProduct("https://fake.vn/c-p3.html");
            _db.Context.SuggestionProduct.AddRange(Suggestion(a.ProductId, "a-cu"), Suggestion(b.ProductId, "b-cu"), Suggestion(untouched.ProductId, "c-cu"));
            _db.Context.SaveChanges();

            // b không có gợi ý mới → gợi ý cũ của b vẫn bị xoá (giống job cũ)
            await new SuggestionRepository(_db.Context).ReplaceForProductsAsync(
                new[] { a.ProductId, b.ProductId },
                new[] { Suggestion(a.ProductId, "a-moi") });

            Assert.Equal(new[] { "a-moi", "c-cu" }, _db.Fresh().SuggestionProduct.Select(s => s.ProductName).OrderBy(n => n));
        }
    }
}
