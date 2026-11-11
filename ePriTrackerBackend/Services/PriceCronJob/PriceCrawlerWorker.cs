using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Notification;
using ePriTrackerBackend.Services.Products;
using Hangfire;

namespace ePriTrackerBackend.Services.PriceCronJob
{
    public interface IPriceCrawlerWorker
    {
        /// <summary>
        /// Job "adaptive-price-crawler-job" (mỗi phút): cào các sản phẩm đến hạn theo Tier.
        /// Filter Hangfire đặt ở interface vì job đăng ký theo interface (Hangfire đọc attribute trên method của kiểu đã đăng ký).
        /// </summary>
        [DisableConcurrentExecution(timeoutInSeconds: 600)] // lượt trước chưa xong → lượt sau chờ, không cào trùng / gửi thông báo trùng
        [AutomaticRetry(Attempts = 0)]                      // lỗi thì để lượt phút sau làm, không retry dồn
        Task ProcessPendingProductsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Cào giá theo Tier: mỗi lượt lấy tối đa <see cref="BatchSize"/> sản phẩm đến hạn (NextCrawlAt), cập nhật giá theo đúng quy tắc
    /// của job cũ (lấy theo lô trước, hết hàng giữ giá, mô tả, giá ban đầu) rồi hẹn lần cào sau theo khoảng cách giá – mục tiêu.
    /// </summary>
    public class PriceCrawlerWorker : IPriceCrawlerWorker
    {
        private const int BatchSize = 50;
        private const int FetchChunkSize = 5; // cào lẻ song song (giới hạn tốc độ theo sàn nằm ở tầng HttpClient)
        private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(30);

        private static readonly TimeZoneInfo VietnamTz = GetVietnamTimeZone();

        private static TimeZoneInfo GetVietnamTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        }

        private static DateTimeOffset GetNowVn() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, VietnamTz);

        private readonly IProductRepository _products;
        private readonly IPriceHistoryRepository _priceHistory;
        private readonly ITrackedItemRepository _trackedItems;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IPlatformScraperResolver _scrapers;
        private readonly INotificationService _notifications;
        private readonly ILogger<PriceCrawlerWorker> _logger;

