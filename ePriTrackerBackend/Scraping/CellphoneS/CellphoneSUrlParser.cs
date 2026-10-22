using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// Quy tắc link CellphoneS (khảo sát 20/10/2026):
    /// - Trang sản phẩm có dạng https://cellphones.com.vn/{slug}.html – đúng MỘT đoạn đường dẫn.
    ///   Trang danh mục nhiều đoạn (/laptop/asus.html) bị loại ngay ở đây.
    /// - Mã sản phẩm = slug (trường "sku" trong JSON-LD có khi là mã nội bộ như "VGA.AS.164" nên không dùng).
    /// - Query, "#...", dấu "/" cuối, chữ hoa, http://, www. đều trỏ về cùng trang → chuẩn hoá về một dạng.
    /// </summary>
    public static class CellphoneSUrlParser
    {
        public const string BaseUrl = "https://cellphones.com.vn";

        private static readonly Regex SlugRegex = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

        public static bool IsCellphoneSUrl(string url)
        {
            if (!TryParseAbsolute(url, out var uri)) return false;
            return uri.Host.Equals("cellphones.com.vn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.cellphones.com.vn", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lấy slug (viết thường) từ link dạng /{slug}.html.</summary>
        public static bool TryGetSlug(string url, out string slug)
        {
            slug = null;
            if (!IsCellphoneSUrl(url) || !TryParseAbsolute(url, out var uri)) return false;

            string path = uri.AbsolutePath.Trim('/');
            if (path.Contains('/') || !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return false;

            string candidate = path[..^".html".Length].ToLowerInvariant();
            if (!SlugRegex.IsMatch(candidate)) return false;

            slug = candidate;
            return true;
        }

        /// <summary>Dạng link chuẩn lưu trong DB: https://cellphones.com.vn/{slug}.html</summary>
        public static string NormalizeProductUrl(string slug) => $"{BaseUrl}/{slug}.html";

        private static bool TryParseAbsolute(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;

            string candidate = url.Trim();
            if (!candidate.Contains("://")) candidate = "https://" + candidate; // người dùng dán "cellphones.com.vn/..." không có scheme

            return Uri.TryCreate(candidate, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
