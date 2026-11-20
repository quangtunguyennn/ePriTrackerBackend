using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Events;
using Microsoft.Extensions.Caching.Memory;

namespace ePriTrackerBackend.Services.Admin
{
    /// <summary>Tab "Sự kiện – Sàn tự đăng" của trang Admin (CellphoneS, HACOM, GearVN). Sự kiện Tiki vẫn qua EventService như cũ.</summary>
    public interface IAdminEventService
    {
        Task<List<AutoEventPlatformDTO>> GetAutoEventsAsync();

        /// <returns>null nếu sàn không tự đăng sự kiện.</returns>
        Task<AutoEventPlatformDTO> SyncNowAsync(string platform, CancellationToken cancellationToken = default);

        /// <returns>null nếu không có sự kiện, hoặc là sự kiện Tiki (Admin quản lý bằng nút đăng/gỡ riêng).</returns>
        Task<AdminAutoEventDTO> SetHiddenAsync(Guid eventId, bool hidden);
    }

    public class AdminEventService : IAdminEventService
    {
        private readonly IEnumerable<IAutoEventSource> _sources;
        private readonly IAutoEventSyncService _sync;
        private readonly IAutoEventSyncStatusStore _status;
        private readonly IEventRepository _events;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IMemoryCache _cache;

        public AdminEventService(
            IEnumerable<IAutoEventSource> sources,
            IAutoEventSyncService sync,
            IAutoEventSyncStatusStore status,
            IEventRepository events,
            IUnitOfWork unitOfWork,
            IMemoryCache cache)
        {
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
            _sync = sync ?? throw new ArgumentNullException(nameof(sync));
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task<List<AutoEventPlatformDTO>> GetAutoEventsAsync()
        {
            var result = new List<AutoEventPlatformDTO>();
            foreach (var platform in AutoPlatforms())
                result.Add(await BuildPlatformAsync(platform));
            return result;
        }

        public async Task<AutoEventPlatformDTO> SyncNowAsync(string platform, CancellationToken cancellationToken = default)
        {
            var name = AutoPlatforms().FirstOrDefault(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;

            // Dùng chung khoá với job 15 phút (AutoEventSyncService) → không chạy chồng; lỗi đọc sàn ghi vào LastSync
            await _sync.SyncPlatformAsync(name, cancellationToken);
            return await BuildPlatformAsync(name);
        }

        public async Task<AdminAutoEventDTO> SetHiddenAsync(Guid eventId, bool hidden)
        {
            var evt = await _events.FindByIdAsync(eventId);
            if (evt == null || !AutoPlatforms().Contains(evt.Platform, StringComparer.OrdinalIgnoreCase)) return null;

            evt.IsHiddenByAdmin = hidden;
            evt.IsPublished = !hidden;
            await _unitOfWork.SaveChangesAsync();
            return ToDto(evt);
        }

        private IEnumerable<string> AutoPlatforms() => _sources.Select(s => s.Platform).Distinct(StringComparer.OrdinalIgnoreCase);

        private async Task<AutoEventPlatformDTO> BuildPlatformAsync(string platform)
        {
            var events = await _events.GetByPlatformNewestFirstAsync(platform);
            return new AutoEventPlatformDTO
            {
                Platform = platform,
                LastSync = _status.Get(platform),
                Events = events.Select(ToDto).ToList()
            };
        }

        private AdminAutoEventDTO ToDto(Models.Entities.Event evt) => new()
        {
            EventId = evt.EventId,
            Title = evt.Title,
            EventLink = evt.EventLink,
            ImageUrl = evt.ImageUrl,
            PreviewImageUrls = evt.PreviewImageUrls,
            EndsAt = evt.EndsAt,
            EndsAtIsExact = evt.EndsAtIsExact,
            IsPublished = evt.IsPublished,
            IsHiddenByAdmin = evt.IsHiddenByAdmin,
            CreatedAt = evt.CreatedAt,
            ProductCount = _cache.TryGetValue(EventService.LiveProductsCacheKey(evt.EventId), out List<LiveEventProductDTO> products) ? products?.Count : null
        };
    }
}
