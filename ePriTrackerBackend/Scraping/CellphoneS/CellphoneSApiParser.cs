using System.Globalization;
using System.Text.Json;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// Đọc JSON trả về từ GraphQL của CellphoneS. Hàm thuần (không gọi mạng) để kiểm thử bằng dữ liệu mẫu.
    /// Thiếu trường / sai kiểu → bỏ qua phần tử đó, không ném lỗi; phản hồi có "errors" → ném lỗi.
    /// </summary>
    public static class CellphoneSApiParser
    {
        public const string SiteUrl = "https://cellphones.com.vn/";
        private const string ImageBaseUrl = "https://cdn2.cellphones.com.vn/358x/media/catalog/product";

        /// <summary>Lấy "data" của phản hồi GraphQL.</summary>
        /// <exception cref="InvalidOperationException">Phản hồi có lỗi GraphQL hoặc không có "data".</exception>
        public static JsonElement Data(JsonElement root)
        {
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new InvalidOperationException("CellphoneS API báo lỗi: " + errors[0].GetRawText());

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("CellphoneS API không trả dữ liệu.");

            return data;
        }

        /// <summary>quick_search.products</summary>
        public static IReadOnlyList<CellphoneSApiProduct> ParseQuickSearch(JsonElement root)
        {
            var data = Data(root);
            if (!data.TryGetProperty("quick_search", out var search) || search.ValueKind != JsonValueKind.Object
                || !search.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
                return Array.Empty<CellphoneSApiProduct>();

            return products.EnumerateArray()
                .Select(p => new CellphoneSApiProduct
                {
                    ProductId = GetLong(p, "product_id"),
                    Name = GetString(p, "name"),
                    UrlPath = GetString(p, "url_path"),
                    DisplayPrice = GetDecimal(p, "display_price"),
                    RootPrice = GetDecimal(p, "display_root_price"),
                    Thumbnail = GetString(p, "thumbnail"),
                    Condition = GetString(p, "product_condition")
                })
                .Where(p => !string.IsNullOrWhiteSpace(p.UrlPath))
                .ToList();
        }

        /// <summary>products[] { general { ... } filterable { ... } } của API danh mục.</summary>
        public static IReadOnlyList<CellphoneSApiProduct> ParseCatalogProducts(JsonElement root)
        {
            var data = Data(root);
            if (!data.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
                return Array.Empty<CellphoneSApiProduct>();

            var result = new List<CellphoneSApiProduct>();
            foreach (var p in products.EnumerateArray())
            {
                if (!p.TryGetProperty("general", out var general) || general.ValueKind != JsonValueKind.Object) continue;
                p.TryGetProperty("filterable", out var filterable);

                long id = GetLong(general, "product_id");
                string urlPath = GetString(general, "url_path");
                if (id <= 0 || string.IsNullOrWhiteSpace(urlPath)) continue;

                bool hasFilterable = filterable.ValueKind == JsonValueKind.Object;
                result.Add(new CellphoneSApiProduct
                {
                    ProductId = id,
                    Name = GetString(general, "name"),
                    UrlPath = urlPath,
                    DisplayPrice = hasFilterable ? GetDecimal(filterable, "display_price") : 0,
                    RootPrice = hasFilterable ? GetDecimal(filterable, "display_root_price") : 0,
                    Thumbnail = hasFilterable ? GetString(filterable, "thumbnail") : null
                });
            }
            return result;
        }

        /// <summary>
        /// dynamic_config(key: "config_hotsale_flashsale").value_json.flashSaleHome.web[].groupUd – các nhóm flash sale đang bày trên trang chủ.
        /// value_json có thể là object hoặc chuỗi JSON.
        /// </summary>
        public static IReadOnlyList<string> ParseHomeFlashSaleGroupUds(JsonElement root)
        {
            var data = Data(root);
            if (!data.TryGetProperty("dynamic_config", out var config) || config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("value_json", out var value))
                return Array.Empty<string>();

            JsonDocument parsed = null;
            try
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    parsed = JsonDocument.Parse(value.GetString() ?? "{}");
                    value = parsed.RootElement;
                }

                if (value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("flashSaleHome", out var home) || home.ValueKind != JsonValueKind.Object)
                    return Array.Empty<string>();

                // Ưu tiên cấu hình bản web, thiếu thì dùng bản app
                foreach (var channel in new[] { "web", "app" })
                {
                    if (!home.TryGetProperty(channel, out var list) || list.ValueKind != JsonValueKind.Array) continue;

                    var uds = list.EnumerateArray()
                        .Select(x => GetString(x, "groupUd"))
                        .Where(ud => !string.IsNullOrWhiteSpace(ud))
                        .Distinct()
                        .ToList();
                    if (uds.Count > 0) return uds;
                }
                return Array.Empty<string>();
            }
            finally
            {
                parsed?.Dispose();
            }
        }

        /// <summary>flash_sale_groups(group_ud) { title_desktop list_program { ... } }</summary>
        public static CellphoneSFlashSaleGroup ParseFlashSaleGroup(JsonElement root)
        {
            var data = Data(root);
            if (!data.TryGetProperty("flash_sale_groups", out var group)) return null;
            if (group.ValueKind == JsonValueKind.Array) group = group.EnumerateArray().FirstOrDefault();
            if (group.ValueKind != JsonValueKind.Object) return null;

            var programs = new List<CellphoneSFlashSaleProgram>();
            if (group.TryGetProperty("list_program", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in list.EnumerateArray())
                {
                    if (!TryGetDate(p, "started_at", out var start) || !TryGetDate(p, "ended_at", out var end)) continue;
                    programs.Add(new CellphoneSFlashSaleProgram
                    {
                        Id = (int)GetLong(p, "id"),
                        Title = GetString(p, "title"),
                        IsActive = GetBool(p, "is_active"),
                        StartsAt = start,
                        EndsAt = end
                    });
                }
            }

            return new CellphoneSFlashSaleGroup
            {
                Ud = GetString(group, "ud"),
                Title = GetString(group, "title_desktop") ?? GetString(group, "title_mobile"),
                Programs = programs
            };
        }

        /// <summary>flash_sale_programs(flash_sale_id) { flash_sale_slots { ... flash_sale_prices { ... } } }</summary>
        public static IReadOnlyList<CellphoneSFlashSaleSlot> ParseFlashSaleSlots(JsonElement root)
        {
            var data = Data(root);
            if (!data.TryGetProperty("flash_sale_programs", out var program)) return Array.Empty<CellphoneSFlashSaleSlot>();
            if (program.ValueKind == JsonValueKind.Array) program = program.EnumerateArray().FirstOrDefault();
            if (program.ValueKind != JsonValueKind.Object
                || !program.TryGetProperty("flash_sale_slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
                return Array.Empty<CellphoneSFlashSaleSlot>();

            var result = new List<CellphoneSFlashSaleSlot>();
            foreach (var s in slots.EnumerateArray())
            {
                long id = GetLong(s, "product_id");
                if (id <= 0) continue;

                JsonElement price = default;
                if (s.TryGetProperty("flash_sale_prices", out var prices) && prices.ValueKind == JsonValueKind.Array)
                    price = prices.EnumerateArray().FirstOrDefault();
                bool hasPrice = price.ValueKind == JsonValueKind.Object;

                result.Add(new CellphoneSFlashSaleSlot
                {
                    ProductId = id,
                    Name = GetString(s, "product_name")?.Trim(),
                    Thumbnail = GetString(s, "product_thumbnail"),
                    IsActive = GetBool(s, "is_active"),
                    Sold = (int)GetLong(s, "sold"),
                    MaxSlot = (int)GetLong(s, "max_slot"),
                    NormalPrice = hasPrice ? GetDecimal(price, "product_normal_price") : 0,
                    SpecialPrice = hasPrice ? GetDecimal(price, "product_special_price") : 0,
                    FlashPrice = hasPrice ? GetDecimal(price, "product_flash_sale_price") : 0
                });
            }
            return result;
        }

        /// <summary>url_info(request_path) { product_id type }. Không phải trang sản phẩm → null.</summary>
        public static long? ParseUrlInfoProductId(JsonElement root)
        {
            var data = Data(root);
            if (!data.TryGetProperty("url_info", out var info) || info.ValueKind != JsonValueKind.Object) return null;

            string type = GetString(info, "type");
            long id = GetLong(info, "product_id");
            return id > 0 && (type == null || type.Equals("product", StringComparison.OrdinalIgnoreCase)) ? id : null;
        }

        public static string ProductUrl(string urlPath)
            => string.IsNullOrWhiteSpace(urlPath) ? null : SiteUrl + urlPath.Trim().TrimStart('/');

        /// <summary>Ảnh trong API là đường dẫn tương đối dưới media/catalog/product.</summary>
        public static string ImageUrl(string thumbnail)
        {
            if (string.IsNullOrWhiteSpace(thumbnail)) return null;
            thumbnail = thumbnail.Trim();
            if (thumbnail.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return thumbnail;
            return ImageBaseUrl + (thumbnail.StartsWith('/') ? "" : "/") + thumbnail;
        }

        private static string GetString(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        private static long GetLong(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
            return p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        private static decimal GetDecimal(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            return p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private static bool GetBool(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var p)
               && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n != 0));

        private static bool TryGetDate(JsonElement element, string property, out DateTimeOffset value)
        {
            value = default;
            string text = GetString(element, property);
            return text != null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }
    }
}
