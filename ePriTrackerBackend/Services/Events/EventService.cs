using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Models.Mappings;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace ePriTrackerBackend.Services.Events
{
    public enum PublishEventResult
    {
        Created,
        Republished
    }

    public enum LiveProductsStatus
    {
        Ok,
        EventNotFound,
        NoProducts
    }

    public sealed class LiveProductsResult
    {
        public LiveProductsStatus Status { get; init; }
        public List<LiveEventProductDTO> Products { get; init; } = new List<LiveEventProductDTO>();
    }

    /// <summary>
    /// Nghiệp vụ sự kiện khuyến mãi: Admin xem trước/đăng/gỡ/xoá, người dùng xem sự kiện và sản phẩm live (có cache).
    /// </summary>
    public interface IEventService
    {
        /// <summary>Sự kiện đang chạy trên sàn, gộp trạng thái đã lưu trong DB, kèm sự kiện đã hết hạn (IsExpired).</summary>
        Task<List<Event>> GetPreviewAsync();
        Task<PublishEventResult> PublishAsync(Event evt);
        Task<List<Event>> GetAllForAdminAsync();

        /// <returns>Trạng thái IsPublished mới, null nếu không tìm thấy.</returns>
        Task<bool?> TogglePublishAsync(Guid eventId);

        /// <summary>Sự kiện đang hiển thị. Đồng thời xoá hẳn sự kiện cũ hơn 14 ngày.</summary>
        Task<List<Event>> GetPublishedAsync();

        /// <summary>Sản phẩm live của sự kiện: ưu tiên cache, chưa có thì cào ngay rồi cache.</summary>
        Task<LiveProductsResult> GetLiveProductsAsync(Guid eventId);

        /// <summary>Cào lại và cache sản phẩm live của mọi sự kiện đang hiển thị (job Hangfire).</summary>
        Task RefreshAllLiveProductsCacheAsync();

        /// <returns>false nếu không tìm thấy.</returns>
        Task<bool> DeleteAsync(Guid eventId);
    }

    public class EventService : IEventService
    {
        private static readonly TimeSpan LiveProductsCacheDuration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan EventRetention = TimeSpan.FromDays(14);

        private readonly IEventRepository _events;
        private readonly IEventScraper _eventScraper;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IMemoryCache _cache;
        private readonly ILogger<EventService> _logger;

        public EventService(
            IEventRepository events,
            IEventScraper eventScraper,
            IUnitOfWork unitOfWork,
            IMemoryCache cache,
            ILogger<EventService> logger)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _eventScraper = eventScraper ?? throw new ArgumentNullException(nameof(eventScraper));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static string LiveProductsCacheKey(Guid eventId) => $"EventProducts_{eventId}";

        public async Task<List<Event>> GetPreviewAsync()
        {
            var scrapedEvents = (await _eventScraper.GetCurrentEventsAsync())
                .Select(e => e.ToEventEntity())
                .ToList();

            var dbEvents = await _events.GetAllAsync();

            var finalEvents = new List<Event>();
            var activeDbEventIds = new HashSet<Guid>(); // Rổ lưu ID các sự kiện còn sống

            // 1. XỬ LÝ SỰ KIỆN CÒN TRÊN SÀN: khớp với DB theo tiêu đề hoặc link (bỏ query string)
            foreach (var scraped in scrapedEvents)
            {
                string scrapedTitle = scraped.Title?.Trim().ToLower() ?? "";
                string scrapedBaseLink = scraped.EventLink?.Split('?')[0] ?? "";

                var existingDbEvent = dbEvents.FirstOrDefault(e =>
                    (e.Title != null && e.Title.Trim().ToLower() == scrapedTitle) ||
                    (e.EventLink != null && e.EventLink.Split('?')[0] == scrapedBaseLink)
                );

                if (existingDbEvent != null)
                {
                    scraped.IsPublished = existingDbEvent.IsPublished;
                    scraped.EventId = existingDbEvent.EventId;
                    activeDbEventIds.Add(existingDbEvent.EventId); // Đánh dấu sự kiện này còn sống
                }
                else
                {
                    scraped.IsPublished = false;
                    scraped.EventId = Guid.Empty;
                }
                finalEvents.Add(scraped);
            }

            // 2. SỰ KIỆN CÓ TRONG DB NHƯNG KHÔNG CÒN TRÊN SÀN → đánh dấu hết hạn, trộn chung vào danh sách trả về
            foreach (var expired in dbEvents.Where(e => !activeDbEventIds.Contains(e.EventId)))
            {
                expired.IsExpired = true;
                finalEvents.Add(expired);
            }

            return finalEvents;
        }

        public async Task<PublishEventResult> PublishAsync(Event evt)
        {
            // Kiểm tra dựa trên Tiêu đề (Title) hoặc Đường dẫn (EventLink) của sự kiện
            var existingEvent = await _events.FindByTitleOrLinkAsync(evt.Title, evt.EventLink);

            if (existingEvent == null)
            {
                evt.EventId = Guid.NewGuid();
                evt.CreatedAt = DateTime.UtcNow;
                evt.IsPublished = true;

                _events.Add(evt);
                await _unitOfWork.SaveChangesAsync();
                return PublishEventResult.Created;
            }

            // Đã tồn tại: chỉ bật lại hiển thị, giữ nguyên ID cũ
            existingEvent.IsPublished = true;
            existingEvent.CreatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync();
            return PublishEventResult.Republished;
        }

        public Task<List<Event>> GetAllForAdminAsync() => _events.GetAllNewestFirstAsync();

        public async Task<bool?> TogglePublishAsync(Guid eventId)
        {
            var evt = await _events.FindByIdAsync(eventId);
            if (evt == null) return null;

            evt.IsPublished = !evt.IsPublished;
            await _unitOfWork.SaveChangesAsync();
            return evt.IsPublished;
        }

        public async Task<List<Event>> GetPublishedAsync()
        {
            // TỰ ĐỘNG XÓA: sự kiện cũ hơn 14 ngày bị xoá cứng khỏi DB
            var expiredEvents = await _events.GetCreatedBeforeAsync(DateTime.UtcNow - EventRetention);
            if (expiredEvents.Any())
            {
                _events.RemoveRange(expiredEvents);
                await _unitOfWork.SaveChangesAsync();
            }

            return await _events.GetPublishedNewestFirstAsync();
        }

        public async Task<LiveProductsResult> GetLiveProductsAsync(Guid eventId)
        {
            // BƯỚC 1: Cache do job Hangfire cào sẵn → trả về ngay
            if (_cache.TryGetValue(LiveProductsCacheKey(eventId), out List<LiveEventProductDTO> cachedProducts))
            {
                return new LiveProductsResult { Status = LiveProductsStatus.Ok, Products = cachedProducts };
            }

            // BƯỚC 2: Cache rỗng hoặc job chưa kịp chạy → cào ngay
            var evt = await _events.FindByIdAsync(eventId);
            if (evt == null) return new LiveProductsResult { Status = LiveProductsStatus.EventNotFound };

            var products = await ScrapeLiveProductsAsync(evt.EventLink);
            if (products.Count == 0) return new LiveProductsResult { Status = LiveProductsStatus.NoProducts };

            // BƯỚC 3: Lưu cache cho những người dùng sau
            CacheLiveProducts(eventId, products);
            return new LiveProductsResult { Status = LiveProductsStatus.Ok, Products = products };
        }

        public async Task RefreshAllLiveProductsCacheAsync()
        {
            var activeEvents = await _events.GetPublishedNewestFirstAsync();

            foreach (var evt in activeEvents)
            {
                try
                {
                    var products = await ScrapeLiveProductsAsync(evt.EventLink);
                    if (products.Count > 0) CacheLiveProducts(evt.EventId, products);
                }
                catch (Exception ex)
                {
                    // Một sự kiện lỗi không được làm hỏng cả lượt cập nhật cache
                    _logger.LogError(ex, "Lỗi khi cào sự kiện {EventId}", evt.EventId);
                }
            }
        }

        public async Task<bool> DeleteAsync(Guid eventId)
        {
            var evt = await _events.FindByIdAsync(eventId);
            if (evt == null) return false;

            _events.Remove(evt);
            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        private async Task<List<LiveEventProductDTO>> ScrapeLiveProductsAsync(string eventLink)
            => (await _eventScraper.GetEventProductsAsync(eventLink))
                .Select(p => p.ToLiveEventProductDto())
                .ToList();

        private void CacheLiveProducts(Guid eventId, List<LiveEventProductDTO> products)
            => _cache.Set(LiveProductsCacheKey(eventId), products,
                new MemoryCacheEntryOptions().SetAbsoluteExpiration(LiveProductsCacheDuration));
    }
}
