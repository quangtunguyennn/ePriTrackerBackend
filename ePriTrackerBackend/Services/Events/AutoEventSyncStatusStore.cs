using System.Collections.Concurrent;

namespace ePriTrackerBackend.Services.Events
{
    /// <summary>Kết quả lượt đồng bộ gần nhất của một sàn tự đăng (hiện ở trang Admin).</summary>
    public sealed class AutoEventSyncStatus
    {
        public string Platform { get; init; }
        public DateTimeOffset RunAt { get; init; }
        public bool Success { get; init; }
        public long DurationMs { get; init; }

        /// <summary>Số sự kiện sàn trả về (trước khi lọc).</summary>
        public int EventsFound { get; init; }

        /// <summary>Số sự kiện đang hiện cho người dùng sau lượt này (đã trừ sự kiện Admin ẩn / dữ liệu đáng ngờ).</summary>
        public int EventsShown { get; init; }

        public int EventsRemoved { get; init; }
        public string Error { get; init; }
    }

    /// <summary>Lưu kết quả đồng bộ gần nhất theo sàn – singleton, chỉ trong bộ nhớ (mất khi khởi động lại server).</summary>
    public interface IAutoEventSyncStatusStore
    {
        void Set(AutoEventSyncStatus status);
        AutoEventSyncStatus Get(string platform);
    }

    public sealed class AutoEventSyncStatusStore : IAutoEventSyncStatusStore
    {
        private readonly ConcurrentDictionary<string, AutoEventSyncStatus> _byPlatform = new(StringComparer.OrdinalIgnoreCase);

        public void Set(AutoEventSyncStatus status)
        {
            if (status?.Platform != null) _byPlatform[status.Platform] = status;
        }

        public AutoEventSyncStatus Get(string platform)
            => platform != null && _byPlatform.TryGetValue(platform, out var status) ? status : null;
    }
}
