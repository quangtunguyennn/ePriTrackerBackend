namespace ePriTrackerBackend.Scraping.Abstractions
{
    /// <summary>
    /// Sản phẩm lấy được từ một sàn TMĐT, chưa gắn với DB hay DTO của API.
    /// Trường nào sàn không trả về thì để null – tầng gọi tự quyết định giá trị mặc định.
    /// </summary>
    public sealed class ScrapedProduct
    {
        /// <summary>ID sản phẩm trên sàn (Tiki: số trong "-p123.html").</summary>
        public string ExternalId { get; init; }
        public string Name { get; init; }
        /// <summary>Giá bán hiện tại. Null khi sàn không trả về giá dạng số.</summary>
        public decimal? Price { get; init; }
        /// <summary>Giá gốc trước giảm. Null khi không có.</summary>
        public decimal? OriginalPrice { get; init; }
        public string ImageUrl { get; init; }
        public string Description { get; init; }
        /// <summary>Link đầy đủ tới trang sản phẩm. Chuỗi rỗng khi không có.</summary>
        public string Url { get; init; }
        /// <summary>Sàn báo còn hàng (true) / hết hàng (false). Null khi sàn không cho biết.</summary>
        public bool? IsAvailable { get; init; }

        /// <summary>
        /// Mua được ngay: có giá VÀ sàn không báo hết hàng. Không có giá (sàn trả 0 / thiếu giá) cũng tính là hết hàng.
        /// </summary>
        public bool IsInStock => Price > 0 && IsAvailable != false;
    }

    /// <summary>Một sản phẩm cần lấy giá theo lô.</summary>
    public sealed class BatchPriceRequest
    {
        /// <summary>Link đã chuẩn hoá (khoá của kết quả trả về).</summary>
        public string ProductUrl { get; init; }
        /// <summary>Mã sản phẩm trên sàn đã lưu từ lần trước (null → sàn tự tra từ link).</summary>
        public string PlatformProductId { get; init; }
    }

    public sealed class BatchPriceResult
    {
        public ScrapedProduct Product { get; init; }
        /// <summary>Mã sản phẩm trên sàn – nơi gọi lưu lại để lần sau khỏi tra.</summary>
        public string PlatformProductId { get; init; }
    }

    /// <summary>
    /// Sự kiện khuyến mãi đang chạy trên sàn.
    /// </summary>
    public sealed class ScrapedEvent
    {
        public int ExternalId { get; init; }
        public string Title { get; init; }
        public string Url { get; init; }
        public string ImageUrl { get; init; }

        /// <summary>Thời điểm kết thúc nếu sàn cho biết (vd khung giờ flash sale).</summary>
        public DateTimeOffset? EndsAt { get; init; }

        /// <summary>Sản phẩm đọc được cùng lúc với sự kiện (nguồn tự đăng). Rỗng khi phải cào riêng từng trang sự kiện (Tiki).</summary>
        public IReadOnlyList<ScrapedProduct> Products { get; init; } = Array.Empty<ScrapedProduct>();
    }
}
