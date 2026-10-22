using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    public class EventServiceTests : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly FakeEventScraper _scraper = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly FakeAutoEventSync _autoSync;
        private readonly EventService _service;

        /// <summary>Giả lập job đồng bộ sự kiện tự đăng: ghi nhận lần gọi, có thể "đồng bộ" bằng cách điền cache.</summary>
        private sealed class FakeAutoEventSync : IAutoEventSyncService
        {
            private readonly IMemoryCache _cache;
            public FakeAutoEventSync(IMemoryCache cache) => _cache = cache;

            public List<string> SyncedPlatforms { get; } = new();
            public Dictionary<Guid, List<LiveEventProductDTO>> ProductsAfterSync { get; } = new();

            public Task SyncAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<bool> SyncPlatformAsync(string platform, CancellationToken cancellationToken = default)
            {
                SyncedPlatforms.Add(platform);
                foreach (var (eventId, products) in ProductsAfterSync) _cache.Set(EventService.LiveProductsCacheKey(eventId), products);
                return Task.FromResult(true);
            }
        }

        public EventServiceTests()
        {
            _autoSync = new FakeAutoEventSync(_cache);
            _service = new EventService(new EventRepository(_db.Context), _scraper, _autoSync, new EfUnitOfWork(_db.Context), _cache, NullLogger<EventService>.Instance);
        }

        public void Dispose()
        {
            _cache.Dispose();
            _db.Dispose();
        }

        [Fact]
        public async Task Preview_MergesWithDb_AndMarksExpired()
        {
            var publishedInDb = _db.AddEvent("Fonterra Chinh Hang", "https://tiki.vn/khuyen-mai/fonterra?old=1", published: true);
            var goneFromTiki = _db.AddEvent("Su Kien Cu", "https://tiki.vn/khuyen-mai/cu", published: true);
            _scraper.CurrentEvents.Add(new ScrapedEvent { ExternalId = 1, Title = "fonterra chinh hang ", Url = "https://tiki.vn/khuyen-mai/fonterra?new=2", ImageUrl = "a" });
            _scraper.CurrentEvents.Add(new ScrapedEvent { ExternalId = 2, Title = "Moi Toanh", Url = "https://tiki.vn/khuyen-mai/moi", ImageUrl = "b" });

            var preview = await _service.GetPreviewAsync();

            Assert.Equal(3, preview.Count);
            var matched = preview[0];
            Assert.Equal(publishedInDb.EventId, matched.EventId);  // khớp theo tiêu đề (bỏ hoa/thường, khoảng trắng)
            Assert.True(matched.IsPublished);
            Assert.False(matched.IsExpired);

            var brandNew = preview[1];
            Assert.Equal(Guid.Empty, brandNew.EventId);
            Assert.False(brandNew.IsPublished);

            var expired = preview[2];
            Assert.Equal(goneFromTiki.EventId, expired.EventId);
            Assert.True(expired.IsExpired);
        }

        [Fact]
        public async Task Publish_NewEvent_Created_ThenSameTitle_Republished()
        {
            var evt = new Event { Title = "Sale", EventLink = "https://tiki.vn/k/sale", ImageUrl = "x", TikiEventId = 5 };

            Assert.Equal(PublishEventResult.Created, await _service.PublishAsync(evt));
            await _service.TogglePublishAsync(evt.EventId); // gỡ bài
            Assert.Equal(PublishEventResult.Republished, await _service.PublishAsync(new Event { Title = "Sale", EventLink = "khac", ImageUrl = "x" }));

            var stored = Assert.Single(_db.Fresh().Event);
            Assert.True(stored.IsPublished);
        }

        [Fact]
        public async Task TogglePublish_FlipsState_OrNullWhenMissing()
        {
            var evt = _db.AddEvent("A", "https://tiki.vn/a", published: true);

            Assert.False(await _service.TogglePublishAsync(evt.EventId));
            Assert.True(await _service.TogglePublishAsync(evt.EventId));
            Assert.Null(await _service.TogglePublishAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task GetPublished_DeletesOlderThan14Days_ReturnsPublishedNewestFirst()
        {
            _db.AddEvent("Qua cu", "https://tiki.vn/old", published: true, createdAt: DateTime.UtcNow.AddDays(-15));
            var older = _db.AddEvent("Cu hon", "https://tiki.vn/b", published: true, createdAt: DateTime.UtcNow.AddDays(-3));
            var newer = _db.AddEvent("Moi hon", "https://tiki.vn/c", published: true, createdAt: DateTime.UtcNow.AddDays(-1));
            _db.AddEvent("An", "https://tiki.vn/d", published: false);

            var events = await _service.GetPublishedAsync();

            Assert.Equal(new[] { newer.EventId, older.EventId }, events.Select(e => e.EventId));
            Assert.Equal(3, _db.Fresh().Event.Count()); // sự kiện 15 ngày đã bị xoá cứng
        }

        [Fact]
        public async Task LiveProducts_ScrapesThenServesFromCache()
        {
            var evt = _db.AddEvent("A", "https://tiki.vn/a");
            _scraper.ProductsByEventUrl["https://tiki.vn/a"] = new() { new ScrapedProduct { ExternalId = "1", Name = "Sữa", Price = 90000 } };

            var first = await _service.GetLiveProductsAsync(evt.EventId);
            var second = await _service.GetLiveProductsAsync(evt.EventId);

            Assert.Equal(LiveProductsStatus.Ok, first.Status);
            Assert.Equal("Sữa", Assert.Single(first.Products).ProductName);
            Assert.Same(first.Products, second.Products);
            Assert.Equal(1, _scraper.ProductCalls); // lần 2 lấy từ cache
        }

        [Fact]
        public async Task LiveProducts_NotFound_And_NoProducts()
        {
            var evt = _db.AddEvent("A", "https://tiki.vn/het-han");

            Assert.Equal(LiveProductsStatus.EventNotFound, (await _service.GetLiveProductsAsync(Guid.NewGuid())).Status);
            Assert.Equal(LiveProductsStatus.NoProducts, (await _service.GetLiveProductsAsync(evt.EventId)).Status);
            Assert.False(_cache.TryGetValue(EventService.LiveProductsCacheKey(evt.EventId), out _)); // danh sách rỗng không được cache
        }

        [Fact]
        public async Task RefreshCache_OneEventFails_OthersStillCached_UnpublishedSkipped()
        {
            var ok = _db.AddEvent("OK", "https://tiki.vn/ok");
            var broken = _db.AddEvent("Loi", "https://tiki.vn/loi");
            var hidden = _db.AddEvent("An", "https://tiki.vn/an", published: false);
            _scraper.ProductsByEventUrl["https://tiki.vn/ok"] = new() { new ScrapedProduct { ExternalId = "1", Price = 1 } };
            _scraper.ProductsByEventUrl["https://tiki.vn/an"] = new() { new ScrapedProduct { ExternalId = "2", Price = 2 } };
            _scraper.FailingUrls.Add("https://tiki.vn/loi");

            await _service.RefreshAllLiveProductsCacheAsync();

            Assert.True(_cache.TryGetValue(EventService.LiveProductsCacheKey(ok.EventId), out List<LiveEventProductDTO>? cached));
            Assert.Single(cached!);
            Assert.False(_cache.TryGetValue(EventService.LiveProductsCacheKey(broken.EventId), out _));
            Assert.False(_cache.TryGetValue(EventService.LiveProductsCacheKey(hidden.EventId), out _));
        }

        [Fact]
        public async Task AdminFeatures_IgnoreAutoPublishedEvents()
        {
            var cps = _db.AddEvent("Flash sale", "https://cellphones.com.vn/#flash", platform: "CellphoneS", endsAt: DateTimeOffset.UtcNow.AddHours(1));
            var tiki = _db.AddEvent("Tiki sale", "https://tiki.vn/k/sale");

            var preview = await _service.GetPreviewAsync();          // Tiki không trả sự kiện nào
            var adminList = await _service.GetAllForAdminAsync();

            Assert.Equal(tiki.EventId, Assert.Single(preview).EventId); // CellphoneS không bị coi là "hết hạn trên Tiki"
            Assert.Equal(tiki.EventId, Assert.Single(adminList).EventId);
            Assert.Null(await _service.TogglePublishAsync(cps.EventId));
            Assert.False(await _service.DeleteAsync(cps.EventId));
            Assert.Equal(2, _db.Fresh().Event.Count());
        }

        [Fact]
        public async Task Publish_AlwaysAdminPlatform()
        {
            await _service.PublishAsync(new Event { Title = "Sale", EventLink = "https://tiki.vn/k/sale", ImageUrl = "x", Platform = "CellphoneS" });

            Assert.Equal("Tiki", Assert.Single(_db.Fresh().Event).Platform);
        }

        [Fact]
        public async Task GetPublished_HidesExpired_KeepsOldAutoEvents()
        {
            var running = _db.AddEvent("Dang chay", "https://cellphones.com.vn/#a", platform: "CellphoneS", endsAt: DateTimeOffset.UtcNow.AddMinutes(30));
            _db.AddEvent("Het gio", "https://cellphones.com.vn/#b", platform: "CellphoneS", endsAt: DateTimeOffset.UtcNow.AddMinutes(-1));
            // Sự kiện tự đăng tạo từ lâu nhưng vẫn được gia hạn → không bị luật "14 ngày" của Admin xoá
            var longRunning = _db.AddEvent("Deal soc", "https://cellphones.com.vn/#c", platform: "CellphoneS",
                createdAt: DateTime.UtcNow.AddDays(-20), endsAt: DateTimeOffset.UtcNow.AddHours(5));

            var events = await _service.GetPublishedAsync();

            Assert.Equal(new[] { running.EventId, longRunning.EventId }, events.Select(e => e.EventId));
            Assert.Equal(3, _db.Fresh().Event.Count()); // hết hạn chỉ bị ẩn – job đồng bộ mới xoá
        }

        [Fact]
        public async Task LiveProducts_AutoEvent_CacheMiss_SyncsPlatform_NotTikiScraper()
        {
            var evt = _db.AddEvent("Flash", "https://cellphones.com.vn/#flash", platform: "CellphoneS", endsAt: DateTimeOffset.UtcNow.AddHours(1));
            var missing = _db.AddEvent("Khac", "https://cellphones.com.vn/#khac", platform: "CellphoneS", endsAt: DateTimeOffset.UtcNow.AddHours(1));
            _autoSync.ProductsAfterSync[evt.EventId] = new() { new LiveEventProductDTO { ProductName = "Laptop" } };

            var ok = await _service.GetLiveProductsAsync(evt.EventId);
            var none = await _service.GetLiveProductsAsync(missing.EventId);

            Assert.Equal("Laptop", Assert.Single(ok.Products).ProductName);
            Assert.Equal(LiveProductsStatus.NoProducts, none.Status);
            Assert.All(_autoSync.SyncedPlatforms, p => Assert.Equal("CellphoneS", p));
            Assert.Equal(0, _scraper.ProductCalls);
        }

        [Fact]
        public async Task RefreshCache_SkipsAutoEvents()
        {
            _db.AddEvent("Flash", "https://cellphones.com.vn/#flash", platform: "CellphoneS", endsAt: DateTimeOffset.UtcNow.AddHours(1));

            await _service.RefreshAllLiveProductsCacheAsync();

            Assert.Equal(0, _scraper.ProductCalls);
        }

        [Fact]
        public async Task Delete_RemovesEvent_OrFalseWhenMissing()
        {
            var evt = _db.AddEvent("A", "https://tiki.vn/a");

            Assert.True(await _service.DeleteAsync(evt.EventId));
            Assert.False(await _service.DeleteAsync(evt.EventId));
            Assert.Empty(_db.Fresh().Event);
        }
    }
}
