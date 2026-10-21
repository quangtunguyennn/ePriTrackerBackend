using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Suggestions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace ePriTrackerBackend.Services
{
    public class SuggestionCrawlService : ISuggestionsCrawlerService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ISuggestionFinder _finder; // Tìm + lọc gợi ý (dùng chung với lazy-load)
        private readonly ILogger<SuggestionCrawlService> _logger;

        // Timeout ngắn (5 giây) để nếu Tầng 1 bị kẹt/bóp băng thông sẽ chuyển sang Tầng 2 ngay
        private static readonly FetchOptions SuggestionFetchOptions = new FetchOptions { Timeout = TimeSpan.FromSeconds(5) };

        // Cấu hình hằng số
        private const int BatchSize = 3; // Xử lý từng lô nhỏ để an toàn cho Memory & DB
        private const int MaxRetries = 3;

        public SuggestionCrawlService(
            IServiceScopeFactory scopeFactory,
            ISuggestionFinder finder,
            ILogger<SuggestionCrawlService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _finder = finder ?? throw new ArgumentNullException(nameof(finder));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task UpdateAllTrackedProductSuggestionsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("🚀 [Hangfire/Dual-Layer] Bắt đầu tiến trình crawl gợi ý sản phẩm...");

            List<Product> productsToUpdate;

            // 1. Lấy danh sách sản phẩm nhanh gọn (scope ngắn để giải phóng DbContext ngay)
            using (var scope = _scopeFactory.CreateScope())
            {
                var products = scope.ServiceProvider.GetRequiredService<IProductRepository>();
                productsToUpdate = await products.GetAllTrackedAsync(asTracking: false, cancellationToken);
            }

            if (!productsToUpdate.Any())
            {
                _logger.LogInformation("ℹ️ [Hangfire] Không có sản phẩm nào đang được theo dõi.");
                return;
            }

            // 2. Cắt Lô (Chunking) để tối ưu RAM
            var productChunks = productsToUpdate.Chunk(BatchSize);
            int successCount = 0;

            foreach (var chunk in productChunks)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("🛑 [Hangfire] Tiến trình cào gợi ý sản phẩm đã bị hủy.");
                    break;
                }

                // Chạy song song trong 1 lô
                var fetchTasks = chunk.Select(product => ProcessSingleProductSuggestionAsync(product, cancellationToken)).ToList();
                var chunkResults = await Task.WhenAll(fetchTasks);

                var validSuggestions = chunkResults.Where(r => r.Suggestions.Any()).ToList();

                if (validSuggestions.Any())
                {
                    // Lưu thẳng lô này vào Database để giải phóng bộ nhớ ngay lập tức
                    await SaveBatchToDatabaseAsync(validSuggestions, cancellationToken);
                    successCount += validSuggestions.Count;
                }

                // Delay ngẫu nhiên giữa các lô để mô phỏng nhịp điệu của người thật
                await Task.Delay(Random.Shared.Next(1500, 3000), cancellationToken);
            }

            _logger.LogInformation($"✅ [Hangfire/Dual-Layer] Đã cập nhật xong gợi ý cho {successCount}/{productsToUpdate.Count} sản phẩm.");
        }

        /// <summary>
        /// Wrapper xử lý logic cho từng sản phẩm trong Lô
        /// </summary>
        private async Task<(Guid ProductId, List<SuggestionProduct> Suggestions)> ProcessSingleProductSuggestionAsync(Product product, CancellationToken ct)
        {
            try
            {
                var suggestions = await FetchSuggestionsWithRetryAsync(product, ct);

                return (product.ProductId, suggestions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Lỗi khi crawl gợi ý cho SP ID: {ProductId} - {ProductName}", product.ProductId, product.ProductName);
                return (product.ProductId, new List<SuggestionProduct>());
            }
        }

        #region Retry Logic

        private async Task<List<SuggestionProduct>> FetchSuggestionsWithRetryAsync(Product product, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    // Delay nhẹ luồng hiện tại để phân tán request trong lô
                    await Task.Delay(Random.Shared.Next(500, 1500), ct);

                    return await _finder.FindAsync(product, SuggestionFetchOptions, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ Lỗi khi lấy gợi ý lần {attempt}/{MaxRetries} cho SP: {product.ProductName}. Chi tiết: {ex.Message}");
                    if (attempt == MaxRetries) return new List<SuggestionProduct>();
                    await Task.Delay(2000 * attempt, ct); // Exponential backoff
                }
            }

            return new List<SuggestionProduct>();
        }

        #endregion

        #region Database Batch Operations

        private async Task SaveBatchToDatabaseAsync(
            List<(Guid ProductId, List<SuggestionProduct> Suggestions)> batchData,
            CancellationToken cancellationToken)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var suggestions = scope.ServiceProvider.GetRequiredService<ISuggestionRepository>();

                try
                {
                    // Xoá gợi ý cũ + ghi gợi ý mới của cả lô trong một transaction
                    await suggestions.ReplaceForProductsAsync(
                        batchData.Select(b => b.ProductId).ToList(),
                        batchData.SelectMany(b => b.Suggestions).ToList(),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "🛑 Lỗi khi Save Batch Database. Đã Rollback lô hiện tại.");
                    throw;
                }
            }
        }

        #endregion
    }
}