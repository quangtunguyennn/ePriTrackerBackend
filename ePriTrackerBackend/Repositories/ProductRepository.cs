using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services;
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
        private readonly ILogger<ProductRepository> _logger;
        private readonly ITikiBrowserService _tikiBrowser;

        // Tái sử dụng HttpClient để tránh cạn kiệt Socket (Socket Exhaustion)
        private static readonly HttpClient _httpClient = new HttpClient();

        private static readonly Regex ProductIdRegex = new Regex(@"-p(\d+)\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SpecialCharRegex = new Regex(@"[^\p{L}\p{N}]", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private static readonly HashSet<string> AccessoriesBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "op lung", "bao da", "kinh cuong luc", "mieng dan",
            "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc"
        };

        private const string TikiBaseUrl = "https://tiki.vn";
        private const decimal MinPriceRatioThreshold = 0.4m;

        // Thiết lập Header giả lập trình duyệt cơ bản cho HttpClient để hạn chế bị chặn ngay từ đầu
        static ProductRepository()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public ProductRepository(
            ePriTrackerContext context,
            ILogger<ProductRepository> logger,
            ITikiBrowserService tikiBrowser)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tikiBrowser = tikiBrowser ?? throw new ArgumentNullException(nameof(tikiBrowser));
        }

        #region Public Interface Implementations

        public async Task AddProduct(string productLink, string userEmail)
        {
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

            var existingProduct = await _context.Product.FirstOrDefaultAsync(x => x.ProductLink == normalizedLink);
            Guid currentProductId;

            if (existingProduct == null)
            {
                _logger.LogInformation("Sản phẩm chưa có trong DB, tiến hành lấy dữ liệu (Ưu tiên HttpClient): {ProductId}", tikiProductId);

                var newProduct = await FetchProductDataAsync(tikiProductId, normalizedLink);

                _context.Product.Add(newProduct);
                await _context.SaveChangesAsync();
                currentProductId = newProduct.ProductId;
            }
            else
            {
                currentProductId = existingProduct.ProductId;
            }

            bool isTracking = await _context.Item.AnyAsync(x => x.UserId == user.UserId && x.ProductId == currentProductId);
            if (!isTracking)
            {
                _context.Item.Add(new Item { UserId = user.UserId, ProductId = currentProductId });
                await _context.SaveChangesAsync();
                // Tự động liên kết Sản phẩm với Sự kiện
                if (productLink.Contains("itm_campaign="))
                {
                    var campaignMatch = Regex.Match(productLink, @"itm_campaign=([^&]+)");
                    if (campaignMatch.Success)
                    {
                        string campaignCode = campaignMatch.Groups[1].Value.ToLower();

                        var matchedEvent = await _context.Event
                            .FirstOrDefaultAsync(e => e.EventLink.ToLower().Contains(campaignCode));

                        if (matchedEvent != null)
                        {
                            bool isMapped = await _context.Set<EventProduct>()
                                .AnyAsync(ep => ep.EventId == matchedEvent.EventId && ep.ProductId == currentProductId);

                            if (!isMapped)
                            {
                                _context.Set<EventProduct>().Add(new EventProduct
                                {
                                    EventId = matchedEvent.EventId,
                                    ProductId = currentProductId
                                });
                                await _context.SaveChangesAsync();
                            }
                        }
                    }
                }
                _logger.LogInformation("Đã thêm sản phẩm {ProductId} vào danh sách theo dõi của User {UserId}", currentProductId, user.UserId);
            }
        }

        public async Task<List<SuggestionProductDTO>> GetAllBetterProducts(Guid productId)
        {
            var suggestions = await _context.Set<SuggestionProduct>()
                .AsNoTracking()
                .Where(s => s.ProductId == productId)
                .ToListAsync();

            if (!suggestions.Any())
            {
                var product = await _context.Product.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId);
                if (product != null)
                {
                    _logger.LogInformation("Lazy Loading: Bắt đầu lấy sản phẩm gợi ý cho: {ProductName}", product.ProductName);

                    var newSuggestions = await CrawlSuggestionsAsync(product.ProductId, product.ProductName, product.InitialPrice);

                    if (newSuggestions.Any())
                    {
                        _context.Set<SuggestionProduct>().AddRange(newSuggestions);
                        await _context.SaveChangesAsync();
                        suggestions = newSuggestions;
                    }
                }
            }

            return suggestions
                .OrderBy(s => s.Price)
                .Select(s => new SuggestionProductDTO
                {
                    ProductName = s.ProductName,
                    Price = s.Price,
                    ImageURL = s.ImageURL,
                    ProductLink = s.ProductLink,
                    LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                })
                .ToList();
        }

        public async Task<List<SuggestionProductDTO>> RefreshSuggestions(Guid productId)
        {
            var product = await _context.Product.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId);
            if (product == null) throw new Exception("Không tìm thấy thông tin sản phẩm gốc.");

            _logger.LogInformation("Người dùng yêu cầu làm mới gợi ý, ép cào dữ liệu mới cho: {ProductName}", product.ProductName);

            var freshSuggestions = await CrawlSuggestionsAsync(product.ProductId, product.ProductName, product.InitialPrice);

            var oldSuggestions = await _context.Set<SuggestionProduct>().Where(s => s.ProductId == productId).ToListAsync();
            if (oldSuggestions.Any())
            {
                _context.Set<SuggestionProduct>().RemoveRange(oldSuggestions);
            }

            if (freshSuggestions.Any())
            {
                _context.Set<SuggestionProduct>().AddRange(freshSuggestions);
            }

            await _context.SaveChangesAsync();

            return freshSuggestions
                .OrderBy(s => s.Price)
                .Select(s => new SuggestionProductDTO
                {
                    ProductName = s.ProductName,
                    Price = s.Price,
                    ImageURL = s.ImageURL,
                    ProductLink = s.ProductLink,
                    LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                })
                .ToList();
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
            if (productItem == null) throw new Exception("Sản phẩm không tồn tại trong danh sách theo dõi.");

            _context.Item.Remove(productItem);
            await _context.SaveChangesAsync();
            return true;
        }

        #endregion

        #region Private API & Parsing Methods (Delegated to TikiBrowserService)

        // HÀM MỚI: Tự động dùng HttpClient cho nhanh, nếu thất bại (bị chặn) thì chuyển sang Playwright
        private async Task<JsonElement> FetchTikiApiWithFallbackAsync(string apiPath)
        {
            string fullUrl = $"{TikiBaseUrl}{apiPath}";
            try
            {
                _logger.LogInformation("Đang thử gọi API Tiki qua HttpClient: {Url}", fullUrl);
                var response = await _httpClient.GetAsync(fullUrl);

                // Nếu bị Tiki trả về 403 Forbidden hoặc lỗi khác -> Ném exception để catch bên dưới
                response.EnsureSuccessStatusCode();

                string jsonString = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(jsonString);

                _logger.LogInformation("Lấy dữ liệu HttpClient thành công (Tốc độ cao)!");
                return document.RootElement.Clone(); // Clone để giữ data khi document bị dispose
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HttpClient thất bại ({Msg}). Kích hoạt Playwright tàng hình cho URL: {Url}", ex.Message, fullUrl);
                // Fallback sang Playwright
                return await _tikiBrowser.FetchTikiApiAsync(apiPath);
            }
        }

        private async Task<Product> FetchProductDataAsync(string tikiProductId, string normalizedLink)
        {
            string apiPath = $"/api/v2/products/{tikiProductId}";

            // Dùng hàm bọc Fallback
            JsonElement root = await FetchTikiApiWithFallbackAsync(apiPath);

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

        private async Task<List<SuggestionProduct>> CrawlSuggestionsAsync(Guid productId, string productName, decimal initialPrice)
        {
            string searchKeyword = ExtractSearchKeyword(productName);
            if (string.IsNullOrEmpty(searchKeyword)) return new List<SuggestionProduct>();

            string apiPath = $"/api/v2/products?limit=10&q={Uri.EscapeDataString(searchKeyword)}";

            try
            {
                // Dùng hàm bọc Fallback
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

                    if (!IsValidSimilarProduct(productName, currentName)) continue;

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
                            ProductLink = string.IsNullOrEmpty(urlPath) ? "" : $"{TikiBaseUrl}/{urlPath.TrimStart('/')}",
                            LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
                        });
                    }
                }
                return suggestionEntities;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy sản phẩm gợi ý từ API.");
                return new List<SuggestionProduct>();
            }
        }

        #endregion

        #region Private Helper Methods (Text Processing & Algorithms)

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

            if (!originalIsAccessory && searchIsAccessory) return false;

            var originalWords = lowerOriginalNoMark.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (originalWords.Length >= 1)
            {
                string entityWord1 = originalWords[0];
                string entityWord2 = originalWords.Length > 1 ? originalWords[1] : "";

                bool containsWord1 = lowerSearchNoMark.Contains(entityWord1);
                bool containsWord2 = !string.IsNullOrEmpty(entityWord2) && lowerSearchNoMark.Contains(entityWord2);

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

        // =========================================================================
        // HÀM ĐƯỢC THAY THẾ: CÀO SẢN PHẨM SỰ KIỆN QUA PLAYWRIGHT (Trình duyệt ảo)
        // =========================================================================
        public async Task<List<LiveEventProductDTO>> GetLiveProductsFromEventAsync(string eventLink)
        {
            var liveProducts = new List<LiveEventProductDTO>();

            try
            {
                // Gọi sang Playwright để bóc tách DOM của trang Landing Page
                JsonElement root = await _tikiBrowser.ScrapeEventPageAsync(eventLink);

                // Dữ liệu giả lập API từ Playwright nhả về, dùng lại logic map DTO cũ
                if (root.TryGetProperty("data", out JsonElement dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArray.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var idProp)) continue;

                        decimal price = item.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0;
                        decimal originalPrice = item.TryGetProperty("original_price", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetDecimal() : price;
                        string urlPath = item.TryGetProperty("url_path", out var u) ? u.GetString() ?? "" : "";

                        liveProducts.Add(new LiveEventProductDTO
                        {
                            ProductId = idProp.GetInt64().ToString(),
                            ProductName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "Sản phẩm ẩn" : "Sản phẩm ẩn",
                            InitialPrice = originalPrice,
                            LatestPrice = price,
                            ImageURL = item.TryGetProperty("thumbnail_url", out var i) ? i.GetString() ?? "" : "",
                            ProductLink = string.IsNullOrEmpty(urlPath) ? "" : $"{TikiBaseUrl}/{urlPath.TrimStart('/')}",
                            LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)) // Đã sửa lỗi Type Mismatch tại đây
                        });
                    }
                }
                return liveProducts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi cào sự kiện từ ProductRepository: {Key}", eventLink);
                return liveProducts;
            }
        }
    }
}