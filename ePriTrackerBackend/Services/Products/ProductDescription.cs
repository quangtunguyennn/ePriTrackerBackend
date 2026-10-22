using Ganss.Xss;

namespace ePriTrackerBackend.Services.Products
{
    /// <summary>
    /// Mô tả lấy từ sàn là HTML và FE hiển thị bằng dangerouslySetInnerHTML → phải lọc trước khi lưu DB
    /// (bỏ script, on*=, javascript:, form...). Bỏ luôn style/class/id để mô tả mọi sàn hiển thị đồng nhất theo CSS của FE.
    /// </summary>
    public static class ProductDescription
    {
        // HtmlSanitizer dùng chung an toàn giữa các luồng khi không đổi cấu hình sau khi tạo
        private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

        public static string Sanitize(string html)
            => string.IsNullOrWhiteSpace(html) ? "" : Sanitizer.Sanitize(html).Trim();

        private static HtmlSanitizer CreateSanitizer()
        {
            var sanitizer = new HtmlSanitizer(); // mặc định href/src chỉ nhận http/https
            foreach (var attribute in new[] { "style", "class", "id" }) sanitizer.AllowedAttributes.Remove(attribute);
            foreach (var tag in new[] { "form", "input", "button", "select", "option", "textarea", "label" }) sanitizer.AllowedTags.Remove(tag);
            return sanitizer;
        }
    }
}
