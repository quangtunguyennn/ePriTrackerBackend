using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Scraping.Gearvn
{
    /// <summary>
    /// Quy tắc link GearVN (web Next.js, dữ liệu Haravan):
    /// - Trang sản phẩm: https://gearvn.com/products/{handle}; kiểu Haravan /collections/{danh-muc}/products/{handle} cũng trỏ về
    ///   cùng sản phẩm. Trang khác (danh mục /collections/..., /pages/..., /blogs/...) bị loại ngay ở đây.
    /// - Mỗi phiên bản (màu, cấu hình) là một sản phẩm riêng có handle riêng → bỏ query (kể cả ?variant=).
    /// - Mã sản phẩm = handle (viết thường).
    /// </summary>
    public static class GearvnUrlParser
    {
        public const string BaseUrl = "https://gearvn.com";

        private static readonly Regex HandleRegex = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

        public static bool IsGearvnUrl(string url)
        {
            if (!TryParseAbsolute(url, out var uri)) return false;
            return uri.Host.Equals("gearvn.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.gearvn.com", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lấy handle (viết thường) từ link /products/{handle} hoặc /collections/{x}/products/{handle}.</summary>
        public static bool TryGetHandle(string url, out string handle)
        {
            handle = null;
            if (!IsGearvnUrl(url) || !TryParseAbsolute(url, out var uri)) return false;

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool isProductPath =
                (segments.Length == 2 && segments[0].Equals("products", StringComparison.OrdinalIgnoreCase))
                || (segments.Length == 4 && segments[0].Equals("collections", StringComparison.OrdinalIgnoreCase)
                                         && segments[2].Equals("products", StringComparison.OrdinalIgnoreCase));
            if (!isProductPath) return false;

            string candidate = segments[^1].ToLowerInvariant();
            if (!HandleRegex.IsMatch(candidate)) return false;

            handle = candidate;
            return true;
        }

        /// <summary>Dạng link chuẩn lưu trong DB: https://gearvn.com/products/{handle}</summary>
        public static string NormalizeProductUrl(string handle) => $"{BaseUrl}/products/{handle}";

        private static bool TryParseAbsolute(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Người dùng hay dán kèm tiêu đề phía sau link → lấy phần link đầu tiên
            string candidate = url.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!candidate.Contains("://")) candidate = "https://" + candidate; // dán "gearvn.com/..." không có scheme

            return Uri.TryCreate(candidate, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
