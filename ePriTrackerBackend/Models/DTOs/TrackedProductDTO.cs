using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Models.DTOs
{
    /// <summary>
    /// Sản phẩm trong danh sách theo dõi của MỘT người dùng: các trường của <see cref="Product"/> (giữ nguyên tên JSON như trước)
    /// + giá lúc người này bắt đầu theo dõi và giá mục tiêu (lưu ở bảng Item – mỗi người một giá ban đầu).
    /// </summary>
    public class TrackedProductDTO
    {
        public Guid ProductId { get; set; }
        public string ProductName { get; set; }
        public string ProductLink { get; set; }
        public string ImageURL { get; set; }
        public string Description { get; set; }
        public decimal? LatestPrice { get; set; }
        public DateTime? AddedAt { get; set; }
        public DateTimeOffset? LastUpdatedAt { get; set; }
        public bool IsInStock { get; set; }
        public DateTimeOffset? OutOfStockSince { get; set; }
        public string PlatformProductId { get; set; }

        /// <summary>Giá lúc người dùng này bắt đầu theo dõi ("Tracked Price"). Null khi lúc đó sản phẩm chưa có giá (hết hàng).</summary>
        public decimal? InitialPrice { get; set; }

        /// <summary>Giá mong muốn của người dùng (chưa dùng).</summary>
        public decimal? TargetPrice { get; set; }

        /// <param name="item">Null khi Admin xem sản phẩm mình không theo dõi.</param>
        public static TrackedProductDTO From(Product product, Item item) => new TrackedProductDTO
        {
            ProductId = product.ProductId,
            ProductName = product.ProductName,
            ProductLink = product.ProductLink,
            ImageURL = product.ImageURL,
            Description = product.Description,
            LatestPrice = product.LatestPrice,
            AddedAt = product.AddedAt,
            LastUpdatedAt = product.LastUpdatedAt,
            IsInStock = product.IsInStock,
            OutOfStockSince = product.OutOfStockSince,
            PlatformProductId = product.PlatformProductId,
            InitialPrice = item?.InitialPrice,
            TargetPrice = item?.TargetPrice
        };
    }
}
