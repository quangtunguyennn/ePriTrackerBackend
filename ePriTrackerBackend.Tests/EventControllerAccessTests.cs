using System.Reflection;
using System.Security.Claims;
using ePriTrackerBackend.Controllers;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests
{
    /// <summary>Khách chưa đăng nhập xem được sự kiện (chỉ đọc cache); theo dõi và các API Admin vẫn cần đăng nhập.</summary>
    public class EventControllerAccessTests : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly FakeEventScraper _scraper = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());

        private sealed class NoopAutoEventSync : IAutoEventSyncService
        {
            public Task SyncAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<bool> SyncPlatformAsync(string platform, CancellationToken cancellationToken = default) => Task.FromResult(true);
        }

        public void Dispose()
        {
            _cache.Dispose();
            _db.Dispose();
        }

        private EventController Controller(bool signedIn)
        {
            var service = new EventService(new EventRepository(_db.Context), _scraper, new NoopAutoEventSync(), new EfUnitOfWork(_db.Context), _cache, NullLogger<EventService>.Instance);
            var identity = signedIn
                ? new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "User") }, "Test")
                : new ClaimsIdentity(); // không có kiểu xác thực = chưa đăng nhập
            return new EventController(service, tracking: null)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
            };
        }

        private static MethodInfo Action(string name) => typeof(EventController).GetMethod(name)!;

        [Theory]
        [InlineData(nameof(EventController.GetPublishedEvents))]
        [InlineData(nameof(EventController.GetLiveEventProducts))]
        public void ViewingEvents_IsOpenToGuests(string action)
        {
            Assert.NotNull(Action(action).GetCustomAttribute<AllowAnonymousAttribute>());
        }

        [Fact]
        public void TrackDeal_And_AdminActions_StillRequireSignIn()
        {
            Assert.Equal("User", Action(nameof(EventController.TrackDeal)).GetCustomAttribute<AuthorizeAttribute>()?.Roles);

            var others = typeof(EventController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name is not (nameof(EventController.GetPublishedEvents) or nameof(EventController.GetLiveEventProducts) or nameof(EventController.TrackDeal)));
            Assert.All(others, m =>
            {
                Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>());
                Assert.Equal("Admin", m.GetCustomAttribute<AuthorizeAttribute>()?.Roles);
            });
        }

        [Fact]
        public async Task Guest_CacheMiss_Gets503_WithoutScraping()
        {
            var evt = _db.AddEvent("Sale", "https://tiki.vn/khuyen-mai/sale");
            _scraper.ProductsByEventUrl["https://tiki.vn/khuyen-mai/sale"] = new() { new ScrapedProduct { ExternalId = "1", Price = 1000 } };

            var result = Assert.IsType<ObjectResult>(await Controller(signedIn: false).GetLiveEventProducts(evt.EventId));

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.Equal(0, _scraper.ProductCalls);
        }

        [Fact]
        public async Task SignedIn_CacheMiss_ScrapesAsBefore_ThenGuestSeesCachedProducts()
        {
            var evt = _db.AddEvent("Sale", "https://tiki.vn/khuyen-mai/sale");
            _scraper.ProductsByEventUrl["https://tiki.vn/khuyen-mai/sale"] = new() { new ScrapedProduct { ExternalId = "1", Price = 1000 } };

            Assert.IsType<OkObjectResult>(await Controller(signedIn: true).GetLiveEventProducts(evt.EventId));
            Assert.IsType<OkObjectResult>(await Controller(signedIn: false).GetLiveEventProducts(evt.EventId));
            Assert.Equal(1, _scraper.ProductCalls);
        }
    }
}
