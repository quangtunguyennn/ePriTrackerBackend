namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>Trạng thái chặn của một sàn (trang Admin hiển thị).</summary>
    public sealed class PlatformBlockStatus
    {
        public string Platform { get; init; }

        /// <summary>Số lần liên tiếp sàn trả 403/429 (thành công một lần → về 0).</summary>
        public int ConsecutiveBlocks { get; init; }

        /// <summary>Đang tạm dừng cào tới thời điểm này; null = không tạm dừng.</summary>
        public DateTimeOffset? PausedUntil { get; init; }

        public DateTimeOffset? LastBlockedAt { get; init; }
        public string LastError { get; init; }
    }

    /// <summary>
    /// Theo dõi sàn đang chặn request tự động (403/429) để job giá ngừng gửi request vô ích – gửi tiếp chỉ làm lệnh chặn kéo dài
    /// và dễ bị chặn hẳn IP. Áp dụng chung mọi sàn, theo tên sàn (IPlatformScraper.Platform).
    /// </summary>
    public interface IPlatformBlockTracker
    {
        /// <summary>Sàn đang tạm dừng cào (chưa tới <paramref name="pausedUntil"/>).</summary>
        bool IsPaused(string platform, out DateTimeOffset pausedUntil);

        /// <summary>Ghi một lần bị chặn. Trả true nếu lần này bắt đầu một đợt tạm dừng.</summary>
        bool RecordBlocked(string platform, string error);

        /// <summary>Sàn trả lời bình thường (kể cả 404) → hết bị chặn, đếm lại từ đầu.</summary>
        void RecordSuccess(string platform);

        /// <summary>Trạng thái hiện tại; null nếu sàn không có lần bị chặn nào gần đây.</summary>
        PlatformBlockStatus GetStatus(string platform);
    }

    /// <summary>
    /// Quy tắc: <see cref="BlocksToPause"/> lần 403/429 liên tiếp → tạm dừng <see cref="FirstPause"/>. Hết hạn thì job chỉ thử lại
    /// (một request); vẫn bị chặn → tạm dừng tiếp, mỗi đợt gấp đôi, tối đa <see cref="MaxPause"/>. Thành công một lần → bình thường.
    /// Lưu trong bộ nhớ (singleton): khởi động lại máy chủ thì thử lại từ đầu.
    /// </summary>
    public sealed class PlatformBlockTracker : IPlatformBlockTracker
    {
        public const int BlocksToPause = 3;
        public static readonly TimeSpan FirstPause = TimeSpan.FromHours(1);
        public static readonly TimeSpan MaxPause = TimeSpan.FromHours(6);

        private sealed class State
        {
            public int ConsecutiveBlocks;
            public int Pauses;
            public DateTimeOffset? PausedUntil;
            public DateTimeOffset? LastBlockedAt;
            public string LastError;
        }

        private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly TimeProvider _time;

        public PlatformBlockTracker(TimeProvider timeProvider = null)
        {
            _time = timeProvider ?? TimeProvider.System;
        }

        public bool IsPaused(string platform, out DateTimeOffset pausedUntil)
        {
            lock (_lock)
            {
                if (platform != null && _states.TryGetValue(platform, out var state) && state.PausedUntil is DateTimeOffset until && until > _time.GetUtcNow())
                {
                    pausedUntil = until;
                    return true;
                }
            }
            pausedUntil = default;
            return false;
        }

        public bool RecordBlocked(string platform, string error)
        {
            if (platform == null) return false;
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                if (!_states.TryGetValue(platform, out var state)) _states[platform] = state = new State();

                state.ConsecutiveBlocks++;
                state.LastBlockedAt = now;
                state.LastError = error;

                // Đang tạm dừng (vd các request song song cùng nhóm cũng bị chặn) → không kéo dài thêm
                if (state.PausedUntil > now) return false;
                if (state.ConsecutiveBlocks < BlocksToPause) return false;

                double hours = Math.Min(MaxPause.TotalHours, FirstPause.TotalHours * Math.Pow(2, state.Pauses));
                state.PausedUntil = now.AddHours(hours);
                state.Pauses++;
                return true;
            }
        }

        public void RecordSuccess(string platform)
        {
            if (platform == null) return;
            lock (_lock) _states.Remove(platform);
        }

        public PlatformBlockStatus GetStatus(string platform)
        {
            if (platform == null) return null;
            lock (_lock)
            {
                if (!_states.TryGetValue(platform, out var state)) return null;
                var now = _time.GetUtcNow();
                return new PlatformBlockStatus
                {
                    Platform = platform,
                    ConsecutiveBlocks = state.ConsecutiveBlocks,
                    PausedUntil = state.PausedUntil > now ? state.PausedUntil : null,
                    LastBlockedAt = state.LastBlockedAt,
                    LastError = state.LastError
                };
            }
        }
    }
}