        public PriceCrawlerWorker(
            IProductRepository products,
            IPriceHistoryRepository priceHistory,
            ITrackedItemRepository trackedItems,
            IUnitOfWork unitOfWork,
            IPlatformScraperResolver scrapers,
            INotificationService notifications,
            ILogger<PriceCrawlerWorker> logger)
        {
            _products = products ?? throw new ArgumentNullException(nameof(products));
            _priceHistory = priceHistory ?? throw new ArgumentNullException(nameof(priceHistory));
            _trackedItems = trackedItems ?? throw new ArgumentNullException(nameof(trackedItems));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _scrapers = scrapers ?? throw new ArgumentNullException(nameof(scrapers));
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ProcessPendingProductsAsync(CancellationToken cancellationToken = default)
        {
            var startedAt = GetNowVn();
            var products = await _products.GetDueForCrawlAsync(startedAt, BatchSize, cancellationToken);

            if (products.Count == 0)
            {
                _logger.LogDebug("💤 [CRAWLER IDLE] Không có sản phẩm nào đến hạn quét.");
                return;
            }

            _logger.LogInformation("🚀 [CRAWLER START] {Count} sản phẩm đến hạn quét.", products.Count);

            // Sàn hỗ trợ lấy giá theo lô (vd CellphoneS qua API): vài request cho cả lượt.
            // Sản phẩm không có trong kết quả lô → cào lẻ (JSON-LD / API Tiki) bên dưới.
            var batchResults = await FetchBatchPricesAsync(products, cancellationToken);

            int priceChanges = 0, priceDrops = 0, outOfStock = 0, failed = 0;
            var chunks = products.Chunk(FetchChunkSize).ToList();

            for (int c = 0; c < chunks.Count; c++)
            {
                var chunk = chunks[c];
                var toFetch = chunk.Where(p => !batchResults.ContainsKey(p.ProductId)).ToList();
                var fetched = (await Task.WhenAll(toFetch.Select(async product => (product.ProductId, Scraped: await FetchCurrentAsync(product.ProductLink, cancellationToken)))))
                    .ToDictionary(x => x.ProductId, x => x.Scraped);

                _firstPrices.Clear();
                var now = GetNowVn();

                foreach (var product in chunk)
                {
                    ScrapedProduct scraped;
                    if (batchResults.TryGetValue(product.ProductId, out var batch))
                    {
                        scraped = batch.Product;
                        if (!string.IsNullOrEmpty(batch.PlatformProductId) && batch.PlatformProductId != product.PlatformProductId)
                            product.PlatformProductId = batch.PlatformProductId; // lần sau khỏi tra mã
                    }
                    else
                    {
                        scraped = fetched.GetValueOrDefault(product.ProductId);
                    }

                    // Lỗi mạng / bị chặn / sàn chưa hỗ trợ → giữ nguyên giá, không kết luận là hết hàng; hẹn lại để khỏi chiếm chỗ mỗi phút
                    if (scraped == null)
                    {
                        product.NextCrawlAt = now.Add(RetryAfterError);
                        failed++;
                        continue;
                    }

                    decimal oldPrice = product.LatestPrice ?? 0;
                    var outcome = ApplyScrapeResult(product, scraped, now);
                    ApplyDescription(product, scraped); // kết quả lô không có mô tả → giữ bản cũ

                    if (outcome == ScrapeOutcome.PriceChanged)
                    {
                        priceChanges++;
                        if (oldPrice > 0 && product.LatestPrice < oldPrice)
                        {
                            priceDrops++;
                            await _notifications.ProcessPriceDropNotificationsAsync(product, product.LatestPrice.Value);
                        }
                    }
                    if (!product.IsInStock) outOfStock++;

                    ScheduleNextCrawl(product, now);
                }

                // Luôn lưu: dù giá không đổi thì lịch cào (NextCrawlAt) cũng đã đổi
                await _unitOfWork.SaveChangesAsync();

                // Sản phẩm thêm lúc hết hàng vừa có giá lần đầu → đó là giá ban đầu của những ai đang theo dõi
                foreach (var (productId, price) in _firstPrices)
                    await _trackedItems.FillMissingInitialPriceAsync(productId, price);

                // Delay ngẫu nhiên giữa các nhóm để tránh kích hoạt WAF – chỉ khi nhóm này có gửi request lẻ và còn nhóm sau
                if (toFetch.Count > 0 && c < chunks.Count - 1)
                    await Task.Delay(Random.Shared.Next(1500, 3000), cancellationToken);
            }

            _logger.LogInformation(
                "✅ [CRAWLER FINISHED] {Total} sản phẩm ({Batched} lấy theo lô) trong {Seconds:F1}s: {Changed} đổi giá, {Drops} giảm giá, {OutOfStock} hết hàng, {Failed} lỗi.",
                products.Count, batchResults.Count, (GetNowVn() - startedAt).TotalSeconds, priceChanges, priceDrops, outOfStock, failed);
        }

        /// <summary>
        /// Phân Tier theo khoảng cách giữa giá hiện tại và giá mục tiêu cao nhất (của mọi người theo dõi), trả về chu kỳ quét.
        /// 0: đã chạm mục tiêu (30 phút) · 1: cách ≤ 5% hoặc > 50 người theo dõi (1 giờ) · 2: cách ≤ 15% hoặc > 20 người, hoặc chưa ai đặt mục tiêu (1–2 giờ)
        /// · 3: xa mục tiêu (24 giờ) · 4: không còn ai theo dõi (30 ngày).
        /// </summary>
        public static (int Tier, int IntervalMinutes) CalculateTier(decimal latestPrice, decimal? maxTargetPrice, int activeTrackers)
        {
            if (activeTrackers <= 0) return (4, 43200);

            if (!maxTargetPrice.HasValue || maxTargetPrice <= 0 || latestPrice <= 0)
                return (2, activeTrackers > 10 ? 60 : 120);

            decimal priceGap = (latestPrice - maxTargetPrice.Value) / latestPrice;

            if (priceGap <= 0) return (0, 30);
            if (priceGap <= 0.05m || activeTrackers > 50) return (1, 60);
            if (priceGap <= 0.15m || activeTrackers > 20) return (2, 240);
            return (3, 1440);
        }

        private static void ScheduleNextCrawl(Product product, DateTimeOffset now)
        {
            var (tier, interval) = CalculateTier(product.LatestPrice ?? 0, product.MaxTargetPrice, product.ActiveTrackersCount);
            product.CurrentTier = tier;
            product.CrawlIntervalMinutes = interval;
            product.NextCrawlAt = now.AddMinutes(interval);
        }

        /// <summary>
        /// Gom sản phẩm theo sàn có <see cref="IBatchPriceScraper"/> và lấy giá theo lô. Sàn lỗi toàn bộ → bỏ qua (cào lẻ dự phòng).
        /// </summary>
        /// <returns>Kết quả theo ProductId.</returns>
        private async Task<Dictionary<Guid, BatchPriceResult>> FetchBatchPricesAsync(IReadOnlyCollection<Product> products, CancellationToken cancellationToken)
        {
            var results = new Dictionary<Guid, BatchPriceResult>();

            var byPlatform = products
                .Select(p => (Product: p, Scraper: _scrapers.TryResolve(p.ProductLink, out var s) ? s as IBatchPriceScraper : null))
                .Where(x => x.Scraper != null)
                .GroupBy(x => x.Scraper);

            foreach (var group in byPlatform)
            {
                var scraper = group.Key;
                var requests = new List<BatchPriceRequest>();
                var productsByUrl = new Dictionary<string, List<Product>>(StringComparer.OrdinalIgnoreCase);

                foreach (var (product, _) in group)
                {
                    if (!scraper.TryNormalizeProductUrl(product.ProductLink, out var url)) continue;
                    if (!productsByUrl.TryGetValue(url, out var list))
                    {
                        productsByUrl[url] = list = new List<Product>();
                        requests.Add(new BatchPriceRequest { ProductUrl = url, PlatformProductId = product.PlatformProductId });
                    }
                    list.Add(product);
                }

                try
                {
                    var prices = await scraper.GetCurrentPricesAsync(requests, cancellationToken);
                    foreach (var (url, price) in prices)
                    {
                        if (!productsByUrl.TryGetValue(url, out var list)) continue;
                        foreach (var product in list) results[product.ProductId] = price;
                    }

                    _logger.LogInformation("📦 {Platform}: {Got}/{Total} sản phẩm lấy giá theo lô.", scraper.Platform, prices.Count, requests.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "⚠️ Lấy giá theo lô trên {Platform} lỗi – chuyển sang cào từng sản phẩm.", scraper.Platform);
                }
            }

            return results;
        }

        private enum ScrapeOutcome { Unchanged, StockChanged, PriceChanged }

        // Sản phẩm có giá lần đầu trong nhóm đang xử lý (xem ApplyScrapeResult)
        private readonly List<(Guid ProductId, decimal Price)> _firstPrices = new();

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

            // Thêm lúc chưa có giá (hết hàng) → giá thật đầu tiên là giá ban đầu (giá ban đầu lưu theo từng người ở bảng Item)
            if ((product.LatestPrice ?? 0) <= 0)
                _firstPrices.Add((product.ProductId, price));

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
        private static void ApplyDescription(Product product, ScrapedProduct scraped)
        {
            string description = ProductDescription.Sanitize(scraped.Description);
            if (description.Length > 0 && description != product.Description)
                product.Description = description;
        }

        /// <summary>
        /// Chọn đúng sàn theo link rồi lấy dữ liệu hiện tại. Link không thuộc sàn nào / không có ID / lỗi mạng → null (bỏ qua).
        /// </summary>
        private async Task<ScrapedProduct> FetchCurrentAsync(string productUrl, CancellationToken cancellationToken)
        {
            if (!_scrapers.TryResolve(productUrl, out var scraper) || !scraper.TryNormalizeProductUrl(productUrl, out _))
            {
                _logger.LogWarning("⚠️ Sàn chưa hỗ trợ hoặc link không hợp lệ: {Url}", productUrl);
                return null;
            }

            try
            {
                // Bên trong scraper là Dual-Layer (tất cả chiến lược thất bại → ném lỗi → catch bên dưới trả null)
                return await scraper.GetProductAsync(productUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("⚠️ Không thể lấy dữ liệu từ {Platform} cho link {Url}: {Message}", scraper.Platform, productUrl, ex.Message);
                return null;
            }
        }
    }
}
