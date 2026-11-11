using System.Collections.Concurrent;
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
        /// <summary>
        /// Gọi khi mở trang chi tiết: gợi ý cập nhật trong vòng "Suggestions:RefreshCooldownMinutes" (mặc định 30 phút) → trả luôn;
        /// cũ hơn hoặc chưa có → cào mới và thay bản cũ. Cào lỗi / không ra kết quả → giữ bản cũ. Không có job chạy ngầm.
        /// </summary>
        Task<List<SuggestionProductDTO>> GetSuggestionsAsync(Guid productId);

        /// <summary>Ép cào lại và thay toàn bộ gợi ý cũ.</summary>
        /// <exception cref="Exception">Không tìm thấy sản phẩm gốc.</exception>
        Task<List<SuggestionProductDTO>> RefreshSuggestionsAsync(Guid productId);
    }

    public class SuggestionService : ISuggestionService
    {
        public const int DefaultRefreshCooldownMinutes = 30;

        // Một sản phẩm chỉ cào một lần tại một thời điểm: React StrictMode / nhiều người mở cùng lúc gọi API song song,
        // request sau chờ rồi dùng luôn kết quả vừa cào (không cào lặp, không ghi trùng gợi ý).
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();

        private readonly TimeSpan _refreshCooldown;
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
            IConfiguration configuration,
            ILogger<SuggestionService> logger)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            _refreshCooldown = TimeSpan.FromMinutes(Math.Max(0, configuration.GetValue("Suggestions:RefreshCooldownMinutes", DefaultRefreshCooldownMinutes)));
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
            if (IsFresh(suggestions)) return ToDtos(suggestions);

            var gate = RefreshLocks.GetOrAdd(productId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                // Trong lúc chờ, request khác có thể vừa cào xong
                suggestions = await _suggestions.GetByProductIdAsync(productId);
                if (IsFresh(suggestions)) return ToDtos(suggestions);

                var product = await _products.FindByIdAsync(productId);
                if (product == null) return ToDtos(suggestions);

                _logger.LogInformation("Mở trang chi tiết: gợi ý {State}, cào mới cho: {ProductName}",
                    suggestions.Any() ? "đã cũ" : "chưa có", product.ProductName);

                var fresh = await FindSafelyAsync(product);
                if (!fresh.Any()) return ToDtos(suggestions); // lỗi / không ra kết quả → giữ bản cũ thay vì để trống

                await _suggestions.ReplaceForProductsAsync(new[] { productId }, fresh);
                return ToDtos(fresh);
            }
            finally
            {
                gate.Release();
            }
        }

        private bool IsFresh(List<SuggestionProduct> suggestions)
            => suggestions.Any()
               && suggestions.Max(s => s.LastUpdatedAt) is DateTimeOffset updatedAt
               && DateTimeOffset.UtcNow - updatedAt < _refreshCooldown;

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
                    LastUpdatedAt = (s.LastUpdatedAt ?? now).ToOffset(now.Offset), // thời điểm cào thật (có thể tới 30 phút trước)
                    // Suy ra từ link → không cần lưu thêm cột trong DB
                    Platform = _scrapers.TryResolve(s.ProductLink, out var scraper) ? scraper.Platform : null
                })
                .ToList();
        }
    }
}
