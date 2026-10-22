using System.Security.Claims;
using ePriTrackerBackend.Controllers;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Products;
using ePriTrackerBackend.Services.Suggestions;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    /// <summary>
    /// Bug 4 (đã sửa): user chỉ được xem chi tiết / gợi ý / ép cào gợi ý cho sản phẩm mình theo dõi; Admin xem được tất cả.
    /// </summary>
    public class ProductAccessTests : IDisposable
    {
        private sealed class CountingFinder : ISuggestionFinder
        {
            public int Calls { get; private set; }
            public Task<List<SuggestionProduct>> FindAsync(Product product, FetchOptions? options = null, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new List<SuggestionProduct>());
            }
        }

        private readonly TestDb _db = new();
        private readonly CountingFinder _finder = new();
        private readonly ProductTrackingService _tracking;
        private readonly User _owner;
        private readonly User _stranger;
        private readonly Product _product;

        public ProductAccessTests()
        {
            var ctx = _db.Context;
            _tracking = new ProductTrackingService(
                new UserRepository(ctx), new ProductRepository(ctx), new TrackedItemRepository(ctx), new EventRepository(ctx),
                new SingleScraperResolver(new FakePlatformScraper()), new EfUnitOfWork(ctx), NullLogger<ProductTrackingService>.Instance);

            _owner = _db.AddUser("owner@test.vn");
            _stranger = _db.AddUser("stranger@test.vn");
            _product = _db.AddProduct("https://fake.vn/a-p1.html");
            _db.Track(_owner, _product);
        }

        public void Dispose() => _db.Dispose();

        [Theory]
        [InlineData("owner@test.vn", false, true)]
        [InlineData("stranger@test.vn", false, false)]
        [InlineData("stranger@test.vn", true, true)]    // Admin
        [InlineData("khong-ton-tai@test.vn", false, false)]
        public async Task CanAccessProduct(string email, bool isAdmin, bool expected)
        {
            Assert.Equal(expected, await _tracking.CanAccessProductAsync(_product.ProductId, email, isAdmin));
        }

        private ProductController ControllerFor(string email, params string[] roles)
        {
            var suggestions = new SuggestionService(new ProductRepository(_db.Context), new SuggestionRepository(_db.Context), _finder, new EfUnitOfWork(_db.Context), new SingleScraperResolver(new FakePlatformScraper()), NullLogger<SuggestionService>.Instance);
            var claims = new List<Claim> { new(ClaimTypes.Name, email) };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            return new ProductController(_tracking, suggestions)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
                }
            };
        }

        [Fact]
        public async Task Stranger_Gets404_OnAllThreeEndpoints_AndNoScrapingHappens()
        {
            var controller = ControllerFor("stranger@test.vn", "User");

            Assert.IsType<NotFoundObjectResult>((await controller.GetById(_product.ProductId)).Result);
            Assert.IsType<NotFoundObjectResult>(await controller.GetAllBetterProducts(_product.ProductId));
            Assert.IsType<NotFoundObjectResult>(await controller.RefreshSuggestions(_product.ProductId));
            Assert.Equal(0, _finder.Calls); // không bị lợi dụng để ép server cào Tiki
        }

        [Fact]
        public async Task Owner_And_Admin_CanAccess()
        {
            var owner = ControllerFor("owner@test.vn", "User");
            Assert.Equal(_product.ProductId, (await owner.GetById(_product.ProductId)).Value!.ProductId);
            Assert.IsType<OkObjectResult>(await owner.RefreshSuggestions(_product.ProductId));

            var admin = ControllerFor("stranger@test.vn", "Admin");
            Assert.Equal(_product.ProductId, (await admin.GetById(_product.ProductId)).Value!.ProductId);
        }
    }
}
