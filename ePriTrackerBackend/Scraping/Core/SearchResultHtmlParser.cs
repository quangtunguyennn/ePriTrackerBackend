using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ePriTrackerBackend.Scraping.Abstractions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>
    /// Đọc danh sách sản phẩm từ HTML trang tìm kiếm của các sàn không có API JSON (CellphoneS, HACOM).
    /// Không phụ thuộc class CSS của từng sàn. Đi từ GIÁ: tìm các phần tử chứa giá tiền, với mỗi phần tử leo lên
    /// thẻ cha nhỏ nhất có chứa link trang sản phẩm (do scraper của sàn nhận diện) = "thẻ sản phẩm",
    /// rồi lấy tên / giá / ảnh trong thẻ đó. Thẻ thiếu tên hoặc giá bị bỏ.
    /// (Bản trước đi từ link; dễ hỏng khi thẻ có thêm link danh mục/thương hiệu – HACOM dùng chung dạng slug cho cả
    /// sản phẩm và danh mục nên link danh mục cũng bị coi là link sản phẩm.)
    /// </summary>
    public static class SearchResultHtmlParser
    {
        private const int MaxCardDepth = 8;
        private const int MaxLinksPerCard = 6;   // thẻ có nhiều hơn → đã leo quá cao tới cả lưới sản phẩm
        private const int MinNameLength = 5;
        private const decimal MinPlausiblePrice = 1000m;

        // "26.990.000đ", "26,990,000 ₫", "40.890.000 VND". Số đi sau "giảm / tiết kiệm / trả góp..." là số tiền giảm, không phải giá bán.
        private static readonly Regex PriceRegex = new(
            @"(?<prefix>giảm|tiết kiệm|trả góp|trả trước)?\s*(?<num>\d{1,3}(?:[.,]\d{3})+)\s*(?:đ|₫|vnđ|vnd)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex WhiteSpaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex ClassTokenSplitRegex = new(@"[\s_\-]+", RegexOptions.Compiled);

        // Token trong class CSS cho biết đây KHÔNG phải giá bán hiện tại (giá cũ, giá trả góp...)
        private static readonly HashSet<string> NotCurrentPriceTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "old", "del", "regular", "origin", "original", "through", "strike", "install", "installment", "tragop", "compare"
        };

        private static readonly string[] ImageAttributes = { "data-src", "data-lazy-src", "data-original", "src" };
        private static readonly string[] ImagePlaceholderHints = { "placeholder", "placehoder", "loading", "blank" };

        /// <summary>Ghép từ khoá (đã mã hoá URL) vào mẫu URL có "{0}".</summary>
        public static string BuildSearchUrl(string template, string keyword)
            => string.Format(template, Uri.EscapeDataString(keyword.Trim()));

        /// <summary>
        /// Không đọc được sản phẩm nào → lưu HTML vào thư mục tạm để xem trang thật trông thế nào
        /// (sai URL tìm kiếm? bị chặn? trang nạp bằng JavaScript? hay chỉ là không có kết quả?).
        /// </summary>
        public static void DumpForDebug(ILogger logger, string platform, string searchUrl, string html)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), $"scrape-debug-{platform}.html");
                File.WriteAllText(path, html ?? "");
                logger.LogWarning("[{Platform}] Không đọc được sản phẩm nào từ {Url} (HTML {Length} ký tự). Đã lưu HTML để kiểm tra: {Path}",
                    platform, searchUrl, html?.Length ?? 0, path);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Không lưu được HTML debug.");
            }
        }

        /// <param name="html">HTML trang tìm kiếm.</param>
        /// <param name="pageUrl">URL trang tìm kiếm – để đổi link tương đối thành tuyệt đối.</param>
        /// <param name="resolveProduct">
        /// Nhận URL tuyệt đối; trả (URL chuẩn hoá, mã ngoài) nếu là trang sản phẩm của sàn, ngược lại null.
        /// </param>
        /// <param name="limit">Số sản phẩm tối đa.</param>
        public static IReadOnlyList<ScrapedProduct> Parse(
            string html,
            string pageUrl,
            Func<string, (string Url, string ExternalId)?> resolveProduct,
            int limit)
        {
            var results = new List<ScrapedProduct>();
            if (string.IsNullOrWhiteSpace(html) || limit <= 0) return results;
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)) return results;

            var document = new HtmlDocument();
            document.LoadHtml(html);

            // 1) Mọi thẻ <a> trỏ tới trang sản phẩm (giải mã link đúng một lần)
            var anchors = document.DocumentNode.SelectNodes("//a[@href]");
            if (anchors == null) return results;

            var productByAnchor = new Dictionary<HtmlNode, (string Url, string ExternalId)>();
            foreach (var anchor in anchors)
            {
                if (!TryResolveUrl(baseUri, anchor.GetAttributeValue("href", ""), out var absolute)) continue;

                var resolved = resolveProduct(absolute);
                if (resolved != null) productByAnchor[anchor] = resolved.Value;
            }
            if (productByAnchor.Count == 0) return results;

            // 2) Các phần tử chứa giá bán (theo thứ tự xuất hiện trong trang)
            var priceNodes = new List<HtmlNode>();
            CollectPriceNodes(document.DocumentNode, priceNodes);

            // 3) Mỗi giá → thẻ sản phẩm gần nhất
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var priceNode in priceNodes)
            {
                if (!TryFindCard(priceNode, productByAnchor, out var card, out var product)) continue;
                if (seen.Contains(product.Url)) continue;

                var item = BuildProduct(card, product.Url, product.ExternalId, productByAnchor, baseUri);
                if (item == null) continue;

                seen.Add(product.Url);
                results.Add(item);
                if (results.Count >= limit) break;
            }

            return results;
        }

        // Leo từ phần tử giá lên cha nhỏ nhất có chứa link sản phẩm. Trong thẻ có nhiều link (sản phẩm + danh mục/thương hiệu)
        // thì chọn link có nhiều thẻ <a> trỏ tới nhất (ảnh + tiêu đề), có ảnh/tiêu đề, chữ dài nhất.
        private static bool TryFindCard(
            HtmlNode priceNode,
            Dictionary<HtmlNode, (string Url, string ExternalId)> productByAnchor,
            out HtmlNode card,
            out (string Url, string ExternalId) product)
        {
            card = null;
            product = default;

            var node = priceNode;
            for (int depth = 0; depth <= MaxCardDepth && node != null; depth++, node = node.ParentNode)
            {
                if (node.NodeType == HtmlNodeType.Document || node.Name is "body" or "html") break;

                var links = node.DescendantsAndSelf("a").Where(productByAnchor.ContainsKey).ToList();
                if (links.Count == 0) continue;

                var groups = links.GroupBy(a => productByAnchor[a].Url, StringComparer.OrdinalIgnoreCase).ToList();
                if (groups.Count > MaxLinksPerCard) break;

                var best = groups.OrderByDescending(Score).First();
                card = node;
                product = productByAnchor[best.First()];
                return true;
            }

            return false;
        }

        private static int Score(IGrouping<string, HtmlNode> group)
        {
            int score = group.Count() * 100;

            if (group.Any(a => a.GetAttributeValue("title", "").Length > 0
                               || a.Descendants("img").Any()
                               || a.Descendants().Any(n => n.Name is "h1" or "h2" or "h3" or "h4")))
                score += 50;

            score += Math.Min(group.Max(a => Clean(a.InnerText).Length), 49);
            return score;
        }

        private static ScrapedProduct BuildProduct(
            HtmlNode card,
            string url,
            string externalId,
            Dictionary<HtmlNode, (string Url, string ExternalId)> productByAnchor,
            Uri baseUri)
        {
            var productAnchors = card.DescendantsAndSelf()
                .Where(n => n.Name == "a" && productByAnchor.TryGetValue(n, out var p) && string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string name = FindName(card, productAnchors);
            decimal? price = FindPrice(card);
            if (string.IsNullOrEmpty(name) || price == null) return null;

            return new ScrapedProduct
            {
                ExternalId = externalId,
                Name = name,
                Price = price,
                ImageUrl = FindImage(card, productAnchors, baseUri),
                Url = url
            };
        }

        // ---------- Phần tử giá ----------

        // Duyệt cây một lần: thêm vào danh sách các phần tử "nhỏ nhất" có chữ chứa giá bán.
        // Trả (chữ hiển thị, có giá ở phía dưới hay không).
        private static (string Text, bool Matched) CollectPriceNodes(HtmlNode node, List<HtmlNode> found)
        {
            if (node.Name is "script" or "style" or "noscript" or "head" or "del" or "s" or "strike")
                return ("", false);

            if (node.NodeType == HtmlNodeType.Text)
                return (WebUtility.HtmlDecode(node.InnerText) + " ", false);

            if (node.NodeType != HtmlNodeType.Element && node.NodeType != HtmlNodeType.Document)
                return ("", false);

            var sb = new StringBuilder();
            bool childMatched = false;
            foreach (var child in node.ChildNodes)
            {
                var (text, matched) = CollectPriceNodes(child, found);
                sb.Append(text);
                childMatched |= matched;
            }

            string all = sb.ToString();
            if (childMatched) return (all, true);

            if (node.NodeType == HtmlNodeType.Element && ParsePrice(all) != null)
            {
                found.Add(node);
                return (all, true);
            }

            return (all, false);
        }

        // ---------- Tên ----------

        private static string FindName(HtmlNode card, List<HtmlNode> productAnchors)
        {
            // 1) thuộc tính title của link sản phẩm
            foreach (var a in productAnchors)
            {
                var title = Clean(a.GetAttributeValue("title", ""));
                if (title.Length >= MinNameLength) return title;
            }

            // 2) thẻ tiêu đề trong card
            var heading = card.DescendantsAndSelf().FirstOrDefault(n => n.Name is "h1" or "h2" or "h3" or "h4");
            if (heading != null)
            {
                var text = Clean(heading.InnerText);
                if (text.Length >= MinNameLength) return text;
            }

            // 3) chữ trong link (bỏ phần giá nếu link bọc cả card)
            var anchorText = productAnchors
                .Select(a => Clean(PriceRegex.Replace(a.InnerText ?? "", " ")))
                .OrderByDescending(t => t.Length)
                .FirstOrDefault();
            if (anchorText != null && anchorText.Length >= MinNameLength) return anchorText;

            // 4) alt của ảnh
            return card.DescendantsAndSelf("img")
                .Select(i => Clean(i.GetAttributeValue("alt", "")))
                .FirstOrDefault(t => t.Length >= MinNameLength) ?? "";
        }

        // ---------- Giá ----------

        private static decimal? FindPrice(HtmlNode card)
        {
            // 1) Phần tử "lá" có class chứa "price" (bỏ giá cũ / trả góp; bỏ phần tử bao ngoài có chứa phần tử price khác)
            foreach (var node in card.DescendantsAndSelf())
            {
                if (!HasPriceClass(node) || IsNotCurrentPrice(node)) continue;
                if (node.Descendants().Any(HasPriceClass)) continue;

                var price = ParsePrice(VisibleText(node));
                if (price != null) return price;
            }

            // 2) Giá đầu tiên trong chữ của card (đã bỏ <del>/<s>/<strike>)
            return ParsePrice(VisibleText(card));
        }

        private static bool HasPriceClass(HtmlNode node)
            => node.NodeType == HtmlNodeType.Element
               && node.GetAttributeValue("class", "").Contains("price", StringComparison.OrdinalIgnoreCase);

        private static bool IsNotCurrentPrice(HtmlNode node)
        {
            var tokens = ClassTokenSplitRegex.Split(node.GetAttributeValue("class", ""));
            if (tokens.Any(NotCurrentPriceTokens.Contains)) return true;

            return node.Ancestors().Any(a => a.Name is "del" or "s" or "strike");
        }

        private static decimal? ParsePrice(string text)
        {
            foreach (Match m in PriceRegex.Matches(text))
            {
                if (m.Groups["prefix"].Success) continue;

                string digits = m.Groups["num"].Value.Replace(".", "").Replace(",", "");
                if (decimal.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= MinPlausiblePrice)
                    return value;
            }
            return null;
        }

        // Chữ hiển thị của node, bỏ script/style và giá gạch ngang
        private static string VisibleText(HtmlNode node)
        {
            var sb = new StringBuilder();
            AppendText(node, sb);
            return sb.ToString();
        }

        private static void AppendText(HtmlNode node, StringBuilder sb)
        {
            if (node.Name is "script" or "style" or "del" or "s" or "strike") return;

            if (node.NodeType == HtmlNodeType.Text)
            {
                sb.Append(WebUtility.HtmlDecode(node.InnerText)).Append(' ');
                return;
            }

            foreach (var child in node.ChildNodes)
                AppendText(child, sb);
        }

        // ---------- Ảnh ----------

        private static string FindImage(HtmlNode card, List<HtmlNode> productAnchors, Uri baseUri)
        {
            // Ưu tiên ảnh nằm trong link sản phẩm (tránh huy hiệu/icon khuyến mãi ở ngoài link)
            var images = productAnchors.SelectMany(a => a.DescendantsAndSelf("img"))
                .Concat(card.DescendantsAndSelf("img"));

            foreach (var img in images)
            {
                foreach (var attribute in ImageAttributes)
                {
                    if (TryGetImageUrl(baseUri, img.GetAttributeValue(attribute, ""), out var url)) return url;
                }

                foreach (var attribute in new[] { "data-srcset", "srcset" })
                {
                    string first = img.GetAttributeValue(attribute, "").Split(',')[0].Trim().Split(' ')[0];
                    if (TryGetImageUrl(baseUri, first, out var url)) return url;
                }
            }

            return null;
        }

        private static bool TryGetImageUrl(Uri baseUri, string value, out string url)
        {
            url = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            if (ImagePlaceholderHints.Any(h => value.Contains(h, StringComparison.OrdinalIgnoreCase))) return false;

            return TryResolveUrl(baseUri, value, out url);
        }

        // ---------- Tiện ích ----------

        private static bool TryResolveUrl(Uri baseUri, string href, out string absolute)
        {
            absolute = null;
            href = href?.Trim();
            if (string.IsNullOrEmpty(href) || href.StartsWith('#')
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href), out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            absolute = uri.AbsoluteUri;
            return true;
        }

        private static string Clean(string text)
            => WebUtility.HtmlDecode(WhiteSpaceRegex.Replace(text ?? "", " ")).Trim();
    }
}