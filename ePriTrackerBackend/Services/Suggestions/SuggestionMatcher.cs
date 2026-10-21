using System.Text.RegularExpressions;

namespace ePriTrackerBackend.Services.Suggestions
{
    /// <summary>
    /// Bản thuật toán gợi ý duy nhất (trước đây trùng ở ProductRepository và SuggestionCrawlService,
    /// đã thống nhất theo bản của job Hangfire).
    /// </summary>
    public class SuggestionMatcher : ISuggestionMatcher
    {
        private const decimal MinPriceRatioThreshold = 0.4m; // Ứng viên phải ≥ 40% giá gốc (loại hàng nhái/phụ kiện giá rẻ)
        private const int MaxKeywordWords = 5;
        private const int MinCoreNameLength = 5;

        private static readonly char[] SplitChars = { '-', '|', '(', ')', '[', ']', ',', '/', ':' };

        // Regex compiled tối ưu hiệu năng
        private static readonly Regex SpecialCharRegex = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
        private static readonly Regex WhiteSpaceRegex = new(@"\s+", RegexOptions.Compiled);

        // So sánh trên chuỗi đã bỏ dấu → các từ trong danh sách phải viết KHÔNG dấu
        private static readonly HashSet<string> AccessoriesBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "op lung", "bao da", "kinh cuong luc", "mieng dan", "dan man hinh",
            "cap sac", "cu sac", "tai nghe", "day deo", "vo boc", "balo", "tui chong soc",
            "chandock", "fidget", "moc khoa", "de tan nhiet"
        };

        // Tách từ để so khớp: mọi ký tự không phải chữ/số đều là dấu phân cách ("WH-1000XM4" → "wh", "1000xm4")
        private static readonly Regex TokenSeparatorRegex = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

        private static readonly string[] MarketingWords =
        {
            "chính hãng", "chinh hang", "nhập khẩu", "nhap khau",
            "bản quốc tế", "ban quoc te", "nguyên seal", "nguyen seal",
            "mới 100%", "moi 100%", "freeship", "tặng kèm", "tang kem",
            "vn/a", "ll/a", "fullbox", "giá rẻ", "gia re", "hàng cty"
        };

        // Dựng sẵn regex cho từng từ marketing (cùng pattern với code cũ, chỉ khác là không tạo lại mỗi lần gọi)
        private static readonly Regex[] MarketingWordRegexes = MarketingWords
            .Select(word => new Regex($@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();

        public string ExtractSearchKeyword(string productName)
        {
            if (string.IsNullOrWhiteSpace(productName)) return string.Empty;

            var parts = productName.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries);

            // Lấy phần trước dấu phân cách đầu tiên nếu đủ dài, ngược lại dùng cả tên
            bool usePrefix = parts.Length > 0 && parts[0].Trim().Length >= MinCoreNameLength;
            string keyword = CleanKeyword(usePrefix ? parts[0] : productName);

            // Phần trước dấu phân cách chỉ toàn từ quảng cáo (vd "Freeship - Bàn phím cơ ...") → dùng cả tên
            if (keyword.Length == 0 && usePrefix) keyword = CleanKeyword(productName);

            return keyword;
        }

        private static string CleanKeyword(string text)
        {
            string cleaned = text.ToLowerInvariant();

            foreach (var regex in MarketingWordRegexes)
            {
                cleaned = regex.Replace(cleaned, "");
            }

            cleaned = SpecialCharRegex.Replace(cleaned, " ");
            cleaned = WhiteSpaceRegex.Replace(cleaned, " ").Trim();

            var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(MaxKeywordWords));
        }

        public bool IsSimilarProduct(string originalName, string candidateName)
        {
            if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(candidateName)) return false;

            var lowerOriginalNoMark = VietnameseText.RemoveDiacritics(originalName.ToLowerInvariant());
            var lowerCandidateNoMark = VietnameseText.RemoveDiacritics(candidateName.ToLowerInvariant());

            // Sản phẩm gốc không phải phụ kiện thì loại các ứng viên là phụ kiện
            bool originalIsAccessory = AccessoriesBlacklist.Any(x => lowerOriginalNoMark.Contains(x));
            bool candidateIsAccessory = AccessoriesBlacklist.Any(x => lowerCandidateNoMark.Contains(x));

            if (!originalIsAccessory && candidateIsAccessory) return false;

            // Ít nhất một nửa số từ trong từ khoá của sản phẩm gốc phải xuất hiện trong tên ứng viên.
            // (Trước đây chỉ so từ đầu tiên → bỏ sót "Apple iPhone..." so với "Điện thoại Apple iPhone...",
            //  và nhận nhầm "Nồi cơm điện" là tương tự "Nồi chiên không dầu".)
            var keywordTokens = Tokenize(VietnameseText.RemoveDiacritics(ExtractSearchKeyword(originalName))).Distinct().ToList();
            if (keywordTokens.Count == 0) return true;

            var candidateTokens = Tokenize(lowerCandidateNoMark).ToHashSet();
            int matched = keywordTokens.Count(candidateTokens.Contains);

            return matched * 2 >= keywordTokens.Count;
        }

        private static IEnumerable<string> Tokenize(string text)
            => TokenSeparatorRegex.Split(text.ToLowerInvariant()).Where(t => t.Length > 0);

        public bool IsPriceAcceptable(decimal candidatePrice, decimal referencePrice)
        {
            // Chưa biết giá gốc → không lọc theo giá
            if (referencePrice <= 0) return true;

            return candidatePrice > 0
                && candidatePrice <= referencePrice
                && candidatePrice >= referencePrice * MinPriceRatioThreshold;
        }

        public decimal GetReferencePrice(decimal initialPrice, decimal? latestPrice)
            => (initialPrice > 0 ? initialPrice : latestPrice) ?? 0m;
    }
}
