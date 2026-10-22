using System.Text.Json;
using HtmlAgilityPack;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>
    /// Tìm khối JSON-LD schema.org "Product" của TRANG SẢN PHẨM trong HTML.
    /// Trang danh mục cũng có khối Product nhưng offers là AggregateOffer (khoảng giá) → bị loại.
    /// </summary>
    public static class JsonLdProductReader
    {
        private static readonly JsonDocumentOptions ParseOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        /// <returns>Khối Product có offers kiểu Offer; null nếu trang không có.</returns>
        public static JsonElement? FindProduct(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            var document = new HtmlDocument();
            document.LoadHtml(html);
            return FindProduct(document);
        }

        /// <returns>Khối Product có offers kiểu Offer; null nếu trang không có.</returns>
        public static JsonElement? FindProduct(HtmlDocument document)
        {
            var scripts = document.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (scripts == null) return null;

            foreach (var script in scripts)
            {
                JsonDocument json;
                try { json = JsonDocument.Parse(script.InnerText, ParseOptions); }
                catch (JsonException) { continue; } // một khối JSON hỏng không làm hỏng cả trang

                using (json)
                {
                    foreach (var node in Flatten(json.RootElement))
                    {
                        if (HasType(node, "Product") && FirstOffer(node) is JsonElement offer && HasType(offer, "Offer"))
                            return node.Clone();
                    }
                }
            }

            return null;
        }

        /// <summary>offers có thể là object hoặc mảng – lấy phần tử đầu.</summary>
        public static JsonElement? FirstOffer(JsonElement product)
        {
            if (product.ValueKind != JsonValueKind.Object || !product.TryGetProperty("offers", out var offers)) return null;
            if (offers.ValueKind == JsonValueKind.Object) return offers;
            if (offers.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in offers.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) return item;
            }
            return null;
        }

        // Gốc JSON-LD có thể là object, mảng, hoặc object chứa "@graph"
        private static IEnumerable<JsonElement> Flatten(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) yield return item;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                yield return root;
                if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in graph.EnumerateArray()) yield return item;
                }
            }
        }

        // "@type" có thể là chuỗi hoặc mảng chuỗi
        private static bool HasType(JsonElement node, string type)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("@type", out var t)) return false;
            if (t.ValueKind == JsonValueKind.String) return string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase);
            return t.ValueKind == JsonValueKind.Array
                && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && string.Equals(x.GetString(), type, StringComparison.OrdinalIgnoreCase));
        }
    }
}
