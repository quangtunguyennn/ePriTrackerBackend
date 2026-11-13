using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Scraping.Gearvn
{
    /// <summary>Một chương trình khuyến mãi trên trang /khuyen-mai của GearVN.</summary>
    public sealed class GearvnPromotion
    {
        public string Id { get; init; }
        public string Title { get; init; }
        public string Slug { get; init; }

        /// <summary>Nội dung ưu đãi ngắn, vd "Bàn phím Asus TUF Gaming K3 Gen II Hatsune Miku chỉ còn 990K".</summary>
        public string Offer { get; init; }

        /// <summary>"Quà tặng" / "Giảm giá".</summary>
        public string DealType { get; init; }

        public DateTimeOffset? StartAt { get; init; }
        public DateTimeOffset? EndAt { get; init; }
        public string BannerUrl { get; init; }
        public IReadOnlyList<GearvnPromotionProduct> Products { get; init; } = Array.Empty<GearvnPromotionProduct>();
    }

    public sealed class GearvnPromotionProduct
    {
        public string Sku { get; init; }
        public string Name { get; init; }

        /// <summary>Đường dẫn tương đối, vd "/products/logitech-g502-hero".</summary>
        public string Url { get; init; }

        /// <summary>"condition" = sản phẩm chính phải mua; "gift" = quà tặng / mua kèm.</summary>
        public string Role { get; init; }

        public string DealLabel { get; init; }
    }

    /// <summary>
    /// Đọc danh sách chương trình từ HTML trang https://gearvn.com/khuyen-mai (Next.js, kiểm tra 01/11/2026).
    /// Dữ liệu không nằm trong thẻ HTML mà trong payload React Server Components: các đoạn <c>self.__next_f.push([1,"..."])</c>
    /// ghép lại thành văn bản, trong đó có mảng JSON <c>"promotions":[...]</c> (id, title, slug, offer, dealType, startAt, endAt,
    /// bannerUrl, primaryProduct, products). Sản phẩm trùng được viết tắt thành chuỗi tham chiếu
    /// <c>"$45:props:...:promotions:1:primaryProduct"</c> → thay bằng primaryProduct của chương trình thứ 1. Hàm thuần để test.
    /// </summary>
    public static class GearvnPromotionParser
    {
        public const string ListUrl = "https://gearvn.com/khuyen-mai";

        private static readonly Regex FlightChunkRegex = new(@"self\.__next_f\.push\(\[1,(""(?:[^""\\]|\\.)*"")\]\)", RegexOptions.Compiled);
        private static readonly Regex PrimaryProductRefRegex = new(@"promotions:(\d+):primaryProduct$", RegexOptions.Compiled);
        private const string PromotionsKey = "\"promotions\":[";

        /// <summary>Link sự kiện lưu trong DB / FE mở: trang khuyến mãi mở sẵn hộp thoại của chương trình.</summary>
        public static string PromotionUrl(string slug) => $"{ListUrl}?promotion={Uri.EscapeDataString(slug)}";

        /// <summary>Link trang danh sách (trang 1 không có tham số).</summary>
        public static string ListPageUrl(int page) => page <= 1 ? ListUrl : $"{ListUrl}?page={page}";

        public static IReadOnlyList<GearvnPromotion> Parse(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return Array.Empty<GearvnPromotion>();

            string flight = ReadFlightPayload(html);
            int start = flight.IndexOf(PromotionsKey, StringComparison.Ordinal);
            if (start < 0) return Array.Empty<GearvnPromotion>();

            string json = SliceJson(flight, start + PromotionsKey.Length - 1);
            if (json == null) return Array.Empty<GearvnPromotion>();

            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();

            return items.Select(item => new GearvnPromotion
            {
                Id = GetString(item, "id"),
                Title = GetString(item, "title"),
                Slug = GetString(item, "slug"),
                Offer = GetString(item, "offer"),
                DealType = GetString(item, "dealType"),
                StartAt = GetDate(item, "startAt"),
                EndAt = GetDate(item, "endAt"),
                BannerUrl = GetString(item, "bannerUrl") ?? GetString(item, "bannerCardUrl"),
                Products = ReadProducts(item, items)
            })
            .Where(p => !string.IsNullOrWhiteSpace(p.Slug) && !string.IsNullOrWhiteSpace(p.Title))
            .ToList();
        }

        private static List<GearvnPromotionProduct> ReadProducts(JsonElement promotion, List<JsonElement> allPromotions)
        {
            var products = new List<GearvnPromotionProduct>();
            if (!promotion.TryGetProperty("products", out var array) || array.ValueKind != JsonValueKind.Array) return products;

            foreach (var entry in array.EnumerateArray())
            {
                var product = entry;
                if (entry.ValueKind == JsonValueKind.String)
                {
                    // Tham chiếu tới primaryProduct của chương trình khác trong cùng mảng
                    var match = PrimaryProductRefRegex.Match(entry.GetString() ?? "");
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out int index) || index >= allPromotions.Count) continue;
                    if (!allPromotions[index].TryGetProperty("primaryProduct", out product)) continue;
                }
                if (product.ValueKind != JsonValueKind.Object) continue;

                string url = GetString(product, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                products.Add(new GearvnPromotionProduct
                {
                    Sku = GetString(product, "sku"),
                    Name = GetString(product, "name"),
                    Url = url,
                    Role = GetString(product, "role"),
                    DealLabel = GetString(product, "dealLabel")
                });
            }
            return products;
        }

        // Ghép các đoạn self.__next_f.push([1,"..."]) – mỗi đoạn là một chuỗi JSON (đã escape)
        private static string ReadFlightPayload(string html)
        {
            var sb = new StringBuilder();
            foreach (Match m in FlightChunkRegex.Matches(html))
            {
                try { sb.Append(JsonSerializer.Deserialize<string>(m.Groups[1].Value)); }
                catch (JsonException) { /* đoạn hỏng → bỏ qua */ }
            }
            return sb.ToString();
        }

        // Cắt một giá trị JSON (mảng/object) bắt đầu tại start bằng đếm ngoặc, bỏ qua ngoặc nằm trong chuỗi
        private static string SliceJson(string text, int start)
        {
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    for (i++; i < text.Length && text[i] != '"'; i++)
                        if (text[i] == '\\') i++;
                    continue;
                }
                if (c == '[' || c == '{') depth++;
                else if (c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        // Quy ước payload React Server Components: "$undefined" = không có giá trị, "$45:..." = tham chiếu, chuỗi thật bắt đầu
        // bằng "$" được viết "$$..." → bỏ tham chiếu/undefined (không phải chữ hiển thị được)
        private static string GetString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.String) return null;
            string value = p.GetString();
            if (string.IsNullOrEmpty(value) || !value.StartsWith('$')) return value;
            return value.StartsWith("$$", StringComparison.Ordinal) ? value[1..] : null;
        }

        private static DateTimeOffset? GetDate(JsonElement element, string property)
            => DateTimeOffset.TryParse(GetString(element, property), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    }
}
