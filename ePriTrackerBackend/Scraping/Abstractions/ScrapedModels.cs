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
    }
}
