using System.Globalization;
using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// Strategy cho CellphoneS: đọc dữ liệu schema.org (JSON-LD) trong trang sản phẩm.
    /// KHÔNG cài <see cref="ISearchablePlatformScraper"/> vì robots.txt của CellphoneS cấm trang tìm kiếm (/catalogsearch/)
    /// – gợi ý cho sản phẩm CellphoneS được tìm trên các sàn khác.
    /// </summary>
    public class CellphoneSScraper : IPlatformScraper
    {
        public const string PlatformName = "CellphoneS";

        /// <summary>Thuộc tính HtmlJsonLdFetchStrategy gắn thêm vào JSON: HTML mô tả đầy đủ (JSON-LD chỉ có 1 câu SEO).</summary>
        public const string DescriptionHtmlField = "x-descriptionHtml";

        /// <summary>"Tính năng nổi bật" + bài viết chi tiết. Máy cũ / sản phẩm ngừng bán không có 2 khối này.</summary>
        public const string DescriptionXPath = "//div[@id='cpsKsp'] | //div[@id='cpsContentSEO']";

        private readonly IHybridFetcher _fetcher;

        public CellphoneSScraper([FromKeyedServices(PlatformName)] IHybridFetcher fetcher)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        }

        public string Platform => PlatformName;

        public bool CanHandle(string productUrl) => CellphoneSUrlParser.IsCellphoneSUrl(productUrl);

        public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl)
        {
            normalizedUrl = CellphoneSUrlParser.TryGetSlug(productUrl, out var slug)
                ? CellphoneSUrlParser.NormalizeProductUrl(slug)
                : null;
            return normalizedUrl != null;
        }

        public async Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            if (!CellphoneSUrlParser.TryGetSlug(productUrl, out var slug))
                throw new ArgumentException("Link CellphoneS không chứa mã sản phẩm.", nameof(productUrl));

            string url = CellphoneSUrlParser.NormalizeProductUrl(slug);
            JsonElement product = await _fetcher.FetchJsonAsync(url, cancellationToken: cancellationToken);
            JsonElement? offer = JsonLdProductReader.FirstOffer(product);

            return new ScrapedProduct
            {
                ExternalId = slug,
                Name = GetString(product, "name"),
                Price = offer is JsonElement o ? ParsePrice(o) : null,
                ImageUrl = GetImage(product),
                Description = GetString(product, DescriptionHtmlField) ?? GetString(product, "description"),
                Url = url,
                IsAvailable = offer is JsonElement a ? ParseAvailability(GetString(a, "availability")) : null
            };
        }

        /// <summary>
        /// schema.org ItemAvailability, ví dụ "https://schema.org/InStock". PreOrder (đặt trước, chưa có hàng) tính là hết hàng
        /// – thống nhất với "upcoming" của Tiki. Giá trị lạ → null.
        /// </summary>
        public static bool? ParseAvailability(string availability)
        {
            if (string.IsNullOrWhiteSpace(availability)) return null;

            return availability.Trim().Split('/').Last().ToLowerInvariant() switch
            {
                "instock" or "limitedavailability" or "onlineonly" or "instoreonly" => true,
                "outofstock" or "soldout" or "discontinued" or "preorder" => false,
                _ => null
            };
        }

        /// <summary>
        /// Giá trong JSON-LD có thể là chuỗi hoặc số. Giá 0 = sàn không bán (ngừng kinh doanh) → coi như không có giá.
        /// </summary>
        private static decimal? ParsePrice(JsonElement offer)
        {
            if (!offer.TryGetProperty("price", out var p)) return null;

            decimal value;
            if (p.ValueKind == JsonValueKind.Number) value = p.GetDecimal();
            else if (p.ValueKind != JsonValueKind.String || !decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return null;

            return value > 0 ? value : null;
        }

        // "image" có thể là chuỗi, mảng chuỗi, hoặc ImageObject { url }
        private static string GetImage(JsonElement product)
        {
            if (!product.TryGetProperty("image", out var image)) return null;
            if (image.ValueKind == JsonValueKind.Array) image = image.EnumerateArray().FirstOrDefault();
            if (image.ValueKind == JsonValueKind.String) return image.GetString();
            return image.ValueKind == JsonValueKind.Object ? GetString(image, "url") : null;
        }

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}
