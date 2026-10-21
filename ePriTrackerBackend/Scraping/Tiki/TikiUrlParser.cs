using System.Text.RegularExpressions;
using System.Web;

namespace ePriTrackerBackend.Scraping.Tiki
{
    /// <summary>
    /// Mọi quy tắc về link Tiki gom tại một chỗ (trước đây rải ở ProductRepository, PriceCrawlerService,
    /// SuggestionCrawlService, EventRepository).
    /// </summary>
    public static class TikiUrlParser
    {
        public const string BaseUrl = "https://tiki.vn";
        public const string DefaultEventTitle = "Sự kiện Tiki Khuyến Mãi";

        private static readonly Regex ProductIdRegex = new Regex(@"-p(\d+)\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Host là tiki.vn hoặc tên miền con. Chấp nhận cả link thiếu "https://".</summary>
        public static bool IsTikiUrl(string url)
        {
            if (!TryParseAbsolute(url, out var uri)) return false;
            string host = uri.Host;
            return host.Equals("tiki.vn", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".tiki.vn", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lấy ID sản phẩm từ đoạn "-p123.html" (không phân biệt hoa thường).</summary>
        public static bool TryGetProductId(string url, out string productId)
        {
            var match = string.IsNullOrEmpty(url) ? Match.Empty : ProductIdRegex.Match(url);
            productId = match.Success ? match.Groups[1].Value : null;
            return match.Success;
        }

        /// <summary>Dạng link chuẩn lưu trong DB: https://tiki.vn/product-p{id}.html</summary>
        public static string NormalizeProductUrl(string productId) => $"{BaseUrl}/product-p{productId}.html";

        /// <summary>
        /// Đường dẫn API chi tiết sản phẩm. Giữ tham số spid (mã người bán) nếu link có,
        /// vì cùng một sản phẩm mỗi người bán có giá khác nhau.
        /// </summary>
        /// <exception cref="ArgumentException">Link không chứa ID sản phẩm.</exception>
        public static string BuildProductApiPath(string productUrl)
        {
            if (!TryGetProductId(productUrl, out var productId))
                throw new ArgumentException("Link Tiki không chứa Product ID.", nameof(productUrl));

            string spid = TryParseAbsolute(productUrl, out var uri)
                ? HttpUtility.ParseQueryString(uri.Query)["spid"]
                : null;

            return string.IsNullOrEmpty(spid)
                ? $"/api/v2/products/{productId}"
                : $"/api/v2/products/{productId}?spid={spid}";
        }

        public static string BuildSearchApiPath(string keyword, int limit)
            => $"/api/v2/products?limit={limit}&q={Uri.EscapeDataString(keyword)}";

        /// <summary>Đổi url_path trong JSON của Tiki thành link đầy đủ. Giữ nguyên nếu đã là link tuyệt đối.</summary>
        public static string ToAbsoluteUrl(string urlPath)
        {
            if (string.IsNullOrWhiteSpace(urlPath)) return string.Empty;
            if (urlPath.StartsWith("http://") || urlPath.StartsWith("https://")) return urlPath;
            return $"{BaseUrl}/{urlPath.TrimStart('/')}";
        }

        /// <summary>
        /// Dựng tiêu đề sự kiện từ link banner: ".../fonterra-chinh-hang?x=1" → "Fonterra Chinh Hang".
        /// </summary>
        public static string FormatEventTitle(string rawUrl)
        {
            if (string.IsNullOrEmpty(rawUrl)) return DefaultEventTitle;

            try
            {
                // Lấy phần đuôi cùng của URL (vd: fonterra-chinh-hang)
                string slug = rawUrl.Split('/').LastOrDefault()?.Split('?').FirstOrDefault();
                if (string.IsNullOrEmpty(slug)) return DefaultEventTitle;

                // Tách các dấu gạch ngang và viết hoa chữ cái đầu tiên của mỗi từ
                var words = slug.Split('-')
                    .Where(w => !string.IsNullOrEmpty(w))
                    .Select(w => char.ToUpper(w[0]) + w.Substring(1));

                return string.Join(" ", words);
            }
            catch
            {
                return DefaultEventTitle;
            }
        }

        private static bool TryParseAbsolute(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;

            string candidate = url.Trim();
            if (!candidate.Contains("://")) candidate = "https://" + candidate; // người dùng dán "tiki.vn/..." không có scheme

            return Uri.TryCreate(candidate, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
