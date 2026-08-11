using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace ePriTrackerBackend.Repositories
{
    public class ProductRepository : IProductRepository
    {
        private readonly ePriTrackerContext _context;
        private readonly IHttpClientFactory _httpClientFactory;

        // Dùng biến static để kiểm soát tốc độ gọi API trên TOÀN BỘ request của người dùng.
        // Giới hạn tối đa 5 request gọi sang Tiki cùng một thời điểm.
        private static readonly SemaphoreSlim _tikiRateLimiter = new SemaphoreSlim(5, 5);

        public ProductRepository(ePriTrackerContext context, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
        }

        private void SetupBrowserHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
            client.DefaultRequestHeaders.Add("Referer", "https://tiki.vn/");
            client.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
            client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
            client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        }

        public async Task AddProduct(string productLink, string userEmail)
        {
            var user = await _context.User.FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) throw new Exception("Không tìm thấy thông tin người dùng.");

            var match = Regex.Match(productLink, @"-p(\d+)\.html");
            if (!match.Success)
            {
                throw new Exception("Link Tiki không hợp lệ. Không tìm thấy Product ID.");
            }
            string tikiProductId = match.Groups[1].Value;

            var existingProduct = await _context.Product.FirstOrDefaultAsync(x => x.ProductLink.Contains($"-p{tikiProductId}.html"));
            Guid currentProductId;

            if (existingProduct == null)
            {
                string jsonResponse = string.Empty;

                // 1. Áp dụng SemaphoreSlim bọc gọn phần lấy dữ liệu (API Call)
                await _tikiRateLimiter.WaitAsync();
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    SetupBrowserHeaders(client);

                    // Delay ngắn lại (0.5s - 1.5s) để người dùng không phải chờ lâu trên UI
                    await Task.Delay(Random.Shared.Next(500, 1500));

                    string apiUrl = $"https://tiki.vn/api/v2/products/{tikiProductId}";
                    var response = await client.GetAsync(apiUrl);

                    if (!response.IsSuccessStatusCode)
                        throw new Exception($"Lấy dữ liệu từ Tiki thất bại. Status Code: {response.StatusCode}");

                    jsonResponse = await response.Content.ReadAsStringAsync();
                }
                finally
                {
                    // Giải phóng luồng NGAY LẬP TỨC để các người dùng khác có thể dùng tiếp
                    _tikiRateLimiter.Release();
                }

                // 2. Các thao tác xử lý chuỗi và DB (Không lo dính tới Rate Limit mạng)
                try
                {
                    if (jsonResponse.TrimStart().StartsWith("<"))
                    {
                        throw new Exception("Tiki đang chặn yêu cầu lấy dữ liệu. Vui lòng thử lại sau.");
                    }

                    using var jsonDoc = JsonDocument.Parse(jsonResponse);
                    var root = jsonDoc.RootElement;

                    string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "Sản phẩm không xác định" : "Sản phẩm không xác định";

                    decimal price = 0;
                    if (root.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number)
                    {
                        price = priceProp.GetDecimal();
                    }

                    string imageUrl = root.TryGetProperty("thumbnail_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
                    string description = root.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";

                    string normalizedLink = $"https://tiki.vn/product-p{tikiProductId}.html";

                    var newProduct = new Product()
                    {
                        ProductLink = normalizedLink,
                        ProductName = name,
                        ImageURL = imageUrl,
                        Description = description,
                        InitialPrice = price,
                        AddedAt = DateTime.UtcNow,
                        LatestPrice = price,
                        LastUpdatedAt = DateTime.UtcNow,
                    };

                    _context.Product.Add(newProduct);
                    await _context.SaveChangesAsync();

                    currentProductId = newProduct.ProductId;
                }
                catch (Exception ex)
                {
                    string detailError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    throw new Exception($"Lỗi trong quá trình thêm sản phẩm: {detailError}");
                }
            }
            else
            {
                currentProductId = existingProduct.ProductId;
            }

            bool isTracking = await _context.Item.AnyAsync(x => x.UserId == user.UserId && x.ProductId == currentProductId);

            if (!isTracking)
            {
                var newItem = new Item()
                {
                    UserId = user.UserId,
                    ProductId = currentProductId
                };

                _context.Item.Add(newItem);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<List<Product>> getAll(string userEmail)
        {
            var user = await _context.User.FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) return new List<Product>();

            var trackedProductIds = await _context.Item
                .Where(i => i.UserId == user.UserId)
                .Select(i => i.ProductId)
                .ToListAsync();

            return await _context.Product
                .Where(p => trackedProductIds.Contains(p.ProductId))
                .OrderByDescending(p => p.AddedAt)
                .ToListAsync();
        }

        public async Task<Product> getById(Guid id)
        {
            return await _context.Product.FirstOrDefaultAsync(x => x.ProductId == id);
        }

        public async Task<List<SuggestionProductDTO>> getAllBetterProducts(Guid productId)
        {
            var originalProduct = await _context.Product.FirstOrDefaultAsync(x => x.ProductId == productId);
            if (originalProduct == null)
                throw new Exception("Không tìm thấy sản phẩm gốc trong hệ thống.");

            string searchKeyword = ExtractSearchKeyword(originalProduct.ProductName);
            if (string.IsNullOrEmpty(searchKeyword))
                return new List<SuggestionProductDTO>();

            string jsonResponse = string.Empty;

            // Áp dụng bọc luồng tĩnh cho việc tìm kiếm
            await _tikiRateLimiter.WaitAsync();
            try
            {
                var client = _httpClientFactory.CreateClient();
                SetupBrowserHeaders(client);

                await Task.Delay(Random.Shared.Next(500, 1500));

                string apiUrl = $"https://tiki.vn/api/v2/products?limit=10&q={Uri.EscapeDataString(searchKeyword)}";
                var response = await client.GetAsync(apiUrl);

                if (!response.IsSuccessStatusCode)
                    return new List<SuggestionProductDTO>();

                jsonResponse = await response.Content.ReadAsStringAsync();
            }
            finally
            {
                _tikiRateLimiter.Release();
            }

            if (jsonResponse.TrimStart().StartsWith("<"))
            {
                return new List<SuggestionProductDTO>();
            }

            using var jsonDoc = JsonDocument.Parse(jsonResponse);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return new List<SuggestionProductDTO>();

            var betterProducts = new List<SuggestionProductDTO>();

            foreach (var item in dataElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out _)) continue;

                string currentName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

                decimal suggestionPrice = 0;
                if (item.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number)
                {
                    suggestionPrice = priceProp.GetDecimal();
                }

                if (!IsValidSimilarProduct(originalProduct.ProductName, currentName))
                    continue;

                if (suggestionPrice > 0 && suggestionPrice < originalProduct.InitialPrice && suggestionPrice > (originalProduct.InitialPrice * 0.4m))
                {
                    string imageUrl = item.TryGetProperty("thumbnail_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
                    string urlPath = item.TryGetProperty("url_path", out var urlProp) ? urlProp.GetString() ?? "" : "";

                    betterProducts.Add(new SuggestionProductDTO
                    {
                        ProductName = currentName,
                        Price = suggestionPrice,
                        ImageURL = imageUrl,
                        ProductLink = string.IsNullOrEmpty(urlPath) ? "" : "https://tiki.vn/" + urlPath,
                    });
                }
            }

            return betterProducts.OrderBy(x => x.Price).ToList();
        }

        private string RemoveVietnameseDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            for (int i = 0; i < normalizedString.Length; i++)
            {
                char c = normalizedString[i];
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

            keyword = Regex.Replace(keyword, @"[^\p{L}\p{N}]", " ");
            keyword = Regex.Replace(keyword, @"\s+", " ").Trim();

            var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(6));
        }

        private bool IsValidSimilarProduct(string originalName, string searchName)
        {
            var lowerOriginalNoMark = RemoveVietnameseDiacritics(originalName.ToLower());
            var lowerSearchNoMark = RemoveVietnameseDiacritics(searchName.ToLower());

            string[] accessoriesBlacklist = {
                "op lung", "bao da", "kinh cuong luc", "mieng dan",
                "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc"
            };

            bool originalIsAccessory = accessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool searchIsAccessory = accessoriesBlacklist.Any(x => lowerSearchNoMark.Contains(x));

            if (!originalIsAccessory && searchIsAccessory) return false;

            var originalWords = lowerOriginalNoMark.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (originalWords.Length >= 2)
            {
                string entityWord1 = originalWords[0];

                if (!lowerSearchNoMark.Contains(entityWord1))
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> deleteProduct(Guid id, string userEmail)
        {
            var user = await _context.User.FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) throw new Exception("Không tìm thấy người dùng");

            var productItem = await _context.Item.FirstOrDefaultAsync(x => x.ProductId == id && x.UserId == user.UserId);

            if (productItem == null)
            {
                throw new Exception("Sản phẩm không tồn tại trong danh sách theo dõi của bạn");
            }

            _context.Item.Remove(productItem);
            await _context.SaveChangesAsync();

            return true;
        }
    }
}