using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Services.Suggestions
{
    /// <summary>
    /// Gợi ý sản phẩm rẻ hơn khi người dùng mở trang chi tiết.
    /// </summary>
    public interface ISuggestionService
    {
        /// <summary>Lấy gợi ý đã lưu; nếu chưa có thì cào ngay (lazy-load) và lưu lại.</summary>
        Task<List<SuggestionProductDTO>> GetSuggestionsAsync(Guid productId);

        /// <summary>Ép cào lại và thay toàn bộ gợi ý cũ.</summary>
        /// <exception cref="Exception">Không tìm thấy sản phẩm gốc.</exception>
        Task<List<SuggestionProductDTO>> RefreshSuggestionsAsync(Guid productId);
    }

    public class SuggestionService : ISuggestionService
    {
        private readonly IProductRepository _products;
        private readonly ISuggestionRepository _suggestions;
        private readonly ISuggestionFinder _finder;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IPlatformScraperResolver _scrapers; // chỉ dùng để gắn tên sàn cho từng gợi ý
        private readonly ILogger<SuggestionService> _logger;

        public SuggestionService(
            IProductRepository products,
            ISuggestionRepository suggestions,
            ISuggestionFinder finder,
            IUnitOfWork unitOfWork,
            IPlatformScraperResolver scrapers,
            ILogger<SuggestionService> logger)
        {
            _products = products ?? throw new ArgumentNullException(nameof(products));
            _suggestions = suggestions ?? throw new ArgumentNullException(nameof(suggestions));
            _finder = finder ?? throw new ArgumentNullException(nameof(finder));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _scrapers = scrapers ?? throw new ArgumentNullException(nameof(scrapers));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<SuggestionProductDTO>> GetSuggestionsAsync(Guid productId)
        {
            var suggestions = await _suggestions.GetByProductIdAsync(productId);

            if (!suggestions.Any())
            {
                var product = await _products.FindByIdAsync(productId);
                if (product != null)
                {
                    _logger.LogInformation("Lazy Loading: Bắt đầu lấy sản phẩm gợi ý cho: {ProductName}", product.ProductName);

                    var newSuggestions = await FindSafelyAsync(product);
                    if (newSuggestions.Any())
                    {
                        _suggestions.AddRange(newSuggestions);
                        await _unitOfWork.SaveChangesAsync();
                        suggestions = newSuggestions;
                    }
                }
            }

            return ToDtos(suggestions);
        }

        public async Task<List<SuggestionProductDTO>> RefreshSuggestionsAsync(Guid productId)
        {
            var product = await _products.FindByIdAsync(productId);
            if (product == null) throw new Exception("Không tìm thấy thông tin sản phẩm gốc.");

            _logger.LogInformation("Người dùng yêu cầu làm mới gợi ý, ép cào dữ liệu mới cho: {ProductName}", product.ProductName);

            var freshSuggestions = await FindSafelyAsync(product);

            var oldSuggestions = await _suggestions.GetByProductIdAsync(productId, asTracking: true);
            if (oldSuggestions.Any()) _suggestions.RemoveRange(oldSuggestions);
            if (freshSuggestions.Any()) _suggestions.AddRange(freshSuggestions);

            await _unitOfWork.SaveChangesAsync();

            return ToDtos(freshSuggestions);
        }

        /// <summary>Lỗi mạng/bị chặn khi cào → coi như không có gợi ý (người dùng vẫn xem được trang).</summary>
        private async Task<List<SuggestionProduct>> FindSafelyAsync(Product product)
        {
            try
            {
                return await _finder.FindAsync(product);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy sản phẩm gợi ý từ API.");
                return new List<SuggestionProduct>();
            }
        }

        private List<SuggestionProductDTO> ToDtos(IEnumerable<SuggestionProduct> suggestions)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

            return suggestions
                .OrderBy(s => s.Price)
                .Select(s => new SuggestionProductDTO
                {
                    ProductName = s.ProductName,
                    Price = s.Price,
                    ImageURL = s.ImageURL,
                    ProductLink = s.ProductLink,
                    LastUpdatedAt = now,
                    // Suy ra từ link → không cần lưu thêm cột trong DB
                    Platform = _scrapers.TryResolve(s.ProductLink, out var scraper) ? scraper.Platform : null
                })
                .ToList();
        }
    }
}
