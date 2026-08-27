using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Services
{
    public class SuggestionCrawlService : ISuggestionsCrawlerService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ITikiBrowserService _tikiBrowserService; // Tầng 2: Vũ khí tàng hình Playwright
        private readonly ILogger<SuggestionCrawlService> _logger;

        // TẦNG 1: HttpClient dùng chung để tối ưu tốc độ, chống cạn kiệt Socket
        private static readonly HttpClient _httpClient = new HttpClient();

        // Cấu hình hằng số
        private const string TikiBaseUrl = "https://tiki.vn";
        private const decimal MinPriceRatioThreshold = 0.4m;
        private const int BatchSize = 3; // Xử lý từng lô nhỏ để an toàn cho Memory & DB
        private const int MaxRetries = 3;

        // Regex compiled tối ưu hiệu năng
        private static readonly Regex SpecialCharRegex = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new(@"\s+", RegexOptions.Compiled);

        private static readonly HashSet<string> AccessoriesBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "op lung", "bao da", "kinh cuong luc", "mieng dan", "dan man hinh",
            "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc",
            "chandock", "fidget", "moc khoa", "de tan nhiệt"
        };

        private static readonly string[] MarketingWords = {
            "chính hãng", "chinh hang", "nhập khẩu", "nhap khau",
            "bản quốc tế", "ban quoc te", "nguyên seal", "nguyen seal",
            "mới 100%", "moi 100%", "freeship", "tặng kèm", "tang kem",
            "vn/a", "ll/a", "fullbox", "giá rẻ", "gia re", "hàng cty"
        };

        // Static Constructor cài đặt mặc định cho HttpClient
        static SuggestionCrawlService()
        {
            // Set Timeout ngắn (5 giây) để nếu Tầng 1 bị kẹt/bóp băng thông sẽ chuyển sang Tầng 2 ngay
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public SuggestionCrawlService(
            IServiceScopeFactory scopeFactory,
            ITikiBrowserService tikiBrowserService,
            ILogger<SuggestionCrawlService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _tikiBrowserService = tikiBrowserService ?? throw new ArgumentNullException(nameof(tikiBrowserService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task UpdateAllTrackedProductSuggestionsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("🚀 [Hangfire/Dual-Layer] Bắt đầu tiến trình crawl gợi ý sản phẩm...");

            List<Product> productsToUpdate;

            // 1. Lấy danh sách sản phẩm nhanh gọn
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ePriTrackerContext>();
                productsToUpdate = await context.Product
                    .AsNoTracking()
                    .Where(p => context.Item.Any(i => i.ProductId == p.ProductId))
                    .ToListAsync(cancellationToken);
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
                decimal basePrice = (product.InitialPrice > 0 ? product.InitialPrice : product.LatestPrice) ?? 0m;
                var suggestions = await FetchSuggestionsWithRetryAsync(product.ProductId, product.ProductName, basePrice, ct);

                return (product.ProductId, suggestions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Lỗi khi crawl gợi ý cho SP ID: {ProductId} - {ProductName}", product.ProductId, product.ProductName);
                return (product.ProductId, new List<SuggestionProduct>());
            }
        }

        #region Core Fetch (Dual-Layer) & Retry Logic

        /// <summary>
        /// 🔥 CORE DUAL-LAYER CRAWLING: Thử HttpClient trước -> Thất bại tự chuyển sang Playwright
        /// </summary>
        private async Task<JsonElement> FetchTikiApiWithFallbackAsync(string apiPath)
        {
            string fullUrl = $"{TikiBaseUrl}{apiPath}";
            try
            {
                // TẦNG 1: Thử cào siêu tốc bằng HttpClient
                var response = await _httpClient.GetAsync(fullUrl);
                response.EnsureSuccessStatusCode();

                string jsonString = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(jsonString);

                return document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HttpClient thất bại lấy gợi ý ({Msg}). Kích hoạt Playwright tàng hình cho URL: {Url}", ex.Message, fullUrl);

                // TẦNG 2: Fallback sang Playwright Stealth Browser Service
                return await _tikiBrowserService.FetchTikiApiAsync(apiPath);
            }
        }

        private async Task<List<SuggestionProduct>> FetchSuggestionsWithRetryAsync(
            Guid productId, string productName, decimal basePrice, CancellationToken ct)
        {
            string searchKeyword = ExtractSearchKeyword(productName);
            if (string.IsNullOrWhiteSpace(searchKeyword)) return new List<SuggestionProduct>();

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    // Delay nhẹ luồng hiện tại để phân tán request trong lô
                    await Task.Delay(Random.Shared.Next(500, 1500), ct);

                    return await FetchSuggestionsCoreAsync(productId, productName, searchKeyword, basePrice, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ Lỗi khi lấy gợi ý lần {attempt}/{MaxRetries} cho SP: {productName}. Chi tiết: {ex.Message}");
                    if (attempt == MaxRetries) return new List<SuggestionProduct>();
                    await Task.Delay(2000 * attempt, ct); // Exponential backoff
                }
            }

            return new List<SuggestionProduct>();
        }

        private async Task<List<SuggestionProduct>> FetchSuggestionsCoreAsync(
            Guid productId, string originalName, string searchKeyword, decimal basePrice, CancellationToken ct)
        {
            string apiPath = $"/api/v2/products?limit=10&q={Uri.EscapeDataString(searchKeyword)}";

            // 🔥 Gọi qua cơ chế Dual-Layer Crawling
            JsonElement root = await FetchTikiApiWithFallbackAsync(apiPath);

            if (!root.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return new List<SuggestionProduct>();
            }

            var suggestionEntities = new List<SuggestionProduct>();

            foreach (var item in dataElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out _)) continue;

                string currentName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                decimal suggestionPrice = item.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number ? priceProp.GetDecimal() : 0;

                if (!IsValidSimilarProduct(originalName, currentName)) continue;

                bool isPriceValid = basePrice <= 0 || (suggestionPrice > 0 && suggestionPrice <= basePrice && suggestionPrice >= (basePrice * MinPriceRatioThreshold));

                if (isPriceValid)
                {
                    string imageUrl = item.TryGetProperty("thumbnail_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
                    string rawUrlPath = item.TryGetProperty("url_path", out var urlProp) ? urlProp.GetString() ?? "" : "";

                    suggestionEntities.Add(new SuggestionProduct
                    {
                        SuggestionProductId = Guid.NewGuid(),
                        ProductId = productId,
                        ProductName = currentName,
                        Price = suggestionPrice,
                        ImageURL = imageUrl,
                        ProductLink = BuildFullTikiUrl(rawUrlPath),
                        LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                    });
                }
            }

            return suggestionEntities;
        }

        #endregion

        #region Database Batch Operations

        private async Task SaveBatchToDatabaseAsync(
            List<(Guid ProductId, List<SuggestionProduct> Suggestions)> batchData,
            CancellationToken cancellationToken)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ePriTrackerContext>();

                // Transaction cục bộ cho lô này (Giải phóng Lock nhanh)
                using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var productIdsInBatch = batchData.Select(b => b.ProductId).ToList();

                    // Xóa hàng loạt trực tiếp dưới DB bằng EF Core 7+ ExecuteDeleteAsync
                    await context.Set<SuggestionProduct>()
                        .Where(s => productIdsInBatch.Contains(s.ProductId))
                        .ExecuteDeleteAsync(cancellationToken);

                    var newSuggestions = batchData.SelectMany(b => b.Suggestions).ToList();

                    if (newSuggestions.Any())
                    {
                        await context.Set<SuggestionProduct>().AddRangeAsync(newSuggestions, cancellationToken);
                        await context.SaveChangesAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _logger.LogError(ex, "🛑 Lỗi khi Save Batch Database. Đã Rollback lô hiện tại.");
                    throw;
                }
            }
        }

        #endregion

        #region Helper Methods (Xử lý Chuỗi & Edge Cases)

        private static string ExtractSearchKeyword(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

            var splitChars = new char[] { '-', '|', '(', ')', '[', ']', ',', '/', ':' };
            var parts = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries);

            string coreName = (parts.Length > 0 && parts[0].Trim().Length >= 5) ? parts[0] : rawName;
            coreName = coreName.ToLowerInvariant();

            foreach (var word in MarketingWords)
            {
                coreName = Regex.Replace(coreName, $@"\b{Regex.Escape(word)}\b", "", RegexOptions.IgnoreCase);
            }

            coreName = SpecialCharRegex.Replace(coreName, " ");
            coreName = WhiteSpaceRegex.Replace(coreName, " ").Trim();

            var words = coreName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(5));
        }

        private static bool IsValidSimilarProduct(string originalName, string searchName)
        {
            if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(searchName)) return false;

            var lowerOriginalNoMark = RemoveVietnameseDiacritics(originalName.ToLowerInvariant());
            var lowerSearchNoMark = RemoveVietnameseDiacritics(searchName.ToLowerInvariant());

            bool originalIsAccessory = AccessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool searchIsAccessory = AccessoriesBlacklist.Any(x => lowerSearchNoMark.Contains(x));

            if (!originalIsAccessory && searchIsAccessory) return false;

            var originalWords = lowerOriginalNoMark.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (originalWords.Length > 0)
            {
                string keyWord = originalWords[0];
                if (keyWord.Length > 2 && !lowerSearchNoMark.Contains(keyWord))
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildFullTikiUrl(string rawUrlPath)
        {
            if (string.IsNullOrWhiteSpace(rawUrlPath)) return string.Empty;
            if (rawUrlPath.StartsWith("http://") || rawUrlPath.StartsWith("https://")) return rawUrlPath;
            return $"{TikiBaseUrl}/{rawUrlPath.TrimStart('/')}";
        }

        private static string RemoveVietnameseDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            for (int i = 0; i < normalizedString.Length; i++)
            {
                char c = normalizedString[i];
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace('đ', 'd')
                .Replace('Đ', 'D');
        }

        #endregion
    }
}