namespace ePriTrackerBackend.Scraping.Abstractions
{
    /// <summary>
    /// STRATEGY theo sàn TMĐT. Mỗi sàn (Tiki, Shopee, ...) là một class cài đặt interface này;
    /// thêm sàn mới chỉ cần thêm class + một dòng đăng ký DI, không sửa service nào (OCP).
    /// </summary>
    public interface IPlatformScraper
    {
        /// <summary>Tên sàn, ví dụ "Tiki".</summary>
        string Platform { get; }

        /// <summary>Link này có thuộc sàn của strategy không (dựa trên host).</summary>
        bool CanHandle(string productUrl);

        /// <summary>
        /// Chuẩn hoá link sản phẩm về một dạng duy nhất để chống trùng trong DB.
        /// Trả false nếu link không chứa được ID sản phẩm.
        /// </summary>
        bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl);

        /// <summary>Lấy thông tin sản phẩm (tên, giá, ảnh...) từ link.</summary>
        /// <exception cref="ArgumentException">Link không chứa ID sản phẩm.</exception>
        Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Sàn cho phép tìm kiếm theo từ khoá (dùng cho gợi ý sản phẩm rẻ hơn). Tách riêng (ISP) vì có sàn
    /// cấm trang tìm kiếm trong robots.txt (vd: CellphoneS) – sàn đó chỉ cài <see cref="IPlatformScraper"/>.
    /// </summary>
    public interface ISearchablePlatformScraper : IPlatformScraper
    {
        Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions options = null, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Sàn có trang sự kiện khuyến mãi. Tách riêng khỏi <see cref="IPlatformScraper"/> (ISP):
    /// không phải sàn nào cũng cần/được hỗ trợ tính năng sự kiện.
    /// </summary>
    public interface IEventScraper
    {
        /// <summary>Tên sàn – sự kiện do Admin duyệt/đăng thuộc sàn này.</summary>
        string Platform { get; }

        /// <summary>Danh sách sự kiện đang chạy. Ném lỗi kèm nội dung sàn trả về nếu không lấy được sự kiện nào.</summary>
        Task<IReadOnlyList<ScrapedEvent>> GetCurrentEventsAsync(CancellationToken cancellationToken = default);

        /// <summary>Sản phẩm đang bày bán trong trang sự kiện. Lỗi thì trả danh sách rỗng.</summary>
        Task<IReadOnlyList<ScrapedProduct>> GetEventProductsAsync(string eventUrl, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Sàn có sự kiện TỰ ĐĂNG (không qua Admin): mỗi lần đọc trả ĐỦ các sự kiện đang chạy, kèm sản phẩm.
    /// </summary>
    public interface IAutoEventSource
    {
        string Platform { get; }

        /// <summary>
        /// Sự kiện đang chạy trên sàn. Ném lỗi khi không đọc được trang – nơi gọi KHÔNG được coi đó là "hết sự kiện".
        /// </summary>
        Task<IReadOnlyList<ScrapedEvent>> GetLiveEventsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Chọn đúng <see cref="IPlatformScraper"/> theo link sản phẩm.
    /// </summary>
    public interface IPlatformScraperResolver
    {
        /// <returns>false nếu chưa có sàn nào hỗ trợ link này.</returns>
        bool TryResolve(string productUrl, out IPlatformScraper scraper);

        /// <summary>Các sàn hỗ trợ tìm kiếm – nơi tìm sản phẩm gợi ý.</summary>
        IReadOnlyList<ISearchablePlatformScraper> SearchablePlatforms { get; }
    }
}
