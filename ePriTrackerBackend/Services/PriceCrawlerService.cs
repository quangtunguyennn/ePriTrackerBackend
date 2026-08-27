using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace ePriTrackerBackend.Services
{
    public class PriceCrawlerService : IPriceCrawlerService
    {
        private readonly ePriTrackerContext _context;
        private readonly ITikiBrowserService _tikiBrowserService; // Tầng 2: Vũ khí tàng hình Playwright
        private readonly ILogger<PriceCrawlerService> _logger;
        private readonly ScraperMetricsService _metrics;

        // TẦNG 1: Tái sử dụng HttpClient để crawl tốc độ cao, tiết kiệm RAM/CPU
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string TikiBaseUrl = "https://tiki.vn";

        static PriceCrawlerService()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public PriceCrawlerService(
            ePriTrackerContext context,
            ITikiBrowserService tikiBrowserService,
            ILogger<PriceCrawlerService> logger,
            ScraperMetricsService metrics)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _tikiBrowserService = tikiBrowserService ?? throw new ArgumentNullException(nameof(tikiBrowserService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        public async Task UpdateAllTrackedProductPricesAsync()
        {
            _logger.LogInformation("🚀 [Hangfire/Dual-Layer] Bắt đầu tiến trình crawl cập nhật giá định kỳ...");

            // Lấy danh sách sản phẩm cần update (có người theo dõi)
            var productsToUpdate = await _context.Product
                .Where(p => _context.Item.Select(i => i.ProductId).Contains(p.ProductId))
                .ToListAsync();

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

                    _context.PriceHistory.Add(new PriceHistory
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
                    await _context.SaveChangesAsync();
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
                decimal? newPrice = await FetchTikiPriceAsync(product.ProductLink);

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
        /// CORE DUAL-LAYER CRAWLING: Thử HttpClient trước -> Thất bại tự chuyển sang Playwright
        /// </summary>
        private async Task<JsonElement> FetchTikiApiWithFallbackAsync(string apiPath)
        {
            string fullUrl = $"{TikiBaseUrl}{apiPath}";
            try
            {
                // TẦNG 1: Thử cào bằng HttpClient (Fast Crawl)
                var response = await _httpClient.GetAsync(fullUrl);

                // Nếu bị chặn (403, 429, 503...) sẽ throw HttpRequestException
                response.EnsureSuccessStatusCode();

                string jsonString = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(jsonString);

                // GHI NHẬN THÀNH CÔNG CHO HTTPCLIENT
                _metrics.RecordHttpClientSuccess();

                return document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HttpClient thất bại ({Msg}). Kích hoạt Playwright tàng hình cho URL: {Url}", ex.Message, fullUrl);

                try
                {
                    // TẦNG 2: Fallback sang Playwright Stealth Browser Service
                    var result = await _tikiBrowserService.FetchTikiApiAsync(apiPath);

                    // GHI NHẬN THÀNH CÔNG CHO PLAYWRIGHT
                    _metrics.RecordPlaywrightSuccess();

                    return result;
                }
                catch (Exception fallbackEx)
                {
                    // CẢ 2 TẦNG ĐỀU THẤT BẠI
                    _logger.LogError("Playwright cũng thất bại. Bỏ cuộc cho URL: {Url}", fullUrl);
                    _metrics.RecordFailure();
                    throw; // Ném lỗi ra ngoài để FetchTikiPriceAsync xử lý return null
                }
            }
        }

        /// <summary>
        /// Tách ID và gọi API qua cơ chế Dual-Layer
        /// </summary>
        private async Task<decimal?> FetchTikiPriceAsync(string productUrl)
        {
            if (string.IsNullOrWhiteSpace(productUrl) || !productUrl.Contains("tiki.vn"))
                return null;

            var match = Regex.Match(productUrl, @"p(\d+)\.html");
            if (!match.Success) return null;

            string productId = match.Groups[1].Value;

            var uri = new Uri(productUrl);
            var queryParams = HttpUtility.ParseQueryString(uri.Query);
            string? spid = queryParams["spid"];

            string apiPath = string.IsNullOrEmpty(spid)
                ? $"/api/v2/products/{productId}"
                : $"/api/v2/products/{productId}?spid={spid}";

            try
            {
                // Gọi API qua bọc Dual-Layer
                JsonElement jsonResult = await FetchTikiApiWithFallbackAsync(apiPath);

                if (jsonResult.TryGetProperty("price", out var priceElement) && priceElement.ValueKind == JsonValueKind.Number)
                {
                    return priceElement.GetDecimal();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"⚠️ Không thể lấy giá từ API ngầm cho SP {productId}: {ex.Message}");
                return null;
            }
        }
    }
}