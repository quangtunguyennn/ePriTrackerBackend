using System.Globalization;
using System.Text.Json;

namespace ePriTrackerBackend.Scraping.Hacom
{
    /// <summary>Một sản phẩm deal trên trang https://hacom.vn/deal (vd "Flash Sale Cuối Tuần").</summary>
    public sealed class HacomDeal
    {
        public string Title { get; init; }
        public DateTimeOffset? FromDate { get; init; }
        public DateTimeOffset? ToDate { get; init; }

        /// <summary>Slug trang sản phẩm, vd "man-hinh-aoc-24b36xe-moao0269".</summary>
        public string Slug { get; init; }

        public string Name { get; init; }
        public string ImageUrl { get; init; }
        public decimal DealPrice { get; init; }

        /// <summary>Giá niêm yết ("giá thị trường") – HACOM gạch ngang giá này.</summary>
        public decimal MarketPrice { get; init; }

        /// <summary>Giá bán thường khi không có deal.</summary>
        public decimal RegularPrice { get; init; }

        public bool IsActive { get; init; }
        public int Order { get; init; }
    }

    /// <summary>
    /// Đọc JSON của API trang deal HACOM: GET https://apis-web.hacom.vn/api-client/api/v1/TblProductDeal/get-list
    /// → { success, data: [{ dealTitle, fromDate, toDate, url, productName, productImgUrl, dealUnitSellingPrice, marketPrice,
    /// unitSellingPrice, active: "A", orderNumber, ... }] } (kiểm tra 01/11/2026). Hàm thuần để test.
    /// </summary>
    public static class HacomDealParser
    {
        public static IReadOnlyList<HacomDeal> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Phản hồi deal HACOM rỗng.");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new FormatException("Phản hồi deal HACOM không có mảng data (API đổi định dạng?).");
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new FormatException("API deal HACOM báo lỗi: " + GetString(root, "message"));

            return data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new HacomDeal
                {
                    Title = GetString(item, "dealTitle")?.Trim(),
                    FromDate = GetDate(item, "fromDate"),
                    ToDate = GetDate(item, "toDate"),
                    Slug = GetString(item, "url")?.Trim().Trim('/'),
                    Name = GetString(item, "productName")?.Trim(),
                    ImageUrl = GetString(item, "productImgUrl"),
                    DealPrice = GetDecimal(item, "dealUnitSellingPrice"),
                    MarketPrice = GetDecimal(item, "marketPrice"),
                    RegularPrice = GetDecimal(item, "unitSellingPrice"),
                    IsActive = string.Equals(GetString(item, "active"), "A", StringComparison.OrdinalIgnoreCase),
                    Order = (int)GetDecimal(item, "orderNumber")
                })
                .ToList();
        }

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static decimal GetDecimal(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            return p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private static DateTimeOffset? GetDate(JsonElement element, string property)
            => DateTimeOffset.TryParse(GetString(element, property), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    }
}
