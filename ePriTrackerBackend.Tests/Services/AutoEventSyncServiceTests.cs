using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Services
{
    public class AutoEventSyncServiceTests : IDisposable
    {
        // "Bây giờ" thật (làm tròn giây): MemoryCache hết hạn theo đồng hồ thật – mốc cố định trong quá khứ làm cache hết hạn ngay
        private static readonly DateTimeOffset Now = TruncateToSecond(DateTimeOffset.UtcNow);

        private static DateTimeOffset TruncateToSecond(DateTimeOffset t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

        private readonly TestDb _db = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly FakeSource _source = new();
        private readonly FixedTime _time = new(Now);

        private sealed class FixedTime : TimeProvider
        {
            public DateTimeOffset UtcNow { get; set; }
            public FixedTime(DateTimeOffset now) => UtcNow = now;
            public override DateTimeOffset GetUtcNow() => UtcNow;
        }

        private sealed class FakeSource : IAutoEventSource
        {
            public string Platform => "CellphoneS";
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

        private AutoEventSyncService Service()
            => new(new[] { _source }, new EventRepository(_db.Context), new EfUnitOfWork(_db.Context), _cache,
                NullLogger<AutoEventSyncService>.Instance, _time);

        private static List<ScrapedProduct> Products(int count, decimal price = 1000, decimal? original = 2000)
            => Enumerable.Range(1, count).Select(i => new ScrapedProduct
            {
                ExternalId = $"sp{i}", Name = $"Sản phẩm {i}", Price = price, OriginalPrice = original,
                ImageUrl = $"https://cdn/{i}.png", Url = $"https://cellphones.com.vn/sp{i}.html"
            }).ToList();

        private static ScrapedEvent Flash(DateTimeOffset? endsAt = null, int products = 5) => new()
        {
            Title = "FLASHSALE TỰU TRƯỜNG (20-22h 21/10)",
            Url = "https://cellphones.com.vn/#flashsale-tuu-truong-20261021-20",
            EndsAt = endsAt ?? Now.AddHours(2),
            Products = Products(products)
        };

        private static ScrapedEvent Deal() => new()
        {
            Title = "DEAL SỐC MỖI NGÀY",
            Url = "https://cellphones.com.vn/#deal-soc-moi-ngay",
            Products = Products(6)
        };

        [Fact]
        public async Task NewEvents_PublishedImmediately_WithProductsCachedUntilEnd()
        {
            _source.Events = new() { Flash(), Deal() };

            Assert.True(await Service().SyncPlatformAsync("CellphoneS"));

            var events = _db.Fresh().Event.OrderBy(e => e.Title).ToList();
            Assert.Equal(2, events.Count);
            Assert.All(events, e => { Assert.True(e.IsPublished); Assert.Equal("CellphoneS", e.Platform); });

            var deal = events[0];
            Assert.Equal(Now + AutoEventSyncService.RollingLifetime, deal.EndsAt); // không có giờ kết thúc → hạn cuốn chiếu
            Assert.False(deal.EndsAtIsExact);
            var flash = events[1];
            Assert.Equal(Now.AddHours(2), flash.EndsAt);
            Assert.True(flash.EndsAtIsExact);
            Assert.Equal("https://cdn/1.png", flash.ImageUrl); // ảnh sự kiện = ảnh sản phẩm đầu tiên
            Assert.Equal(new[] { "https://cdn/1.png", "https://cdn/2.png", "https://cdn/3.png", "https://cdn/4.png" }, flash.PreviewImageUrls); // 5 sản phẩm → lấy 4

            Assert.True(_cache.TryGetValue(EventService.LiveProductsCacheKey(flash.EventId), out List<LiveEventProductDTO>? cached));
            Assert.Equal(5, cached!.Count);
            Assert.Equal(2000m, cached[0].InitialPrice);
            Assert.Equal(1000m, cached[0].LatestPrice);
        }

        [Fact]
        public async Task SecondSync_UpdatesSameRow_ExtendsRollingDeadline()
        {
            _source.Events = new() { Deal() };
            await Service().SyncPlatformAsync("CellphoneS");
            var firstId = _db.Fresh().Event.Single().EventId;

            _time.UtcNow = Now.AddMinutes(15);
            await Service().SyncPlatformAsync("CellphoneS");

            var stored = _db.Fresh().Event.Single();
            Assert.Equal(firstId, stored.EventId); // không tạo bản trùng → link người dùng đang mở vẫn dùng được
            Assert.Equal(Now.AddMinutes(15) + AutoEventSyncService.RollingLifetime, stored.EndsAt);
        }

        [Fact]
        public async Task EventGoneFromSite_OrExpired_IsDeleted_AndUncached()
        {
            _source.Events = new() { Flash(), Deal() };
            await Service().SyncPlatformAsync("CellphoneS");
            var flashId = _db.Fresh().Event.Single(e => e.Title.StartsWith("FLASH")).EventId;

            // 22h: khung flash sale kết thúc – trang chủ chỉ còn Deal sốc
            _time.UtcNow = Now.AddHours(2);
            _source.Events = new() { Deal() };
            await Service().SyncPlatformAsync("CellphoneS");

            Assert.Equal("DEAL SỐC MỖI NGÀY", _db.Fresh().Event.Single().Title);
            Assert.False(_cache.TryGetValue(EventService.LiveProductsCacheKey(flashId), out _));
        }

        [Fact]
        public async Task RollingEventMissingFromOneRead_KeptUntilItExpires()
        {
            // Nguồn đọc deal thưa hơn (trang chủ 2 giờ/lần) – thiếu một lượt chưa chắc đã kết thúc
            _source.Events = new() { Deal() };
            await Service().SyncPlatformAsync("CellphoneS");

            _time.UtcNow = Now.AddMinutes(15);
            _source.Events = new() { Flash() };
            await Service().SyncPlatformAsync("CellphoneS");
            Assert.Contains(_db.Fresh().Event, e => e.Title == "DEAL SỐC MỖI NGÀY");

            _time.UtcNow = Now + AutoEventSyncService.RollingLifetime;
            await Service().SyncPlatformAsync("CellphoneS");
            Assert.DoesNotContain(_db.Fresh().Event, e => e.Title == "DEAL SỐC MỖI NGÀY");
        }

        [Fact]
        public async Task SourceFails_KeepsEverything()
        {
            _source.Events = new() { Flash(), Deal() };
            await Service().SyncPlatformAsync("CellphoneS");

            _source.Failure = new TimeoutException("Playwright timeout");
            Assert.False(await Service().SyncPlatformAsync("CellphoneS"));

            Assert.Equal(2, _db.Fresh().Event.Count());
        }

        [Fact]
        public async Task SuspiciousEvent_NotPublished_ButExistingOneKept()
        {
            _source.Events = new() { Deal() };
            await Service().SyncPlatformAsync("CellphoneS");

            // Lần sau đọc được khối nhưng chỉ 2 sản phẩm hợp lệ (trang tải thiếu?) → giữ bản cũ
            var broken = Deal();
            _source.Events = new() { new ScrapedEvent { Title = broken.Title, Url = broken.Url, Products = Products(2) } };
            await Service().SyncPlatformAsync("CellphoneS");

            Assert.Single(_db.Fresh().Event);
        }

        [Fact]
        public async Task InvalidProducts_AreDropped_TooFewMeansNotPublished()
        {
            var products = Products(3);
            products.Add(new ScrapedProduct { Name = "Giá 0", Price = 0, Url = "https://cellphones.com.vn/a.html" });
            products.Add(new ScrapedProduct { Name = "Gốc thấp hơn giá bán", Price = 5000, OriginalPrice = 100, Url = "https://cellphones.com.vn/b.html" });
            products.Add(new ScrapedProduct { Name = "Không link", Price = 5000 });
            _source.Events = new() { new ScrapedEvent { Title = "DEAL", Url = "https://cellphones.com.vn/#deal", Products = products } };

            await Service().SyncPlatformAsync("CellphoneS");

            Assert.Empty(_db.Fresh().Event);
        }

        [Fact]
        public async Task OtherPlatforms_AreUntouched()
        {
            var tiki = _db.AddEvent("Tiki sale", "https://tiki.vn/k/sale");
            _source.Events = new() { Deal() };

            await Service().SyncAllAsync();

            Assert.Contains(_db.Fresh().Event, e => e.EventId == tiki.EventId);
            Assert.False(await Service().SyncPlatformAsync("Tiki")); // Tiki không có nguồn tự đăng
        }
    }
}
