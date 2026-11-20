using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Models.Mappings;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace ePriTrackerBackend.Services.Events
{
    /// <summary>
    /// Đồng bộ sự kiện của các sàn TỰ ĐĂNG (<see cref="IAutoEventSource"/>, vd CellphoneS) – không qua Admin:
    /// sự kiện đang chạy trên sàn → đăng ngay lên trang người dùng; hết hạn / biến mất khỏi sàn → xoá.
    /// </summary>
    public interface IAutoEventSyncService
    {
        /// <summary>Đồng bộ mọi sàn tự đăng (job Hangfire "sync-auto-events-job").</summary>
        Task SyncAllAsync(CancellationToken cancellationToken = default);

        /// <returns>false nếu sàn không có nguồn tự đăng hoặc không đọc được trang (khi đó không xoá gì).</returns>
        Task<bool> SyncPlatformAsync(string platform, CancellationToken cancellationToken = default);
    }

    public class AutoEventSyncService : IAutoEventSyncService
    {
        /// <summary>Sự kiện sàn không cho biết giờ kết thúc (vd "Deal sốc mỗi ngày") sống thêm chừng này sau mỗi lần thấy trên sàn.
        /// Job ngừng chạy / sàn đổi giao diện → sự kiện tự hết hạn thay vì treo mãi.</summary>
        public static readonly TimeSpan RollingLifetime = TimeSpan.FromHours(6);

        /// <summary>Ít hơn chừng này sản phẩm hợp lệ → nghi đọc sai trang, không đăng (giữ bản cũ nếu có).</summary>
        public const int MinProducts = 4;

        /// <summary>Số ảnh sản phẩm ghép thành ảnh đại diện (lưới 2×2).</summary>
        public const int PreviewImageCount = 4;

        // Job Hangfire và lượt đồng bộ "lười" (server vừa khởi động) có thể chạy cùng lúc → tránh thêm trùng sự kiện
        private static readonly SemaphoreSlim SyncLock = new(1, 1);

        private readonly IEnumerable<IAutoEventSource> _sources;
        private readonly IEventRepository _events;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IMemoryCache _cache;
        private readonly TimeProvider _time;
        private readonly ILogger<AutoEventSyncService> _logger;
        private readonly IScraperMetrics _metrics;
        private readonly IAutoEventSyncStatusStore _status;

        public AutoEventSyncService(
            IEnumerable<IAutoEventSource> sources,
            IEventRepository events,
            IUnitOfWork unitOfWork,
            IMemoryCache cache,
            ILogger<AutoEventSyncService> logger,
            TimeProvider timeProvider = null,
            IScraperMetrics metrics = null,
            IAutoEventSyncStatusStore status = null)
        {
            _metrics = metrics ?? NullScraperMetrics.Instance;
            _status = status ?? new AutoEventSyncStatusStore();
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = timeProvider ?? TimeProvider.System;
        }

        public async Task SyncAllAsync(CancellationToken cancellationToken = default)
        {
            foreach (var source in _sources)
            {
                await SyncPlatformAsync(source.Platform, cancellationToken);
            }
        }

        public async Task<bool> SyncPlatformAsync(string platform, CancellationToken cancellationToken = default)
        {
            var source = _sources.FirstOrDefault(s => string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase));
            if (source == null) return false;

            await SyncLock.WaitAsync(cancellationToken);
            try
            {
                IReadOnlyList<ScrapedEvent> liveEvents;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    liveEvents = await source.GetLiveEventsAsync(cancellationToken);
                    _metrics.Record(new ScrapeRecord
                    {
                        Platform = source.Platform, Operation = ScrapeOperations.Events, Success = true,
                        Duration = stopwatch.Elapsed, Target = $"{liveEvents.Count} sự kiện"
                    });
                }
                catch (Exception ex)
                {
                    // Lỗi mạng / trang đổi giao diện → giữ nguyên DB; sự kiện cũ vẫn tự hết hạn theo EndsAt
                    _logger.LogWarning(ex, "⚠️ Không đọc được sự kiện {Platform}, giữ nguyên dữ liệu cũ.", source.Platform);
                    _metrics.Record(new ScrapeRecord
                    {
                        Platform = source.Platform, Operation = ScrapeOperations.Events, Success = false,
                        Duration = stopwatch.Elapsed, Target = "Đồng bộ sự kiện", Error = ex.Message
                    });
                    _status.Set(new AutoEventSyncStatus
                    {
                        Platform = source.Platform, RunAt = _time.GetUtcNow(), Success = false,
                        DurationMs = stopwatch.ElapsedMilliseconds, Error = ex.Message
                    });
                    return false;
                }

                var (published, removed) = await ApplyAsync(source.Platform, liveEvents);
                _status.Set(new AutoEventSyncStatus
                {
                    Platform = source.Platform, RunAt = _time.GetUtcNow(), Success = true, DurationMs = stopwatch.ElapsedMilliseconds,
                    EventsFound = liveEvents.Count, EventsShown = published, EventsRemoved = removed
                });
                return true;
            }
            finally
            {
                SyncLock.Release();
            }
        }

        private async Task<(int Published, int Removed)> ApplyAsync(string platform, IReadOnlyList<ScrapedEvent> liveEvents)
        {
            var now = _time.GetUtcNow();
            var stored = await _events.GetByPlatformAsync(platform);
            var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toCache = new List<(Guid EventId, List<LiveEventProductDTO> Products, DateTimeOffset EndsAt)>();
            int published = 0;

            foreach (var scraped in liveEvents.Where(e => !string.IsNullOrWhiteSpace(e.Url)))
            {
                seenLinks.Add(scraped.Url);

                var products = scraped.Products.Where(IsValidProduct).ToList();
                var endsAt = scraped.EndsAt ?? now + RollingLifetime;
                if (string.IsNullOrWhiteSpace(scraped.Title) || products.Count < MinProducts || endsAt <= now)
                {
                    // Dữ liệu đáng ngờ → không đăng; bản đã đăng trước đó (nếu có) được giữ đến khi hết hạn
                    _logger.LogWarning("Bỏ qua sự kiện {Platform} \"{Title}\": {Count} sản phẩm hợp lệ, hết hạn lúc {EndsAt}.",
                        platform, scraped.Title, products.Count, endsAt);
                    continue;
                }

                var evt = stored.FirstOrDefault(e => string.Equals(e.EventLink, scraped.Url, StringComparison.OrdinalIgnoreCase));
                if (evt == null)
                {
                    evt = new Event
                    {
                        EventId = Guid.NewGuid(),
                        Platform = platform,
                        EventLink = scraped.Url,
                        CreatedAt = now.UtcDateTime
                    };
                    _events.Add(evt);
                    stored.Add(evt);
                }

                evt.Title = scraped.Title.Trim();
                evt.ImageUrl = !string.IsNullOrWhiteSpace(scraped.ImageUrl) ? scraped.ImageUrl : products[0].ImageUrl ?? "";
                // Không có banner riêng → FE ghép ảnh vài sản phẩm đầu tiên cho biết sự kiện bán gì; có banner (GearVN) → dùng banner
                evt.PreviewImageUrls = scraped.ImageIsBanner && !string.IsNullOrWhiteSpace(scraped.ImageUrl)
                    ? null
                    : products
                        .Select(p => p.ImageUrl)
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Distinct()
                        .Take(PreviewImageCount)
                        .ToList();
                evt.EndsAt = endsAt;
                evt.EndsAtIsExact = scraped.EndsAt != null;
                evt.IsPublished = !evt.IsHiddenByAdmin; // Admin đã ẩn → giữ ẩn qua các lượt đồng bộ

                toCache.Add((evt.EventId, products.Select(p => p.ToLiveEventProductDto()).ToList(), endsAt));
                if (evt.IsPublished) published++;
            }

            // Xoá hẳn: đã hết giờ, hoặc sự kiện có giờ chính xác (flash sale) mà sàn không còn trả về.
            // Sự kiện hạn cuốn chiếu không thấy trong lượt này thì để tự hết hạn: nguồn của chúng có thể đọc thưa hơn
            // (vd deal CellphoneS đọc từ trang chủ 2 giờ/lần) – thiếu một lượt không có nghĩa là đã kết thúc.
            var removed = stored.Where(e => (e.EndsAt != null && e.EndsAt <= now)
                                         || (e.EndsAtIsExact && !seenLinks.Contains(e.EventLink))).ToList();
            if (removed.Count > 0) _events.RemoveRange(removed);

            await _unitOfWork.SaveChangesAsync();

            foreach (var evt in removed) _cache.Remove(EventService.LiveProductsCacheKey(evt.EventId));
            foreach (var (eventId, products, endsAt) in toCache)
            {
                _cache.Set(EventService.LiveProductsCacheKey(eventId), products,
                    new MemoryCacheEntryOptions().SetAbsoluteExpiration(endsAt));
            }

            _logger.LogInformation("✅ Đồng bộ sự kiện {Platform}: {Published} sự kiện đang hiển thị, xoá {Removed} sự kiện.",
                platform, published, removed.Count);
            return (published, removed.Count);
        }

        // Giá hợp lệ, có link; giá gốc (nếu có) không được thấp hơn giá bán
        private static bool IsValidProduct(ScrapedProduct product)
            => product.Price > 0
               && !string.IsNullOrWhiteSpace(product.Url)
               && (product.OriginalPrice == null || product.OriginalPrice >= product.Price);
    }
}
