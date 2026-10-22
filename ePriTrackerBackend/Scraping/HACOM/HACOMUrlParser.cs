using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Scraping.Hacom
{
    /// <summary>
    /// Quy tắc link HACOM:
    /// - Trang sản phẩm có dạng https://hacom.vn/{slug} – đúng MỘT đoạn đường dẫn (không có .html).
    ///   Trang danh mục nhiều đoạn bị loại ngay ở đây.
    /// - Mã sản phẩm = slug.
    /// - Query, "#...", dấu "/" cuối, chữ hoa, http://, www. đều trỏ về cùng trang → chuẩn hoá về một dạng.
    /// </summary>
    public static class HacomUrlParser
    {
        public const string BaseUrl = "https://hacom.vn";

        private static readonly Regex SlugRegex = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

        public static bool IsHacomUrl(string url)
        {
            if (!TryParseAbsolute(url, out var uri)) return false;
            return uri.Host.Equals("hacom.vn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.hacom.vn", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lấy slug (viết thường) từ link dạng /{slug}.</summary>
        public static bool TryGetSlug(string url, out string slug)
        {
            slug = null;
            if (!IsHacomUrl(url) || !TryParseAbsolute(url, out var uri)) return false;

            string path = uri.AbsolutePath.Trim('/');
            if (path.Contains('/') || string.IsNullOrWhiteSpace(path)) return false;

            // Cắt bỏ dấu phẩy, dấu chấm hoặc các ký tự phân tách dư thừa ở cuối URL
            string candidate = path.TrimEnd(',', '.', ';').ToLowerInvariant();
            if (!SlugRegex.IsMatch(candidate)) return false;

            slug = candidate;
            return true;
        }

        /// <summary>Dạng link chuẩn lưu trong DB: https://hacom.vn/{slug}</summary>
        public static string NormalizeProductUrl(string slug) => $"{BaseUrl}/{slug}";

        private static bool TryParseAbsolute(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Tách lấy phần link đầu tiên, bỏ qua tiêu đề hoặc các đoạn text thừa phía sau
            string candidate = url.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)[0];

            if (!candidate.Contains("://")) candidate = "https://" + candidate; // người dùng dán "hacom.vn/..." không có scheme

            return Uri.TryCreate(candidate, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}