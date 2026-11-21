using System.Text.Json;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;

namespace ePriTrackerBackend.Services.Notification
{
    /// <summary>Hộp thông báo của MỘT người dùng (chuông trên Navbar): xem, đếm chưa đọc, đánh dấu đã đọc.</summary>
    public interface INotificationInboxService
    {
        Task<NotificationPageDTO> GetPageAsync(Guid userId, int take, DateTimeOffset? before, CancellationToken cancellationToken = default);
        Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <returns>false nếu không có thông báo này hoặc không phải của người dùng.</returns>
        Task<bool> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);

        Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);
    }

    public class NotificationInboxService : INotificationInboxService
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 50;

        private readonly INotificationRepository _notifications;
        private readonly IUnitOfWork _unitOfWork;
        private readonly INotificationPublisher _publisher;

        public NotificationInboxService(INotificationRepository notifications, IUnitOfWork unitOfWork, INotificationPublisher publisher = null)
        {
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _publisher = publisher ?? NullNotificationPublisher.Instance;
        }

        public async Task<NotificationPageDTO> GetPageAsync(Guid userId, int take, DateTimeOffset? before, CancellationToken cancellationToken = default)
        {
            take = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);

            var all = await _notifications.GetForUserAsync(userId, cancellationToken);
            var tracked = await _notifications.GetTrackedProductIdsAsync(userId, cancellationToken);

            var older = all
                .Where(n => before == null || n.CreatedAt < before.Value)
                .OrderByDescending(n => n.CreatedAt)
                .ToList();

            return new NotificationPageDTO
            {
                Items = older.Take(take).Select(n => new NotificationDTO
                {
                    NotificationId = n.NotificationId,
                    Type = n.Type,
                    Data = ParseData(n.Data),
                    Title = n.Title,
                    Content = n.Content,
                    ProductId = n.ProductId,
                    ProductTracked = n.ProductId.HasValue && tracked.Contains(n.ProductId.Value),
                    IsRead = n.IsRead,
                    CreatedAt = n.CreatedAt
                }).ToList(),
                UnreadCount = all.Count(n => !n.IsRead),
                HasMore = older.Count > take
            };
        }

        public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
            => _notifications.CountUnreadAsync(userId, cancellationToken);

        public async Task<bool> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
        {
            var notification = await _notifications.FindForUserAsync(notificationId, userId, cancellationToken);
            if (notification == null) return false;

            if (!notification.IsRead)
            {
                notification.IsRead = true;
                await _unitOfWork.SaveChangesAsync();
                await NotifyOtherTabsAsync(userId);
            }
            return true;
        }

        public async Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            if (await _notifications.MarkAllReadAsync(userId, cancellationToken) > 0)
                await NotifyOtherTabsAsync(userId);
        }

        // Người dùng mở nhiều tab/thiết bị: tab khác cũng cập nhật số chưa đọc. Lỗi đẩy không làm hỏng thao tác.
        private async Task NotifyOtherTabsAsync(Guid userId)
        {
            try { await _publisher.NotifyChangedAsync(new[] { userId }); }
            catch { /* realtime chỉ là tiện ích */ }
        }

        // Dữ liệu lỗi/không phải object → coi như không có (FE dùng Title/Content)
        private static JsonElement? ParseData(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
