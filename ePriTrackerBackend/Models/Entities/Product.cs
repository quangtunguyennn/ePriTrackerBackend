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
        [Required]
        public Decimal InitialPrice { get; set; }
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
    }
}
