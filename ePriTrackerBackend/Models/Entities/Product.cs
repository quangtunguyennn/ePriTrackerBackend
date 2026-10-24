using System.ComponentModel.DataAnnotations;

namespace ePriTrackerBackend.Models.Entities
{
    public class Product
    {
        [Key]
        public Guid ProductId { get; set; }

        [Required]
        public string ProductName { get; set; }

        [Required]
        public string ProductLink { get; set; }

        [Required]
        public string ImageURL { get; set; }

        public string Description { get; set; }

        public Decimal? LatestPrice { get; set; }
        public DateTime? AddedAt { get; set; }
        public DateTimeOffset? LastUpdatedAt { get; set; }

        /// <summary>
        /// Còn hàng trên sàn. Hết hàng thì GIỮ giá gần nhất, không tự bỏ theo dõi – người dùng tự quyết định.
        /// (Không khai báo default trong model: EF luôn gửi giá trị thật; migration điền true cho dữ liệu cũ.)
        /// </summary>
        public bool IsInStock { get; set; } = true;

        /// <summary>Thời điểm phát hiện hết hàng; null khi đang còn hàng.</summary>
        public DateTimeOffset? OutOfStockSince { get; set; }

        /// <summary>
        /// Mã sản phẩm trên sàn dùng cho lấy giá theo lô (vd CellphoneS product_id). Job giá tự tra và lưu lần đầu; null với sàn không cần.
        /// </summary>
        [MaxLength(50)]
        public string PlatformProductId { get; set; }

        // =======================================================
        // CÁC TRƯỜNG TỐI ƯU CRONJOB, TÍNH TOÁN KHOẢNG GIÁ & TIER
        // =======================================================

        /// <summary>
        /// Giá mục tiêu cao nhất của tất cả user đang theo dõi SP này.
        /// Dùng để tính toán khoảng cách giá (Price Gap) để phân Tier quét.
        /// </summary>
        public decimal? MaxTargetPrice { get; set; }

        /// <summary>
        /// Tổng số người dùng đang theo dõi. 
        /// Nếu = 0 -> Đẩy xuống Tier 4 (Ngủ đông).
        /// </summary>
        public int ActiveTrackersCount { get; set; } = 0;

        /// <summary>
        /// Mốc thời gian hệ thống sẽ lấy SP này ra để crawl lần tiếp theo.
        /// (Lưu ý: Cần đánh Index Composite cho trường này và ActiveTrackersCount).
        /// </summary>
        public DateTimeOffset? NextCrawlAt { get; set; }

        /// <summary>
        /// Lưu cấp độ ưu tiên hiện tại của sản phẩm.
        /// 0: Báo động đỏ, 1: Sát ngưỡng, 2: Gần ngưỡng, 3: Xa ngưỡng, 4: Ngủ đông
        /// </summary>
        public int CurrentTier { get; set; } = 3;

        /// <summary>
        /// Chu kỳ quét hiện tại tính bằng phút (VD: 30, 60, 240, 1440, 43200).
        /// Hỗ trợ admin thống kê và debug mà không cần tính ngược từ NextCrawlAt.
        /// </summary>
        public int CrawlIntervalMinutes { get; set; } = 1440;
    }
}