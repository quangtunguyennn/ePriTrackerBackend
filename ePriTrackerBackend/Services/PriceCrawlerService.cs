using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Services
{
    public class PriceCrawlerService : IPriceCrawlerService
    {
        private readonly ePriTrackerContext _context;
        private readonly HttpClient _httpClient;
        private readonly ILogger<PriceCrawlerService> _logger;

        public PriceCrawlerService(
            ePriTrackerContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<PriceCrawlerService> logger)
        {
            _context = context;
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;

            // Bổ sung đầy đủ bộ Header giả lập trình duyệt Chrome
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://tiki.vn/");
            _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
            _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
            _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        }

        public async Task UpdateAllTrackedProductPricesAsync()
        {
            _logger.LogInformation("🚀 [Hangfire] Bắt đầu tiến trình crawl cập nhật giá định kỳ...");

            var productsToUpdate = await _context.Product
                .Where(p => _context.Item.Select(i => i.ProductId).Contains(p.ProductId))
                .ToListAsync();

            if (!productsToUpdate.Any())
            {
                _logger.LogInformation("ℹ️ [Hangfire] Không có sản phẩm nào đang được theo dõi.");
                return;
            }

            // --- PHA 1: LẤY GIÁ TỪ API ĐỒNG THỜI (Tối đa 5 request cùng lúc) ---
            int maxConcurrentRequests = 5;
            using var semaphore = new SemaphoreSlim(maxConcurrentRequests);
            var tasks = new List<Task>();

            // Dùng ConcurrentBag để lưu dữ liệu an toàn trong môi trường đa luồng
            var fetchedPrices = new ConcurrentBag<(Guid ProductId, decimal NewPrice)>();

            foreach (var product in productsToUpdate)
            {
                // Lưu ý: Không dùng DbContext bên trong Task.Run
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        decimal? newPrice = await FetchPriceFromApiAsync(product.ProductLink);

                        if (newPrice.HasValue && newPrice > 0)
                        {
                            fetchedPrices.Add((product.ProductId, newPrice.Value));
                        }

                        // Vẫn giữ Delay ngẫu nhiên cho luồng hiện tại để tránh Spam
                        await Task.Delay(Random.Shared.Next(1000, 2500));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Lỗi khi crawl SP ID: {product.ProductId}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            // Chờ tất cả các luồng hoàn thành việc lấy dữ liệu API
            await Task.WhenAll(tasks);

            // --- PHA 2: CẬP NHẬT DATABASE TUẦN TỰ (Để đảm bảo Thread-Safe cho DbContext) ---
            int successCount = 0;
            foreach (var fetched in fetchedPrices)
            {
                var product = productsToUpdate.FirstOrDefault(p => p.ProductId == fetched.ProductId);
                if (product != null)
                {
                    product.LatestPrice = fetched.NewPrice;
                    product.LastUpdatedAt = DateTimeOffset.UtcNow;

                    _context.PriceHistory.Add(new PriceHistory
                    {
                        ProductId = product.ProductId,
                        Price = fetched.NewPrice,
                        CheckedAt = DateTimeOffset.UtcNow
                    });
                    successCount++;
                }
            }

            // Ghi toàn bộ thay đổi xuống DB trong 1 lần duy nhất
            await _context.SaveChangesAsync();

            _logger.LogInformation($"✅ [Hangfire] Đã cập nhật thành công giá cho {successCount}/{productsToUpdate.Count} sản phẩm.");
        }

        private async Task<decimal?> FetchPriceFromApiAsync(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return null;

                if (url.Contains("tiki.vn"))
                {
                    return await FetchTikiPriceAsync(url);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"⚠️ Lỗi trong FetchPriceFromApiAsync với URL: {url}");
                return null;
            }
        }

        private async Task<decimal?> FetchTikiPriceAsync(string productUrl)
        {
            var match = Regex.Match(productUrl, @"p(\d+)\.html");
            if (!match.Success) return null;

            string productId = match.Groups[1].Value;

            var uri = new Uri(productUrl);
            var queryParams = HttpUtility.ParseQueryString(uri.Query);
            string? spid = queryParams["spid"];

            string apiUrl = string.IsNullOrEmpty(spid)
                ? $"https://tiki.vn/api/v2/products/{productId}"
                : $"https://tiki.vn/api/v2/products/{productId}?spid={spid}";

            var response = await _httpClient.GetAsync(apiUrl);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();

                if (content.TrimStart().StartsWith("<"))
                {
                    _logger.LogWarning($"⚠️ Bị Tiki chặn (trả về HTML) khi lấy giá SP {productId}.");
                    return null;
                }

                using var jsonDoc = JsonDocument.Parse(content);

                if (jsonDoc.RootElement.TryGetProperty("price", out var priceElement))
                {
                    return priceElement.GetDecimal();
                }
            }

            return null;
        }
    }
}