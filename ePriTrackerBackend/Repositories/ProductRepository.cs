using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Repositories
{
    public class ProductRepository : IProductRepository
    {
        private readonly ePriTrackerContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ProductRepository> _logger;

        // Giới hạn request gọi sang Tiki (Tránh bị chặn IP)
        private static readonly SemaphoreSlim _tikiRateLimiter = new SemaphoreSlim(5, 5);

        // Tối ưu hóa Regex (Compile 1 lần, dùng nhiều lần)
        private static readonly Regex ProductIdRegex = new Regex(@"-p(\d+)\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SpecialCharRegex = new Regex(@"[^\p{L}\p{N}]", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        // Danh sách phụ kiện (Dùng HashSet để lookup O(1) siêu tốc)
        private static readonly HashSet<string> AccessoriesBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "op lung", "bao da", "kinh cuong luc", "mieng dan",
            "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc"
        };

        private const string TikiBaseUrl = "https://tiki.vn";
        private const decimal MinPriceRatioThreshold = 0.4m; // Giá gợi ý không được thấp hơn 40% giá gốc (Lọc rác)

        public ProductRepository(
            ePriTrackerContext context,
            IHttpClientFactory httpClientFactory,
            ILogger<ProductRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AddProduct(string productLink, string userEmail)
        {
            // 1. Guard clauses (Kiểm tra đầu vào)
            if (string.IsNullOrWhiteSpace(productLink))
                throw new ArgumentException("Link sản phẩm không được trống.", nameof(productLink));
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new ArgumentException("Email người dùng không được trống.", nameof(userEmail));

            var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null)
            {
                _logger.LogWarning("Không tìm thấy người dùng với email: {Email}", userEmail);
                throw new Exception("Không tìm thấy thông tin người dùng.");
            }

            var match = ProductIdRegex.Match(productLink);
            if (!match.Success)
            {
                _logger.LogWarning("Link Tiki không hợp lệ: {ProductLink}", productLink);
                throw new Exception("Link Tiki không hợp lệ. Không tìm thấy Product ID.");
            }

            string tikiProductId = match.Groups[1].Value;
            string normalizedLink = $"{TikiBaseUrl}/product-p{tikiProductId}.html";

            // 2. Xử lý Sản phẩm
            var existingProduct = await _context.Product.FirstOrDefaultAsync(x => x.ProductLink == normalizedLink);
            Guid currentProductId;

            if (existingProduct == null)
            {
                _logger.LogInformation("Sản phẩm chưa có trong DB, tiến hành lấy dữ liệu từ Tiki: {ProductId}", tikiProductId);
                var newProduct = await FetchProductFromTikiAsync(tikiProductId, normalizedLink);

                // Lưu sản phẩm
                _context.Product.Add(newProduct);
                await _context.SaveChangesAsync();
                currentProductId = newProduct.ProductId;

                // Fire & Forget hoặc Await: Lấy danh sách gợi ý
                _logger.LogInformation("Bắt đầu lấy sản phẩm gợi ý cho: {ProductName}", newProduct.ProductName);
                var suggestions = await CrawlAndBuildSuggestionsAsync(currentProductId, newProduct.ProductName, newProduct.InitialPrice);
                if (suggestions.Any())
                {
                    _context.Set<SuggestionProduct>().AddRange(suggestions);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Đã lưu {Count} sản phẩm gợi ý.", suggestions.Count);
                }
            }
            else
            {
                _logger.LogInformation("Sản phẩm đã tồn tại trong DB: {ProductId}", existingProduct.ProductId);
                currentProductId = existingProduct.ProductId;
            }

            // 3. Xử lý Tracking cho User
            bool isTracking = await _context.Item.AnyAsync(x => x.UserId == user.UserId && x.ProductId == currentProductId);
            if (!isTracking)
            {
                var newItem = new Item
                {
                    UserId = user.UserId,
                    ProductId = currentProductId
                };
                _context.Item.Add(newItem);
                await _context.SaveChangesAsync();
                _logger.LogInformation("Đã thêm sản phẩm {ProductId} vào danh sách theo dõi của User {UserId}", currentProductId, user.UserId);
            }
        }

        public async Task<List<SuggestionProductDTO>> GetAllBetterProducts(Guid productId)
        {
            // Dùng AsNoTracking cho các query chỉ đọc để tăng tối đa hiệu suất
            return await _context.Set<SuggestionProduct>()
                .AsNoTracking()
                .Where(s => s.ProductId == productId)
                .OrderBy(s => s.Price)
                .Select(s => new SuggestionProductDTO
                {
                    ProductName = s.ProductName,
                    Price = s.Price,
                    ImageURL = s.ImageURL,
                    ProductLink = s.ProductLink
                })
                .ToListAsync();
        }

        public async Task<List<Product>> GetAll(string userEmail)
        {
            var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) return new List<Product>();

            var trackedProductIds = await _context.Item
                .AsNoTracking()
                .Where(i => i.UserId == user.UserId)
                .Select(i => i.ProductId)
                .ToListAsync();

            if (!trackedProductIds.Any()) return new List<Product>();

            return await _context.Product
                .AsNoTracking()
                .Where(p => trackedProductIds.Contains(p.ProductId))
                .OrderByDescending(p => p.AddedAt)
                .ToListAsync();
        }

        public async Task<Product?> GetById(Guid id)
        {
            return await _context.Product.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == id);
        }

        public async Task<bool> DeleteProduct(Guid id, string userEmail)
        {
            var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) throw new Exception("Không tìm thấy người dùng");

            var productItem = await _context.Item.FirstOrDefaultAsync(x => x.ProductId == id && x.UserId == user.UserId);
            if (productItem == null)
            {
                throw new Exception("Sản phẩm không tồn tại trong danh sách theo dõi của bạn.");
            }

            _context.Item.Remove(productItem);
            await _context.SaveChangesAsync();

            _logger.LogInformation("User {UserId} đã bỏ theo dõi sản phẩm {ProductId}", user.UserId, id);
            return true;
        }

        #region Private Helper Methods

        private async Task<Product> FetchProductFromTikiAsync(string tikiProductId, string normalizedLink)
        {
            string jsonResponse;
            await _tikiRateLimiter.WaitAsync();
            try
            {
                var client = _httpClientFactory.CreateClient();
                SetupBrowserHeaders(client);
                await Task.Delay(Random.Shared.Next(500, 1200)); // Delay ngẫu nhiên chống Bot Detection

                string apiUrl = $"{TikiBaseUrl}/api/v2/products/{tikiProductId}";
                var response = await client.GetAsync(apiUrl);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Lỗi API Tiki khi lấy SP chính. Status: {StatusCode}", response.StatusCode);
                    throw new Exception($"Lấy dữ liệu từ Tiki thất bại. Status Code: {response.StatusCode}");
                }

                jsonResponse = await response.Content.ReadAsStringAsync();
            }
            finally
            {
                _tikiRateLimiter.Release();
            }

            if (jsonResponse.TrimStart().StartsWith("<"))
            {
                throw new Exception("Tiki đang chặn yêu cầu lấy dữ liệu (Rate Limit / Bot). Vui lòng thử lại sau.");
            }

            using var jsonDoc = JsonDocument.Parse(jsonResponse);
            var root = jsonDoc.RootElement;

            string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "Sản phẩm không xác định" : "Sản phẩm không xác định";
            decimal price = root.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number ? priceProp.GetDecimal() : 0;
            string imageUrl = root.TryGetProperty("thumbnail_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
            string description = root.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";

            return new Product
            {
                ProductLink = normalizedLink,
                ProductName = name,
                ImageURL = imageUrl,
                Description = description,
                InitialPrice = price,
                AddedAt = DateTime.UtcNow,
                LatestPrice = price,
                LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
            };
        }

        private async Task<List<SuggestionProduct>> CrawlAndBuildSuggestionsAsync(Guid productId, string productName, decimal initialPrice)
        {
            string searchKeyword = ExtractSearchKeyword(productName);
            if (string.IsNullOrEmpty(searchKeyword)) return new List<SuggestionProduct>();

            string jsonResponse;
            await _tikiRateLimiter.WaitAsync();
            try
            {
                var client = _httpClientFactory.CreateClient();
                SetupBrowserHeaders(client);
                await Task.Delay(Random.Shared.Next(500, 1500));

                string apiUrl = $"{TikiBaseUrl}/api/v2/products?limit=10&q={Uri.EscapeDataString(searchKeyword)}";
                var response = await client.GetAsync(apiUrl);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Lỗi API Tiki khi search gợi ý. Keyword: {Keyword}, Status: {StatusCode}", searchKeyword, response.StatusCode);
                    return new List<SuggestionProduct>();
                }
                jsonResponse = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi Exception khi crawl sản phẩm gợi ý. Keyword: {Keyword}", searchKeyword);
                return new List<SuggestionProduct>();
            }
            finally
            {
                _tikiRateLimiter.Release();
            }

            if (jsonResponse.TrimStart().StartsWith("<"))
            {
                _logger.LogWarning("Tiki trả về HTML. Khả năng bị chặn do Rate Limit.");
                return new List<SuggestionProduct>();
            }

            try
            {
                using var jsonDoc = JsonDocument.Parse(jsonResponse);
                var root = jsonDoc.RootElement;

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

                    if (!IsValidSimilarProduct(productName, currentName))
                        continue;

                    // Điều kiện giá linh hoạt: Gợi ý có thể bằng hoặc rẻ hơn, nhưng không rớt xuống ngưỡng phi lý
                    if (suggestionPrice > 0 && suggestionPrice <= initialPrice && suggestionPrice > (initialPrice * MinPriceRatioThreshold))
                    {
                        string imageUrl = item.TryGetProperty("thumbnail_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
                        string urlPath = item.TryGetProperty("url_path", out var urlProp) ? urlProp.GetString() ?? "" : "";

                        suggestionEntities.Add(new SuggestionProduct
                        {
                            SuggestionProductId = Guid.NewGuid(),
                            ProductId = productId,
                            ProductName = currentName,
                            Price = suggestionPrice,
                            ImageURL = imageUrl,
                            ProductLink = string.IsNullOrEmpty(urlPath) ? "" : $"{TikiBaseUrl}/{urlPath}",
                            LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                        });
                    }
                }
                return suggestionEntities;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi Parse JSON khi xử lý sản phẩm gợi ý.");
                return new List<SuggestionProduct>();
            }
        }

        private void SetupBrowserHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
            client.DefaultRequestHeaders.Add("Referer", TikiBaseUrl + "/");
        }

        private string ExtractSearchKeyword(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;

            var splitChars = new char[] { '-', '|', '(', '[', ',' };
            string coreName = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries)[0];
            string keyword = coreName.ToLower();

            string[] marketingWords = {
                "chính hãng", "chinh hang", "nhập khẩu", "nhap khau",
                "bản quốc tế", "ban quoc te", "nguyên seal", "nguyen seal",
                "mới 100%", "moi 100%", "freeship", "tặng kèm", "tang kem",
                "vn/a", "ll/a", "fullbox", "giá rẻ", "gia re"
            };

            foreach (var word in marketingWords)
            {
                keyword = keyword.Replace(word, "");
            }

            keyword = SpecialCharRegex.Replace(keyword, " ");
            keyword = WhiteSpaceRegex.Replace(keyword, " ").Trim();

            var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(6));
        }

        private bool IsValidSimilarProduct(string originalName, string searchName)
        {
            if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(searchName)) return false;

            var lowerOriginalNoMark = RemoveVietnameseDiacritics(originalName.ToLower());
            var lowerSearchNoMark = RemoveVietnameseDiacritics(searchName.ToLower());

            bool originalIsAccessory = AccessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool searchIsAccessory = AccessoriesBlacklist.Any(x => lowerSearchNoMark.Contains(x));

            // Chống spam gợi ý phụ kiện cho sản phẩm chính
            if (!originalIsAccessory && searchIsAccessory) return false;

            var originalWords = lowerOriginalNoMark.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (originalWords.Length >= 1)
            {
                string entityWord1 = originalWords[0];
                string entityWord2 = originalWords.Length > 1 ? originalWords[1] : "";

                bool containsWord1 = lowerSearchNoMark.Contains(entityWord1);
                bool containsWord2 = !string.IsNullOrEmpty(entityWord2) && lowerSearchNoMark.Contains(entityWord2);

                // Yêu cầu ít nhất 1 trong 2 từ khóa gốc (thường là Brand hoặc Dòng SP) phải xuất hiện
                if (!containsWord1 && !containsWord2)
                {
                    return false;
                }
            }

            return true;
        }

        private string RemoveVietnameseDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            foreach (char c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
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