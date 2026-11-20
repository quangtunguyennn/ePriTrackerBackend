using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Admin;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    public class AdminEventServiceTests : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly FakeSource _source = new();
        private readonly AutoEventSyncStatusStore _status = new();

        private sealed class FakeSource : IAutoEventSource
        {
            public string Platform => "HACOM";
            public List<ScrapedEvent> Events { get; set; } = new();
            public Exception? Failure { get; set; }

            public Task<IReadOnlyList<ScrapedEvent>> GetLiveEventsAsync(CancellationToken cancellationToken = default)
                => Failure != null ? Task.FromException<IReadOnlyList<ScrapedEvent>>(Failure) : Task.FromResult<IReadOnlyList<ScrapedEvent>>(Events);
        }

        public void Dispose()
        {
            _cache.Dispose();
            _db.Dispose();
        }

        private AdminEventService Service()
        {
            var events = new EventRepository(_db.Context);
            var unitOfWork = new EfUnitOfWork(_db.Context);
            var sync = new AutoEventSyncService(new[] { _source }, events, unitOfWork, _cache, NullLogger<AutoEventSyncService>.Instance,
                status: _status);
            return new AdminEventService(new[] { _source }, sync, _status, events, unitOfWork, _cache);
        }

        private static ScrapedEvent FlashSale() => new()
        {
            Title = "Flash Sale Cuối Tuần",
            Url = "https://hacom.vn/deal#flash",
            EndsAt = DateTimeOffset.UtcNow.AddHours(5),
            Products = Enumerable.Range(1, 6).Select(i => new ScrapedProduct
            {
                ExternalId = $"sp{i}", Name = $"SP {i}", Price = 1000, ImageUrl = $"https://cdn/{i}.jpg", Url = $"https://hacom.vn/sp-{i}"
            }).ToList()
        };

        [Fact]
        public async Task SyncNow_PublishesEvents_RecordsStatus_AndShowsProductCount()
        {
            _source.Events = new() { FlashSale() };

            var result = await Service().SyncNowAsync("hacom"); // không phân biệt hoa thường

            Assert.Equal("HACOM", result.Platform);
            Assert.True(result.LastSync.Success);
            Assert.Equal(1, result.LastSync.EventsFound);
            Assert.Equal(1, result.LastSync.EventsShown);
            var evt = Assert.Single(result.Events);
            Assert.True(evt.IsPublished);
            Assert.Equal(6, evt.ProductCount);
        }

        [Fact]
        public async Task HiddenEvent_StaysHiddenAfterNextSync_UntilShownAgain()
        {
            _source.Events = new() { FlashSale() };
            var service = Service();
            var evt = Assert.Single((await service.SyncNowAsync("HACOM")).Events);

            var hidden = await service.SetHiddenAsync(evt.EventId, true);
            Assert.True(hidden.IsHiddenByAdmin);
            Assert.False(hidden.IsPublished);

            var afterSync = await service.SyncNowAsync("HACOM");
            Assert.False(Assert.Single(afterSync.Events).IsPublished);
            Assert.Equal(0, afterSync.LastSync.EventsShown);
            Assert.Empty(await new EventRepository(_db.Fresh()).GetPublishedNewestFirstAsync()); // người dùng không thấy

            var shown = await service.SetHiddenAsync(evt.EventId, false);
            Assert.True(shown.IsPublished);
            Assert.False(Assert.Single((await service.SyncNowAsync("HACOM")).Events).IsHiddenByAdmin);
        }

        [Fact]
        public async Task SyncFailure_KeepsEvents_AndRecordsError()
        {
            _source.Events = new() { FlashSale() };
            var service = Service();
            await service.SyncNowAsync("HACOM");

            _source.Failure = new HttpRequestException("HTTP 403");
            var result = await service.SyncNowAsync("HACOM");

            Assert.False(result.LastSync.Success);
            Assert.Equal("HTTP 403", result.LastSync.Error);
            Assert.Single(result.Events);
        }

        [Fact]
        public async Task UnknownPlatform_AndTikiEvent_AreRejected()
        {
            var tiki = _db.AddEvent("Tiki sale", "https://tiki.vn/khuyen-mai/x");

            Assert.Null(await Service().SyncNowAsync("Tiki"));
            Assert.Null(await Service().SetHiddenAsync(tiki.EventId, true));
            Assert.Null(await Service().SetHiddenAsync(Guid.NewGuid(), true));
        }

        [Fact]
        public async Task GetAutoEvents_ListsEveryAutoPlatform_EvenWithoutEvents()
        {
            var platforms = await Service().GetAutoEventsAsync();

            var hacom = Assert.Single(platforms);
            Assert.Equal("HACOM", hacom.Platform);
            Assert.Empty(hacom.Events);
            Assert.Null(hacom.LastSync);
        }
    }
}
