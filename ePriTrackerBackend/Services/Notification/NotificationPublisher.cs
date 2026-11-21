namespace ePriTrackerBackend.Services.Notification
{
    /// <summary>
    /// Báo cho trình duyệt của người dùng biết "thông báo của bạn vừa thay đổi" (có mới / vừa đọc) – trình duyệt tự gọi API tải lại.
    /// Chỉ gửi tín hiệu, không gửi nội dung: nội dung luôn lấy qua API có kiểm tra quyền.
    /// </summary>
    public interface INotificationPublisher
    {
        Task NotifyChangedAsync(IReadOnlyCollection<Guid> userIds);
    }

    /// <summary>Không đẩy gì – cho test / nơi không cần realtime.</summary>
    public sealed class NullNotificationPublisher : INotificationPublisher
    {
        public static readonly NullNotificationPublisher Instance = new();
        private NullNotificationPublisher() { }
        public Task NotifyChangedAsync(IReadOnlyCollection<Guid> userIds) => Task.CompletedTask;
    }
}
