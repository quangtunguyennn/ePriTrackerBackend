using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Models.DTOs
{
    /// <summary>GET /api/admin/monitoring – tình trạng cào dữ liệu.</summary>
    public class AdminMonitoringDTO
    {
        public DateTimeOffset GeneratedAt { get; set; }

        /// <summary>Số liệu theo sàn tính từ thời điểm này (server khởi động) – lưu trong bộ nhớ.</summary>
        public DateTimeOffset MetricsSince { get; set; }

        public List<PlatformMonitoringDTO> Platforms { get; set; } = new();
        public List<RecurringJobStatusDTO> Jobs { get; set; } = new();
        public JobCountsDTO JobCounts { get; set; }

        /// <summary>Không đọc được trạng thái job (vd Hangfire chưa sẵn sàng) – các phần khác vẫn trả bình thường.</summary>
        public string JobsError { get; set; }

        public List<ScrapeErrorEntry> RecentErrors { get; set; } = new();
    }

    public class PlatformMonitoringDTO
    {
        public string Platform { get; set; }

        /// <summary>Sản phẩm đang có người theo dõi.</summary>
        public int TrackedProducts { get; set; }

        /// <summary>Đã tới lịch cào (NextCrawlAt) quá lâu mà chưa được cào.</summary>
        public int OverdueProducts { get; set; }

        /// <summary>Lâu không cào được (lần lấy dữ liệu thành công gần nhất quá 2 ngày) – giá đứng yên không tính.</summary>
        public int StaleProducts { get; set; }

        public int OutOfStockProducts { get; set; }

        /// <summary>Sàn đã gỡ (ngưng cào) – không tính vào quá hạn / lâu không cập nhật.</summary>
        public int DelistedProducts { get; set; }

        /// <summary>Job giá đang tạm dừng cào sàn này tới thời điểm này vì sàn chặn (403/429 liên tục); null = bình thường.</summary>
        public DateTimeOffset? PausedUntil { get; set; }

        /// <summary>Số lần liên tiếp sàn trả 403/429 gần đây (0 = không bị chặn).</summary>
        public int ConsecutiveBlocks { get; set; }

        public List<PlatformOperationStats> Operations { get; set; } = new();
    }

    public class RecurringJobStatusDTO
    {
        public string Id { get; set; }
        public string Cron { get; set; }
        public DateTimeOffset? LastExecution { get; set; }

        /// <summary>Trạng thái lần chạy gần nhất của Hangfire: Succeeded / Failed / Processing / …</summary>
        public string LastState { get; set; }

        public DateTimeOffset? NextExecution { get; set; }
        public string Error { get; set; }
    }

    public class JobCountsDTO
    {
        public long Succeeded { get; set; }
        public long Failed { get; set; }
        public long Processing { get; set; }
        public long Enqueued { get; set; }
        public long Scheduled { get; set; }
    }

    /// <summary>GET /api/admin/stats – tình hình sử dụng hệ thống.</summary>
    public class AdminStatsDTO
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public int TotalUsers { get; set; }
        public int NewUsersLast7Days { get; set; }

        /// <summary>Sản phẩm đang có ít nhất một người theo dõi.</summary>
        public int TrackedProducts { get; set; }

        /// <summary>Mọi sản phẩm trong DB (kể cả không còn ai theo dõi).</summary>
        public int TotalProducts { get; set; }

        /// <summary>Tổng lượt theo dõi (một người theo dõi một sản phẩm = 1).</summary>
        public int TrackingEntries { get; set; }

        public int OutOfStockProducts { get; set; }
        public int TotalNotifications { get; set; }
        public int NotificationsLast7Days { get; set; }

        public List<PlatformCountDTO> ProductsByPlatform { get; set; } = new();

        /// <summary><see cref="AdminStatsDays"/> ngày gần nhất theo giờ VN, cũ trước.</summary>
        public List<DailyStatsDTO> Daily { get; set; } = new();

        public List<TopProductDTO> TopTracked { get; set; } = new();
        public List<TopProductDTO> TopPriceDrops { get; set; } = new();

        public const int AdminStatsDays = 30;
    }

    public class PlatformCountDTO
    {
        public string Platform { get; set; }
        public int Products { get; set; }
    }

    public class DailyStatsDTO
    {
        /// <summary>yyyy-MM-dd (giờ VN).</summary>
        public string Date { get; set; }
        public int NewProducts { get; set; }
        public int NewUsers { get; set; }
        public int Notifications { get; set; }
    }

    public class TopProductDTO
    {
        public Guid ProductId { get; set; }
        public string ProductName { get; set; }
        public string ProductLink { get; set; }
        public string ImageURL { get; set; }
        public string Platform { get; set; }
        public int Trackers { get; set; }
        public decimal? LatestPrice { get; set; }

        /// <summary>Chỉ có ở danh sách giảm giá: giá 7 ngày trước và % giảm.</summary>
        public decimal? OldPrice { get; set; }
        public double? DropPercent { get; set; }
    }

    /// <summary>GET /api/admin/events/auto – một sàn tự đăng sự kiện.</summary>
    public class AutoEventPlatformDTO
    {
        public string Platform { get; set; }
        public ePriTrackerBackend.Services.Events.AutoEventSyncStatus LastSync { get; set; }
        public List<AdminAutoEventDTO> Events { get; set; } = new();
    }

    public class AdminAutoEventDTO
    {
        public Guid EventId { get; set; }
        public string Title { get; set; }
        public string EventLink { get; set; }
        public string ImageUrl { get; set; }
        public List<string> PreviewImageUrls { get; set; }
        public DateTimeOffset? EndsAt { get; set; }
        public bool EndsAtIsExact { get; set; }
        public bool IsPublished { get; set; }
        public bool IsHiddenByAdmin { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>Số sản phẩm trong cache (null = cache đã hết hạn / server vừa khởi động).</summary>
        public int? ProductCount { get; set; }
    }
}
