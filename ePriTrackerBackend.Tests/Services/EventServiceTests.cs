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
        private readonly EventService _service;

        public EventServiceTests()
        {
            _service = new EventService(new EventRepository(_db.Context), _scraper, new EfUnitOfWork(_db.Context), _cache, NullLogger<EventService>.Instance);
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
        public async Task Delete_RemovesEvent_OrFalseWhenMissing()
        {
            var evt = _db.AddEvent("A", "https://tiki.vn/a");

            Assert.True(await _service.DeleteAsync(evt.EventId));
            Assert.False(await _service.DeleteAsync(evt.EventId));
            Assert.Empty(_db.Fresh().Event);
        }
    }
}
