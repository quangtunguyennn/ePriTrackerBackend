using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Services
{
    public class PriceCrawlerService : IPriceCrawlerService
    {
        private readonly IProductRepository _products;
        private readonly IPriceHistoryRepository _priceHistory;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IPlatformScraperResolver _scrapers; // Chọn sàn theo link sản phẩm (Strategy)
        private readonly ILogger<PriceCrawlerService> _logger;

        public PriceCrawlerService(
            IProductRepository products,
            IPriceHistoryRepository priceHistory,
            IUnitOfWork unitOfWork,
            IPlatformScraperResolver scrapers,
            ILogger<PriceCrawlerService> logger)
        {
            _products = products ?? throw new ArgumentNullException(nameof(products));
            _priceHistory = priceHistory ?? throw new ArgumentNullException(nameof(priceHistory));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _scrapers = scrapers ?? throw new ArgumentNullException(nameof(scrapers));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task UpdateAllTrackedProductPricesAsync()
        {
            _logger.LogInformation("🚀 [Hangfire/Dual-Layer] Bắt đầu tiến trình crawl cập nhật giá định kỳ...");

            // Lấy danh sách sản phẩm cần update (có người theo dõi) – có theo dõi thay đổi để cập nhật giá
            var productsToUpdate = await _products.GetAllTrackedAsync(asTracking: true);

            if (!productsToUpdate.Any())
            {
                _logger.LogInformation("ℹ️ [Hangfire] Không có sản phẩm nào đang được theo dõi.");
                return;
            }

            int successCount = 0;
            int batchSize = 5; // Xử lý 5 sản phẩm cùng lúc
            var productChunks = productsToUpdate.Chunk(batchSize);

            foreach (var chunk in productChunks)
            {
                // Sử dụng Task.WhenAll để lấy giá song song cho từng Batch
                var fetchTasks = chunk.Select(product => FetchAndProcessPriceAsync(product)).ToList();

                var results = await Task.WhenAll(fetchTasks);

                // Lọc ra các kết quả thành công
                var validUpdates = results.Where(r => r.IsSuccess).ToList();

                foreach (var update in validUpdates)
                {
                    var product = update.Product;
                    product.LatestPrice = update.NewPrice;
                    product.LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

                    _priceHistory.Add(new PriceHistory
                    {
                        ProductId = product.ProductId,
                        Price = update.NewPrice,
                        CheckedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                    });

                    successCount++;
                }

                // Ghi DB sau mỗi lô (Batch) tránh tràn RAM
                if (validUpdates.Any())
                {
                    await _unitOfWork.SaveChangesAsync();
                }

                // Delay ngẫu nhiên giữa các Lô để tránh kích hoạt WAF
                await Task.Delay(Random.Shared.Next(1500, 3000));
            }

            _logger.LogInformation($"✅ [Hangfire/Dual-Layer] Đã cập nhật thành công giá cho {successCount}/{productsToUpdate.Count} sản phẩm.");
        }

        /// <summary>
        /// Wrapper method xử lý logic gọi API và map kết quả cho từng luồng Task
        /// </summary>
        private async Task<(bool IsSuccess, Product Product, decimal NewPrice)> FetchAndProcessPriceAsync(Product product)
        {
            try
            {
                decimal? newPrice = await FetchCurrentPriceAsync(product.ProductLink);

                if (newPrice.HasValue && newPrice > 0 && newPrice != product.LatestPrice)
                {
                    return (true, product, newPrice.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Lỗi khi crawl SP ID: {product.ProductId}");
            }

            return (false, product, 0);
        }

        /// <summary>
        /// Chọn đúng sàn theo link rồi lấy giá hiện tại. Link không thuộc sàn nào / không có ID → bỏ qua (null).
        /// </summary>
        private async Task<decimal?> FetchCurrentPriceAsync(string productUrl)
        {
            if (!_scrapers.TryResolve(productUrl, out var scraper) || !scraper.TryNormalizeProductUrl(productUrl, out _))
                return null;

            try
            {
                // Bên trong scraper là Dual-Layer (cả 2 tầng thất bại → ném lỗi → catch bên dưới trả null)
                var product = await scraper.GetProductAsync(productUrl);
                return product.Price;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"⚠️ Không thể lấy giá từ {scraper.Platform} cho link {productUrl}: {ex.Message}");
                return null;
            }
        }
    }
}