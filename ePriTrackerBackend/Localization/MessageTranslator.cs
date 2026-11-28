using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;

namespace ePriTrackerBackend.Localization
{
    /// <summary>
    /// Dịch câu "message" trả cho FE theo ngôn ngữ của request (Accept-Language), khoá là câu tiếng Anh.
    /// Hỗ trợ câu ghép (vd "Event published successfully! (5 products)"): dịch từng câu, phần có số dùng mẫu "({0} products)".
    /// Câu không có bản dịch (vd lỗi kèm dữ liệu thô từ sàn) giữ nguyên tiếng Anh.
    /// </summary>
    public static class MessageTranslator
    {
        // Tách sau dấu kết câu (. ! ?) + khoảng trắng, hoặc trước "(" của phần ghi chú cuối câu
        private static readonly Regex SentenceBreak = new(@"(?<=[.!?])\s+(?=\S)|\s+(?=\()", RegexOptions.Compiled);
        private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled);

        public static string Translate(IStringLocalizer localizer, string message)
        {
            if (localizer == null || string.IsNullOrWhiteSpace(message)) return message;

            if (TryTranslate(localizer, message, out var whole)) return whole;

            var parts = SentenceBreak.Split(message.Trim());
            if (parts.Length < 2) return message;

            bool any = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (TryTranslate(localizer, parts[i], out var translated))
                {
                    parts[i] = translated;
                    any = true;
                }
            }
            return any ? string.Join(" ", parts) : message;
        }

        private static bool TryTranslate(IStringLocalizer localizer, string text, out string translated)
        {
            var exact = localizer[text];
            if (!exact.ResourceNotFound)
            {
                translated = exact.Value;
                return true;
            }

            // Mẫu có số: "(5 products)" → khoá "({0} products)"
            var numbers = Number.Matches(text).Select(m => m.Value).ToArray();
            if (numbers.Length > 0)
            {
                int index = 0;
                string template = Number.Replace(text, _ => "{" + index++ + "}");
                var formatted = localizer[template, numbers];
                if (!formatted.ResourceNotFound)
                {
                    translated = formatted.Value;
                    return true;
                }
            }

            translated = text;
            return false;
        }
    }
}
