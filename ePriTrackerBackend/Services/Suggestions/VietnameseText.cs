using System.Globalization;
using System.Text;

namespace ePriTrackerBackend.Services.Suggestions
{
    public static class VietnameseText
    {
        /// <summary>
        /// Bỏ dấu tiếng Việt: "Điện thoại" → "Dien thoai". Giữ nguyên hoa/thường. Null/rỗng trả lại nguyên giá trị.
        /// </summary>
        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            foreach (char c in normalizedString)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace('đ', 'd')
                .Replace('Đ', 'D');
        }
    }
}
