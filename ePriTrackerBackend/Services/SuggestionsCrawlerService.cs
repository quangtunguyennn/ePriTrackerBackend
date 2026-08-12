using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Services
{
    public class SuggestionCrawlService : ISuggestionsCrawlerService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SuggestionCrawlService> _logger;

        // Cấu hình hằng số
        private const string TikiBaseUrl = "https://tiki.vn";
        private const string TikiSearchApiUrl = "https://tiki.vn/api/v2/products";
        private const decimal MinPriceRatioThreshold = 0.4m; // Giá gợi ý phải >= 40% giá gốc (tránh phụ kiện rẻ tiền)
        private const int MaxConcurrentRequests = 3;         // Giảm xuống 3 để an toàn với Anti-bot của Tiki
        private const int MaxRetries = 3;                    // Số lần thử lại tối đa khi bị lỗi mạng/rate-limit

        // Regex compiled tối ưu hiệu năng
        private static readonly Regex SpecialCharRegex = new Regex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        // Danh sách phụ kiện cần loại bỏ nếu sản phẩm gốc không phải phụ kiện
        private static readonly HashSet<string> AccessoriesBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "op lung", "bao da", "kinh cuong luc", "mieng dan", "dan man hinh",
            "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc",
            "chandock", "fidget", "moc khoa", "de tan nhiệt"
        };

        // Danh sách các từ Marketing rác cần lọc khỏi từ khóa tìm kiếm
        private static readonly string[] MarketingWords = {
            "chính hãng", "chinh hang", "nhập khẩu", "nhap khau",
            "bản quốc tế", "ban quoc te", "nguyên seal", "nguyen seal",
            "mới 100%", "moi 100%", "freeship", "tặng kèm", "tang kem",
            "vn/a", "ll/a", "fullbox", "giá rẻ", "gia re", "hàng cty"
        };

        public SuggestionCrawlService(
            IServiceScopeFactory scopeFactory,
            IHttpClientFactory httpClientFactory,
            ILogger<SuggestionCrawlService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task UpdateAllTrackedProductSuggestionsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("🚀 [Hangfire] Bắt đầu tiến trình crawl cập nhật gợi ý sản phẩm định kỳ...");

            try
            {
                List<Product> productsToUpdate;

                // 1. Tạo Scope độc lập lấy danh sách sản phẩm cần cập nhật
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
                    _logger.LogInformation("ℹ️ [Hangfire] Không có sản phẩm nào đang được theo dõi. Kết thúc Job.");
                    return;
                }

                var fetchedSuggestionsBag = new ConcurrentBag<(Guid ProductId, List<SuggestionProduct> Suggestions)>();

                // 2. Crawl dữ liệu song song từ Tiki với giới hạn luồng an toàn
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentRequests,
                    CancellationToken = cancellationToken
                };

                await Parallel.ForEachAsync(productsToUpdate, parallelOptions, async (product, ct) =>
                {
                    try
                    {
                        // Delay ngẫu nhiên giữa các luồng để tránh bị gắn cờ Bot
                        await Task.Delay(Random.Shared.Next(1200, 2800), ct);

                        // Edge Case: Lấy mốc giá so sánh (Gốc hoặc Mới nhất)
                        // Lấy mốc giá so sánh (Gốc hoặc Mới nhất), nếu null thì fallback về 0
                        decimal basePrice = (product.InitialPrice > 0 ? product.InitialPrice : product.LatestPrice) ?? 0m;

                        var suggestions = await FetchSuggestionsWithRetryAsync(product.ProductId, product.ProductName, basePrice, ct);

                        if (suggestions.Any())
                        {
                            fetchedSuggestionsBag.Add((product.ProductId, suggestions));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Bỏ qua log lỗi khi job chủ động bị hủy
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Lỗi khi crawl gợi ý cho SP ID: {ProductId} - {ProductName}", product.ProductId, product.ProductName);
                    }
                });

                if (cancellationToken.IsCancellationRequested) return;

                if (!fetchedSuggestionsBag.Any())
                {
                    _logger.LogWarning("⚠️ [Hangfire] Không thu thập được gợi ý mới nào hợp lệ.");
                    return;
                }

                // 3. Batch Database Update trong 1 Transaction duy nhất
                await SaveSuggestionsToDatabaseAsync(fetchedSuggestionsBag, productsToUpdate.Count, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("🛑 [Hangfire] Tiến trình cào gợi ý sản phẩm đã bị hủy.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "🔥 [Hangfire] Lỗi nghiêm trọng toàn cục trong tiến trình cào gợi ý sản phẩm.");
            }
        }

        #region Core Fetch & Retry Logic

        /// <summary>
        /// Thử lại nhiều lần nếu gặp sự cố kết nối hoặc bị Tiki Rate-Limit
        /// </summary>
        private async Task<List<SuggestionProduct>> FetchSuggestionsWithRetryAsync(
            Guid productId, string productName, decimal basePrice, CancellationToken ct)
        {
            string searchKeyword = ExtractSearchKeyword(productName);
            if (string.IsNullOrWhiteSpace(searchKeyword))
            {
                _logger.LogWarning("⚠️ Không thể trích xuất từ khóa từ tên SP: {ProductName}", productName);
                return new List<SuggestionProduct>();
            }

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await FetchSuggestionsCoreAsync(productId, productName, searchKeyword, basePrice, ct);
                }
                catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("⏳ [Rate-Limit 429] Tiki chặn tạm thời khi tìm '{Keyword}'. Lần thử {Attempt}/{MaxRetries}...", searchKeyword, attempt, MaxRetries);
                    if (attempt == MaxRetries) return new List<SuggestionProduct>();

                    // Exponential Backoff: Chờ 2s, 4s, 8s...
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Lỗi khi gọi API Tiki lần {Attempt}/{MaxRetries} cho SP: {ProductName}", attempt, MaxRetries, productName);
                    if (attempt == MaxRetries) return new List<SuggestionProduct>();
                    await Task.Delay(1500 * attempt, ct);
                }
            }

            return new List<SuggestionProduct>();
        }

        private async Task<List<SuggestionProduct>> FetchSuggestionsCoreAsync(
            Guid productId, string originalName, string searchKeyword, decimal basePrice, CancellationToken ct)
        {
            string apiUrl = $"{TikiSearchApiUrl}?limit=10&q={Uri.EscapeDataString(searchKeyword)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            SetupBrowserHeaders(request);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);

            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tiki API Error (Status: {StatusCode}) khi search: {Keyword}", response.StatusCode, searchKeyword);
                return new List<SuggestionProduct>();
            }

            string content = await response.Content.ReadAsStringAsync(ct);

            // Edge Case: Tiki trả về HTML (Captcha/WAF)
            if (content.TrimStart().StartsWith("<"))
            {
                _logger.LogWarning("🛡️ Bị Tiki Anti-bot chặn (Trả về HTML) khi search từ khóa: {Keyword}", searchKeyword);
                throw new HttpRequestException("Blocked by Anti-bot HTML response", null, HttpStatusCode.TooManyRequests);
            }

            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return new List<SuggestionProduct>();
            }

            var suggestionEntities = new List<SuggestionProduct>();
            var now = DateTime.UtcNow;

            foreach (var item in dataElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out _)) continue;

                string currentName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                decimal suggestionPrice = item.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number ? priceProp.GetDecimal() : 0;

                // Lọc sản phẩm tương đồng & lọc rác phụ kiện
                if (!IsValidSimilarProduct(originalName, currentName)) continue;

                // Edge Case: Xử lý giá gợi ý hợp lệ
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

        #region Database Operations

        private async Task SaveSuggestionsToDatabaseAsync(
            ConcurrentBag<(Guid ProductId, List<SuggestionProduct> Suggestions)> fetchedSuggestionsBag,
            int totalProductsToUpdate,
            CancellationToken cancellationToken)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ePriTrackerContext>();
                using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var productIdsToUpdate = fetchedSuggestionsBag.Select(f => f.ProductId).Distinct().ToList();

                    // Xóa các Gợi ý cũ bằng ExecuteDeleteAsync (EF Core >= 7.0)
                    await context.Set<SuggestionProduct>()
                        .Where(s => productIdsToUpdate.Contains(s.ProductId))
                        .ExecuteDeleteAsync(cancellationToken);

                    // Thêm danh sách Gợi ý mới
                    var allNewSuggestions = fetchedSuggestionsBag.SelectMany(f => f.Suggestions).ToList();

                    if (allNewSuggestions.Any())
                    {
                        await context.Set<SuggestionProduct>().AddRangeAsync(allNewSuggestions, cancellationToken);
                        await context.SaveChangesAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation("✅ [Hangfire] Đã cập nhật thành công {TotalSuggestions} gợi ý cho {SuccessCount}/{TotalProducts} sản phẩm.",
                        allNewSuggestions.Count, productIdsToUpdate.Count, totalProductsToUpdate);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _logger.LogError(ex, "🛑 Lỗi Database khi lưu danh sách Suggestions. Đã Rollback Transaction.");
                    throw;
                }
            }
        }

        #endregion

        #region Helper Methods (Xử lý Chuỗi & Edge Cases)

        private static void SetupBrowserHeaders(HttpRequestMessage request)
        {
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8");
            request.Headers.Add("Referer", $"{TikiBaseUrl}/");
            request.Headers.Add("Sec-Ch-Ua", "\"Chromium\";v=\"122\", \"Not(A:Brand\";v=\"24\", \"Google Chrome\";v=\"122\"");
            request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
            request.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
        }

        /// <summary>
        /// Bóc tách từ khóa ngắn gọn, chính xác nhất từ tên sản phẩm gốc
        /// </summary>
        private static string ExtractSearchKeyword(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

            var splitChars = new char[] { '-', '|', '(', ')', '[', ']', ',', '/', ':' };
            var parts = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries);

            // Ưu tiên lấy vế đầu tiên nếu vế đó đủ dài (>= 5 ký tự)
            string coreName = (parts.Length > 0 && parts[0].Trim().Length >= 5) ? parts[0] : rawName;
            coreName = coreName.ToLowerInvariant();

            // Loại bỏ các từ Marketing rác
            foreach (var word in MarketingWords)
            {
                coreName = Regex.Replace(coreName, $@"\b{Regex.Escape(word)}\b", "", RegexOptions.IgnoreCase);
            }

            // Loại bỏ ký tự đặc biệt & chuẩn hóa khoảng trắng
            coreName = SpecialCharRegex.Replace(coreName, " ");
            coreName = WhiteSpaceRegex.Replace(coreName, " ").Trim();

            // Chỉ lấy tối đa 5 từ quan trọng nhất
            var words = coreName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(5));
        }

        /// <summary>
        /// Kiểm tra tính tương đồng và lọc phụ kiện không liên quan
        /// </summary>
        private static bool IsValidSimilarProduct(string originalName, string searchName)
        {
            if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(searchName)) return false;

            var lowerOriginalNoMark = RemoveVietnameseDiacritics(originalName.ToLowerInvariant());
            var lowerSearchNoMark = RemoveVietnameseDiacritics(searchName.ToLowerInvariant());

            bool originalIsAccessory = AccessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool searchIsAccessory = AccessoriesBlacklist.Any(x => lowerSearchNoMark.Contains(x));

            // Nếu SP gốc KHÔNG PHẢI phụ kiện, mà SP gợi ý LÀ phụ kiện -> Loại bỏ ngay
            if (!originalIsAccessory && searchIsAccessory) return false;

            // Kiểm tra khớp ít nhất 1 từ quan trọng đầu tiên (thường là Brand/Model)
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

        /// <summary>
        /// Edge Case: Ghép URL Tiki an toàn không bị lặp dấu /
        /// </summary>
        private static string BuildFullTikiUrl(string rawUrlPath)
        {
            if (string.IsNullOrWhiteSpace(rawUrlPath)) return string.Empty;
            if (rawUrlPath.StartsWith("http://") || rawUrlPath.StartsWith("https://")) return rawUrlPath;

            return $"{TikiBaseUrl}/{rawUrlPath.TrimStart('/')}";
        }

        /// <summary>
        /// Sửa lỗi tiếng Việt: Khử dấu tiếng Việt chuẩn xác
        /// </summary>
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