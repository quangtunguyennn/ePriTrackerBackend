namespace ePriTrackerBackend.Services.Suggestions
{
    /// <summary>
    /// Thuật toán chọn sản phẩm gợi ý "rẻ hơn, tương tự": tạo từ khoá tìm kiếm, lọc theo tên và theo giá.
    /// Không phụ thuộc sàn TMĐT – dùng chung cho lazy-load (ProductRepository) và job Hangfire (SuggestionCrawlService).
    /// </summary>
    public interface ISuggestionMatcher
    {
        /// <summary>Rút gọn tên sản phẩm thành từ khoá tìm kiếm (bỏ từ marketing, ký tự đặc biệt). Trả "" nếu không tạo được.</summary>
        string ExtractSearchKeyword(string productName);

        /// <summary>Sản phẩm ứng viên có cùng loại với sản phẩm gốc không (loại phụ kiện, khớp từ khoá chính).</summary>
        bool IsSimilarProduct(string originalName, string candidateName);

        /// <summary>Giá ứng viên có nằm trong khoảng chấp nhận so với giá tham chiếu không.</summary>
        bool IsPriceAcceptable(decimal candidatePrice, decimal referencePrice);

        /// <summary>Giá dùng để so sánh: giá khởi điểm, nếu chưa có thì lấy giá mới nhất.</summary>
        decimal GetReferencePrice(decimal initialPrice, decimal? latestPrice);
    }
}
