using ePriTrackerBackend.Services.Notification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ePriTrackerBackend.Hubs
{
    /// <summary>
    /// Kênh realtime cho chuông thông báo (/hubs/notifications). Chỉ server gửi xuống, trình duyệt không gọi lên.
    /// Đăng nhập bằng JWT gửi qua query "access_token" (WebSocket không đặt được header). SignalR định danh người dùng
    /// theo claim NameIdentifier = UserId nên <see cref="SignalRNotificationPublisher"/> gửi đúng người.
    /// </summary>
    [Authorize]
    public class NotificationHub : Hub
    {
        /// <summary>Tên sự kiện trình duyệt lắng nghe: "thông báo của bạn vừa thay đổi" → tự gọi API tải lại số chưa đọc.</summary>
        public const string NotificationsChangedEvent = "notificationsChanged";

        public const string Path = "/hubs/notifications";
    }

    /// <summary>Gửi tín hiệu (không kèm nội dung) tới mọi kết nối của những người dùng này.</summary>
    public sealed class SignalRNotificationPublisher : INotificationPublisher
    {
        private readonly IHubContext<NotificationHub> _hub;

        public SignalRNotificationPublisher(IHubContext<NotificationHub> hub)
        {
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        }

        public Task NotifyChangedAsync(IReadOnlyCollection<Guid> userIds)
        {
            if (userIds == null || userIds.Count == 0) return Task.CompletedTask;
            var users = userIds.Distinct().Select(id => id.ToString()).ToList();
            return _hub.Clients.Users(users).SendAsync(NotificationHub.NotificationsChangedEvent);
        }
    }
}
