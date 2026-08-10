using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Repositories
{
    public class ProductRepository : IProductRepository
    {
        private readonly ePriTrackerContext _context;
        public ProductRepository(ePriTrackerContext context)
        {
            _context = context;
        }

        public async Task AddProduct(string productLink, string userEmail)
        {
            var user = await _context.User.FirstOrDefaultAsync(x => x.Email == userEmail);
            if (user == null) throw new Exception("Không tìm thấy thông tin người dùng.");

            var existingProduct = await _context.Product.FirstOrDefaultAsync(x => x.ProductLink == productLink);
            Guid currentProductId;

            if (existingProduct == null)
            {
                // -- DÙNG TIKI API ĐỂ LẤY DỮ LIỆU --
                try
                {
                    // 1. Tách Product ID từ link Tiki (Ví dụ: tách lấy 279411794 từ chuỗi -p279411794.html)
                    var match = Regex.Match(productLink, @"-p(\d+)\.html");
                    if (!match.Success)
                    {
                        throw new Exception("Link Tiki không hợp lệ. Không tìm thấy Product ID.");
                    }
                    string tikiProductId = match.Groups[1].Value;

                    // 2. Khởi tạo HttpClient và gọi Tiki API
                    using var client = new HttpClient();
                    // Vẫn cần User-Agent để Tiki không tưởng mình là bot phá hoại
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");

                    string apiUrl = $"https://tiki.vn/api/v2/products/{tikiProductId}";
                    var response = await client.GetAsync(apiUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception($"Lấy dữ liệu từ Tiki thất bại. Status Code: {response.StatusCode}");
                    }

                    // 3. Đọc dữ liệu JSON trả về
                    string jsonResponse = await response.Content.ReadAsStringAsync();

                    // Parse JSON động (không cần tạo Class rườm rà)
                    using var jsonDoc = JsonDocument.Parse(jsonResponse);
                    var root = jsonDoc.RootElement;

                    // 4. Trích xuất đúng các key dựa theo JSON mẫu bạn cung cấp
                    string name = root.GetProperty("name").GetString() ?? "Sản phẩm không xác định";
                    decimal price = root.GetProperty("price").GetDecimal(); // Lấy trực tiếp số thập phân, không cần Regex xóa chữ "đ" nữa
                    string imageUrl = root.GetProperty("thumbnail_url").GetString() ?? "";
                    string description = root.GetProperty("description").GetString() ?? "";
                    // 5. Lưu vào Database
                    var newProduct = new Product()
                    {
                        ProductLink = productLink,
                        ProductName = name,
                        ImageURL = imageUrl,
                        Description = description,
                        InitialPrice = price, // Lưu ý check lại tên biến này xem có khớp DB chưa nhé
                        AddedAt = DateTime.UtcNow,
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

            // Gán sản phẩm này vào danh sách theo dõi của User
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

            // TỐI ƯU CÂU LỆNH BẰNG LINQ (Thay cho vòng lặp foreach N+1 Query)
            // Lấy ra danh sách các ProductId mà user này đang theo dõi
            var trackedProductIds = await _context.Item
                .Where(i => i.UserId == user.UserId)
                .Select(i => i.ProductId)
                .ToListAsync();

            // Truy vấn trực tiếp các Product có ID nằm trong danh sách trên
            var productList = await _context.Product
                .Where(p => trackedProductIds.Contains(p.ProductId))
                .OrderByDescending(p => p.AddedAt)
                .ToListAsync();

            return productList;
            
        }
      
        public async Task<Product> getById(Guid id)
        {
            return await _context.Product.FirstOrDefaultAsync(x => x.ProductId == id);
        }

        public async Task<List<SuggestionProductDTO>> getAllBetterProducts(Guid productId)
        {
            // 1. Lấy sản phẩm gốc từ DB
            var originalProduct = await _context.Product.FirstOrDefaultAsync(x => x.ProductId == productId);
            if (originalProduct == null)
                throw new Exception("Không tìm thấy sản phẩm gốc trong hệ thống.");

            // 2. Bóc tách từ khóa cốt lõi để Search
            string searchKeyword = ExtractSearchKeyword(originalProduct.ProductName);
            if (string.IsNullOrEmpty(searchKeyword))
                return new List<SuggestionProductDTO>();

            // 3. Gọi API Search của Tiki
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0");

            string apiUrl = $"https://tiki.vn/api/v2/products?limit=10&q={Uri.EscapeDataString(searchKeyword)}";
            var response = await client.GetAsync(apiUrl);

            if (!response.IsSuccessStatusCode)
                return new List<SuggestionProductDTO>();

            string jsonResponse = await response.Content.ReadAsStringAsync();
            using var jsonDoc = JsonDocument.Parse(jsonResponse);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return new List<SuggestionProductDTO>();

            var betterProducts = new List<SuggestionProductDTO>();

            // 4. Lọc và Map trực tiếp sang DTO
            foreach (var item in dataElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out _)) continue;

                string currentName = item.GetProperty("name").GetString() ?? "";
                decimal suggestionPrice = item.GetProperty("price").GetDecimal();

                // Kiểm tra qua bộ lọc thuật toán (Loại phụ kiện, bắt buộc trùng từ khóa chính)
                if (!IsValidSimilarProduct(originalProduct.ProductName, currentName))
                    continue;

                // Chỉ lấy sản phẩm RẺ HƠN sản phẩm gốc và giá hợp lý (tránh hàng giả/mô hình quá rẻ)
                if (suggestionPrice > 0 && suggestionPrice < originalProduct.InitialPrice && suggestionPrice > (originalProduct.InitialPrice * 0.4m))
                {
                    betterProducts.Add(new SuggestionProductDTO
                    {
                        ProductName = currentName,
                        Price = suggestionPrice,
                        ImageURL = item.GetProperty("thumbnail_url").GetString() ?? "",
                        ProductLink = "https://tiki.vn/" + (item.GetProperty("url_path").GetString() ?? ""),
                        
                    });
                }
            }

            // 5. Trả về DTO danh sách sắp xếp theo Giá tăng dần (Rẻ nhất xếp đầu)
            return betterProducts.OrderBy(x => x.Price).ToList();
        }

        private string RemoveVietnameseDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Chuyển chuỗi về dạng phân tách các ký tự có dấu (FormD)
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            for (int i = 0; i < normalizedString.Length; i++)
            {
                char c = normalizedString[i];
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);

                // Bỏ qua các ký tự là dấu (NonSpacingMark)
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            // Trả về chuỗi và xử lý riêng chữ 'đ'/'Đ' của tiếng Việt
            return stringBuilder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace('đ', 'd')
                .Replace('Đ', 'D');
        }
        private string ExtractSearchKeyword(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;

            // 1. CẮT BỎ PHẦN MÔ TẢ PHỤ: Thường nằm sau các dấu -, |, (, [, hoặc dấu phẩy
            var splitChars = new char[] { '-', '|', '(', '[', ',' };
            string coreName = rawName.Split(splitChars, StringSplitOptions.RemoveEmptyEntries)[0];

            string keyword = coreName.ToLower();

            // 2. CHỈ XÓA TỪ MARKETING (Lưu ý: KHÔNG xóa tên loại như điện thoại, loa, tivi)
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

            // 3. Chuẩn hóa khoảng trắng
            keyword = Regex.Replace(keyword, @"[^\p{L}\p{N}]", " ");
            keyword = Regex.Replace(keyword, @"\s+", " ").Trim();

            // 4. (Tùy chọn cực mạnh): Chỉ lấy tối đa 5-6 từ đầu tiên để Search API cho ra kết quả rộng và chính xác nhất
            // Ví dụ: "loa bluetooth jbl charge 5 40w" (6 từ) là quá đủ để search
            var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(6));
        }

        private bool IsValidSimilarProduct(string originalName, string searchName)
        {
            var lowerOriginalNoMark = RemoveVietnameseDiacritics(originalName.ToLower());
            var lowerSearchNoMark = RemoveVietnameseDiacritics(searchName.ToLower());

            // --- BƯỚC 1: LỌC BLACKLIST (Chống nhận diện nhầm phụ kiện) ---
            string[] accessoriesBlacklist = {
        "op lung", "bao da", "kinh cuong luc", "mieng dan",
        "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc"
    };

            bool originalIsAccessory = accessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool searchIsAccessory = accessoriesBlacklist.Any(x => lowerSearchNoMark.Contains(x));

            if (!originalIsAccessory && searchIsAccessory) return false;

            // --- BƯỚC 2: RÀNG BUỘC TỪ KHÓA CỐT LÕI (WHITELIST) ---
            // Lấy 2 từ đầu tiên của sản phẩm gốc (thường là Loại sản phẩm + Nhãn hàng. Ví dụ: "loa jbl", "iphone 15")
            var originalWords = lowerOriginalNoMark.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (originalWords.Length >= 2)
            {
                string entityWord1 = originalWords[0]; // VD: "loa"
                string entityWord2 = originalWords[1]; // VD: "jbl" hoặc "bluetooth"

                // Sản phẩm search được ít nhất phải chứa từ khóa định danh đầu tiên
                // Nếu gốc là "loa...", thì kết quả search cũng phải có chữ "loa"
                if (!lowerSearchNoMark.Contains(entityWord1))
                {
                    return false;
                }

                // Tùy chọn: Ràng buộc chặt hơn - nếu có chứa thông số/hãng ở từ thứ 2, thì ưu tiên check luôn
                // if (!lowerSearchNoMark.Contains(entityWord2)) return false; 
            }

            // --- BƯỚC 3: ƯU TIÊN SẢN PHẨM CÙNG THÔNG SỐ (Model, Dung lượng) ---
            // Tìm các từ có chứa SỐ trong sản phẩm gốc (VD: "charge5", "256gb", "40w")
            // Đây là các thông số/model cực kỳ quan trọng
            var specRegex = new Regex(@"\b[a-z0-9]*[0-9]+[a-z0-9]*\b");
            var originalSpecs = specRegex.Matches(lowerOriginalNoMark).Select(m => m.Value).ToList();

            if (originalSpecs.Any())
            {
                // Ta có thể đếm xem sản phẩm gợi ý khớp được bao nhiêu thông số
                int matchCount = originalSpecs.Count(spec => lowerSearchNoMark.Contains(spec));

                // Nếu không khớp một thông số nào (ví dụ gốc là 256GB mà gợi ý là 64GB không thỏa mãn)
                // Bạn có thể return false ở đây nếu muốn tracking SIÊU CHÍNH XÁC (Cùng một model)
                // Hiện tại tạm thời để pass qua, nhưng thuật toán này giúp bạn mở rộng sau này.
            }

            return true;
        }

        public async Task<bool> deleteProduct(Guid id)
        {
            var productItem = await _context.Item.FirstOrDefaultAsync(x => x.ProductId ==  id);

            if(productItem == null)
            {
                throw new Exception("product not found");
            }

            _context.Item.Remove(productItem);
            await _context.SaveChangesAsync();


            return true;
        }
    }
}