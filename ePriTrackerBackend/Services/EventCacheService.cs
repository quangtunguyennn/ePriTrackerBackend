using ePriTrackerBackend.Services.Events;

namespace ePriTrackerBackend.Services
{
    /// <summary>
    /// Điểm vào của job Hangfire "refresh-event-cache-job". Giữ nguyên tên/namespace vì Hangfire lưu tên kiểu dưới DB;
    /// toàn bộ nghiệp vụ nằm ở <see cref="IEventService"/>.
    /// </summary>
    public class EventCacheService : IEventCacheService
    {
        private readonly IEventService _eventService;

        public EventCacheService(IEventService eventService)
        {
            _eventService = eventService;
        }

        public Task RefreshLiveEventProductsCacheAsync() => _eventService.RefreshAllLiveProductsCacheAsync();
    }
}
