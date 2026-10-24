using Microsoft.EntityFrameworkCore;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Notification;

namespace ePriTrackerBackend.Services.PriceCronJob
{
    public interface IPriceCrawlerWorker
    {
        Task ProcessPendingProductsAsync(CancellationToken cancellationToken = default);
    }

    public class PriceCrawlerWorker : IPriceCrawlerWorker
    {
        private readonly ePriTrackerContext _dbContext;
        private readonly IPlatformScraperResolver _scrapers;
        private readonly INotificationService _notificationService;
        private readonly ILogger<PriceCrawlerWorker> _logger;
        private const int BatchSize = 50;

        private static readonly TimeZoneInfo VietnamTz = GetVietnamTimeZone();

        private static TimeZoneInfo GetVietnamTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        }

        private static DateTimeOffset GetNowVn() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, VietnamTz);

        public PriceCrawlerWorker(
            ePriTrackerContext dbContext,
            IPlatformScraperResolver scrapers,
            INotificationService notificationService,
            ILogger<PriceCrawlerWorker> logger)
        {
            _dbContext = dbContext;
            _scrapers = scrapers;
            _notificationService = notificationService;
            _logger = logger;
        }

        public async Task ProcessPendingProductsAsync(CancellationToken cancellationToken = default)
        {
            var nowVn = GetNowVn();

            _logger.LogInformation("==========================================================================================");
            _logger.LogInformation("🚀 [CRAWLER START] [{Timestamp:yyyy-MM-dd HH:mm:ss} ICT] Bắt đầu mẻ cào giá mới...", nowVn);

            // 1. Quét DB theo Giờ VN
            var products = await _dbContext.Product
                .Where(p => p.NextCrawlAt <= nowVn && p.ActiveTrackersCount > 0)
                .OrderBy(p => p.NextCrawlAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (!products.Any())
            {
                _logger.LogInformation("💤 [CRAWLER IDLE] [{Timestamp:yyyy-MM-dd HH:mm:ss} ICT] Không có sản phẩm nào đến hạn quét.", GetNowVn());
                _logger.LogInformation("==========================================================================================");
                return;
            }

            _logger.LogInformation("📦 [BATCH INFO] Đã bốc {Count} sản phẩm đến hạn xử lý.", products.Count);

            int successCount = 0;
            int failedCount = 0;
            int priceChangedCount = 0;
            int priceDropCount = 0;

            for (int i = 0; i < products.Count; i++)
            {
                var product = products[i];
                int itemIndex = i + 1;

                if (!_scrapers.TryResolve(product.ProductLink, out var scraper))
                {
                    _logger.LogWarning("⚠️ [{Index}/{Total}] [NO SCRAPER] ProductId: {ProductId} | Link: {Link}",
                        itemIndex, products.Count, product.ProductId, product.ProductLink);
                    failedCount++;
                    continue;
                }

                try
                {
                    var scrapedData = await scraper.GetProductAsync(product.ProductLink);
                    decimal oldPrice = product.LatestPrice ?? 0;
                    decimal newPrice = scrapedData.Price ?? 0;
                    var currentVn = GetNowVn();

                    bool isPriceChanged = newPrice > 0 && newPrice != oldPrice;
                    bool isPriceDrop = newPrice > 0 && oldPrice > 0 && newPrice < oldPrice;

                    var (tier, interval, nextCrawlVn) = CalculateNextCrawl(newPrice, product.MaxTargetPrice, product.ActiveTrackersCount);

                    if (isPriceDrop)
                    {
                        decimal diff = oldPrice - newPrice;
                        _logger.LogInformation("🔥 [{Index}/{Total}] [PRICE DROP] ProductId: {ProductId} | Name: \"{ProductName}\" | Giá cũ: {OldPrice:N0}đ ➔ Giá mới: {NewPrice:N0}đ (Giảm {Diff:N0}đ) | Tier mới: {Tier} (Next: {NextCrawl:HH:mm:ss dd/MM})",
                            itemIndex, products.Count, product.ProductId, product.ProductName, oldPrice, newPrice, diff, tier, nextCrawlVn);

                        await _notificationService.ProcessPriceDropNotificationsAsync(_dbContext, product, newPrice);
                        priceDropCount++;
                        priceChangedCount++;
                    }
                    else if (isPriceChanged)
                    {
                        string direction = newPrice > oldPrice ? "Tăng" : "Thay đổi";
                        _logger.LogInformation("📈 [{Index}/{Total}] [PRICE CHANGE] ProductId: {ProductId} | Name: \"{ProductName}\" | Giá cũ: {OldPrice:N0}đ ➔ Giá mới: {NewPrice:N0}đ ({Direction}) | Tier mới: {Tier} (Next: {NextCrawl:HH:mm:ss dd/MM})",
                            itemIndex, products.Count, product.ProductId, product.ProductName, oldPrice, newPrice, direction, tier, nextCrawlVn);

                        priceChangedCount++;
                    }
                    else
                    {
                        _logger.LogInformation("➡️ [{Index}/{Total}] [UNCHANGED] ProductId: {ProductId} | Name: \"{ProductName}\" | Giữ nguyên: {Price:N0}đ | Tier mới: {Tier} (Next: {NextCrawl:HH:mm:ss dd/MM})",
                            itemIndex, products.Count, product.ProductId, product.ProductName, newPrice, tier, nextCrawlVn);
                    }

                    if (isPriceChanged)
                    {
                        _dbContext.PriceHistory.Add(new PriceHistory
                        {
                            ProductId = product.ProductId,
                            Price = newPrice,
                            CheckedAt = currentVn // Giờ VN
                        });
                    }

                    // Lưu toàn bộ mốc thời gian Entity theo Giờ VN (+07:00)
                    product.LatestPrice = newPrice;
                    product.LastUpdatedAt = currentVn; // Giờ VN
                    product.IsInStock = scrapedData.IsInStock;
                    product.OutOfStockSince = scrapedData.IsInStock ? null : (product.OutOfStockSince ?? currentVn); // Giờ VN
                    product.CurrentTier = tier;
                    product.CrawlIntervalMinutes = interval;
                    product.NextCrawlAt = nextCrawlVn; // Giờ VN

                    _dbContext.Product.Update(product);
                    successCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogError(ex, "❌ [{Index}/{Total}] [ERROR] ProductId: {ProductId} | Name: \"{ProductName}\" | Lỗi cào giá! Đẩy NextCrawlAt +30p.",
                        itemIndex, products.Count, product.ProductId, product.ProductName);

                    product.NextCrawlAt = GetNowVn().AddMinutes(30); // Giờ VN
                    _dbContext.Product.Update(product);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var endVn = GetNowVn();
            var duration = (endVn - nowVn).TotalSeconds;

            _logger.LogInformation("------------------------------------------------------------------------------------------");
            _logger.LogInformation("✅ [CRAWLER FINISHED] [{Timestamp:yyyy-MM-dd HH:mm:ss} ICT] | Tổng thời gian: {Duration:F2}s", endVn, duration);
            _logger.LogInformation("📊 [SUMMARY] Tổng cào: {Total} | Thành công: {Success} | Thất bại: {Failed} | Giá biến động: {Changed} | Giảm giá: {Drop}",
                products.Count, successCount, failedCount, priceChangedCount, priceDropCount);
            _logger.LogInformation("==========================================================================================");
        }

        private (int NewTier, int IntervalMinutes, DateTimeOffset NextCrawlAt) CalculateNextCrawl(decimal latestPrice, decimal? maxTargetPrice, int activeTrackers)
        {
            var nowVn = GetNowVn(); // Giờ VN

            if (activeTrackers <= 0)
                return (4, 43200, nowVn.AddDays(30));

            if (!maxTargetPrice.HasValue || maxTargetPrice <= 0 || latestPrice <= 0)
            {
                int defaultMinutes = activeTrackers > 10 ? 60 : 120;
                return (2, defaultMinutes, nowVn.AddMinutes(defaultMinutes));
            }

            decimal priceGap = (latestPrice - maxTargetPrice.Value) / latestPrice;

            if (priceGap <= 0) return (0, 30, nowVn.AddMinutes(30));
            if (priceGap <= 0.05m || activeTrackers > 50) return (1, 60, nowVn.AddHours(1));
            if (priceGap <= 0.15m || activeTrackers > 20) return (2, 240, nowVn.AddHours(4));

            return (3, 1440, nowVn.AddHours(24));
        }
    }
}