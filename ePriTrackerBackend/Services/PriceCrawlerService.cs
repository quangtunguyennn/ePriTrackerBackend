using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Products;
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

            int priceChanges = 0, outOfStockCount = 0;
            int batchSize = 5; // Xử lý 5 sản phẩm cùng lúc
            var productChunks = productsToUpdate.Chunk(batchSize);

            foreach (var chunk in productChunks)
            {
                // Lấy dữ liệu song song cho từng Batch (giới hạn tốc độ theo sàn nằm ở tầng HttpClient)
                var results = await Task.WhenAll(chunk.Select(async product => (Product: product, Scraped: await FetchCurrentAsync(product.ProductLink))));

                bool anyChange = false;
                var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

                foreach (var (product, scraped) in results)
                {
                    // Lỗi mạng / bị chặn / link không hỗ trợ → giữ nguyên mọi thứ, không kết luận là hết hàng
                    if (scraped == null) continue;

                    var outcome = ApplyScrapeResult(product, scraped, now);
                    bool descriptionChanged = ApplyDescription(product, scraped);
                    anyChange |= outcome != ScrapeOutcome.Unchanged || descriptionChanged;
                    if (outcome == ScrapeOutcome.PriceChanged) priceChanges++;
                    if (!product.IsInStock) outOfStockCount++;
                }

                // Ghi DB sau mỗi lô (Batch) tránh tràn RAM
                if (anyChange)
                {
                    await _unitOfWork.SaveChangesAsync();
                }

                // Delay ngẫu nhiên giữa các Lô để tránh kích hoạt WAF
                await Task.Delay(Random.Shared.Next(1500, 3000));
            }

            _logger.LogInformation("✅ [Hangfire/Dual-Layer] Cập nhật giá xong: {Changed}/{Total} sản phẩm đổi giá, {OutOfStock} sản phẩm đang hết hàng.",
                priceChanges, productsToUpdate.Count, outOfStockCount);
        }

        private enum ScrapeOutcome { Unchanged, StockChanged, PriceChanged }

        /// <summary>
        /// Áp quy tắc đã thống nhất:
        /// - Hết hàng: GIỮ giá gần nhất, không ghi lịch sử giá, đánh dấu hết hàng (ghi thời điểm). Không tự bỏ theo dõi.
        /// - Còn hàng: cập nhật giá như cũ, bỏ nhãn hết hàng. Giá thật đầu tiên của sản phẩm thêm lúc chưa có giá → làm giá ban đầu.
        /// </summary>
        private ScrapeOutcome ApplyScrapeResult(Product product, ScrapedProduct scraped, DateTimeOffset now)
        {
            if (!scraped.IsInStock)
            {
                if (!product.IsInStock) return ScrapeOutcome.Unchanged;

                product.IsInStock = false;
                product.OutOfStockSince = now;
                return ScrapeOutcome.StockChanged;
            }

            decimal price = scraped.Price.Value; // IsInStock bảo đảm Price > 0
            var outcome = ScrapeOutcome.Unchanged;

            if (!product.IsInStock)
            {
                product.IsInStock = true;
                product.OutOfStockSince = null;
                product.LastUpdatedAt = now;
                outcome = ScrapeOutcome.StockChanged;
            }

            if (product.InitialPrice <= 0)
            {
                product.InitialPrice = price;
                outcome = ScrapeOutcome.StockChanged;
            }

            if (price != product.LatestPrice)
            {
                product.LatestPrice = price;
                product.LastUpdatedAt = now;
                _priceHistory.Add(new PriceHistory { ProductId = product.ProductId, Price = price, CheckedAt = now });
                outcome = ScrapeOutcome.PriceChanged;
            }

            return outcome;
        }

        /// <summary>
        /// Mô tả mới (đã lọc) khác bản đang lưu → cập nhật (kể cả khi hết hàng). Sàn không trả mô tả → giữ bản cũ.
        /// Không ghi lịch sử giá, không đổi LastUpdatedAt (đó là thời điểm đổi giá).
        /// </summary>
        private static bool ApplyDescription(Product product, ScrapedProduct scraped)
        {
            string description = ProductDescription.Sanitize(scraped.Description);
            if (description.Length == 0 || description == product.Description) return false;

            product.Description = description;
            return true;
        }

        /// <summary>
        /// Chọn đúng sàn theo link rồi lấy dữ liệu hiện tại. Link không thuộc sàn nào / không có ID / lỗi mạng → null (bỏ qua).
        /// </summary>
        private async Task<ScrapedProduct> FetchCurrentAsync(string productUrl)
        {
            if (!_scrapers.TryResolve(productUrl, out var scraper) || !scraper.TryNormalizeProductUrl(productUrl, out _))
                return null;

            try
            {
                // Bên trong scraper là Dual-Layer (tất cả chiến lược thất bại → ném lỗi → catch bên dưới trả null)
                return await scraper.GetProductAsync(productUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Không thể lấy dữ liệu từ {Platform} cho link {Url}: {Message}", scraper.Platform, productUrl, ex.Message);
                return null;
            }
        }
    }
}