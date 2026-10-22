using System.Globalization;
using System.Text.RegularExpressions;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Suggestions;
using HtmlAgilityPack;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// Tách sự kiện từ trang chủ CellphoneS ĐÃ RENDER (dữ liệu nạp bằng JavaScript):
    /// - "FLASHSALE ..." – chia khung giờ ("20-22h 21/10"), chỉ lấy khung ĐANG diễn ra, hết khung là hết hạn.
    /// - "DEAL SỐC MỖI NGÀY" – không có giờ kết thúc (nơi gọi tự đặt hạn cuốn chiếu).
    /// Nhận diện khối theo CHỮ tiêu đề, không theo class CSS (class Tailwind đổi liên tục).
    /// </summary>
    public static class CellphoneSHomeEventParser
    {
        public const string HomeUrl = "https://cellphones.com.vn/";
        private const string BaseUrl = "https://cellphones.com.vn";
        private const int MinCardsPerBlock = 4;
        // Khảo sát: danh sách sản phẩm cách tiêu đề 4 (Deal sốc) – 6 (Flash sale) cấp. Leo xa hơn là đã sang vùng bao cả trang.
        private const int MaxTitleDepth = 8;

        private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
        private static readonly Regex FlashSaleTitle = new(@"^FLASH\s*SALE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DealTitle = new(@"^DEAL SỐC\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Price = new(@"^\d{1,3}(?:\.\d{3})+\s*đ$", RegexOptions.Compiled);
        private static readonly Regex Slot = new(@"^(\d{1,2})\s*-\s*(\d{1,2})\s*h\s+(\d{1,2})/(\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SoldQuota = new(@"^Đã bán \d+/\d+ suất$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex NonSlug = new(@"[^a-z0-9]+", RegexOptions.Compiled);

        /// <param name="now">Giờ hiện tại – để chọn khung flash sale đang chạy.</param>
        /// <exception cref="InvalidOperationException">Không thấy khối sự kiện nào → trang tải thiếu hoặc CellphoneS đổi giao diện.</exception>
        public static IReadOnlyList<ScrapedEvent> Parse(string html, DateTimeOffset now)
        {
            var document = new HtmlDocument();
            document.LoadHtml(html ?? "");

            var flashBlock = FindBlock(document, FlashSaleTitle);
            var dealBlock = FindBlock(document, DealTitle);
            if (flashBlock == null && dealBlock == null)
                throw new InvalidOperationException("Không nhận diện được khối sự kiện nào trên trang chủ CellphoneS.");

            var events = new List<ScrapedEvent>();

            if (flashBlock is { } flash && FindActiveSlot(flash.Box, now) is { } slot)
            {
                var products = ParseCards(flash.Box);
                events.Add(new ScrapedEvent
                {
                    Title = $"{flash.Title} ({slot.Label})",
                    Url = $"{HomeUrl}#{Slugify(flash.Title)}-{slot.Start:yyyyMMdd-HH}",
                    ImageUrl = products.FirstOrDefault()?.ImageUrl,
                    EndsAt = slot.End,
                    Products = products
                });
            }

            if (dealBlock is { } deal)
            {
                var products = ParseCards(deal.Box);
                events.Add(new ScrapedEvent
                {
                    Title = deal.Title,
                    Url = $"{HomeUrl}#{Slugify(deal.Title)}",
                    ImageUrl = products.FirstOrDefault()?.ImageUrl,
                    Products = products
                });
            }

            return events;
        }

        /// <summary>
        /// Phần tử lá có chữ khớp tiêu đề → leo lên tổ tiên GẦN NHẤT chứa đủ thẻ sản phẩm.
        /// Nhiều chỗ khớp (vd menu "Deal sốc đến 50%" cũng khớp) → chọn khối ÍT thẻ nhất: chữ ở menu phải leo tới tận
        /// vùng bao cả trang mới gặp sản phẩm, còn tiêu đề thật nằm sát danh sách của nó.
        /// </summary>
        private static (string Title, HtmlNode Box)? FindBlock(HtmlDocument document, Regex title)
        {
            (string Title, HtmlNode Box, int Cards)? best = null;

            foreach (var leaf in Leaves(document.DocumentNode))
            {
                string text = Text(leaf);
                // Chữ nằm trong link là mục menu ("Deal sốc đến 50%"), không phải tiêu đề khối
                if (text == null || !title.IsMatch(text) || leaf.Ancestors("a").Any()) continue;

                int depth = 0;
                for (var box = leaf.ParentNode; box != null && depth < MaxTitleDepth; box = box.ParentNode, depth++)
                {
                    int cards = ProductCards(box).Count();
                    if (cards < MinCardsPerBlock) continue;

                    if (best == null || cards < best.Value.Cards) best = (text, box, cards);
                    break;
                }
            }
            return best is { } b ? (b.Title, b.Box) : null;
        }

        private static (string Label, DateTimeOffset Start, DateTimeOffset End)? FindActiveSlot(HtmlNode box, DateTimeOffset now)
        {
            var nowVn = now.ToOffset(VietnamOffset);
            foreach (var leaf in Leaves(box))
            {
                string text = Text(leaf);
                if (text == null) continue;

                var m = Slot.Match(text);
                if (!m.Success) continue;

                int fromHour = int.Parse(m.Groups[1].Value), toHour = int.Parse(m.Groups[2].Value);
                int day = int.Parse(m.Groups[3].Value), month = int.Parse(m.Groups[4].Value);
                if (month is < 1 or > 12 || day < 1 || day > 31 || fromHour > 24 || toHour > 24 || toHour <= fromHour) continue;

                // Nhãn không có năm → chọn năm gần "bây giờ" nhất (khung 31/12 xem lúc 01/01 vẫn đúng)
                var date = SafeDate(nowVn.Year, month, day);
                if (date == null) continue;
                if ((date.Value - nowVn.Date).TotalDays > 180) date = SafeDate(nowVn.Year - 1, month, day);
                else if ((nowVn.Date - date.Value).TotalDays > 180) date = SafeDate(nowVn.Year + 1, month, day);
                if (date == null) continue;

                var start = new DateTimeOffset(date.Value, VietnamOffset).AddHours(fromHour);
                var end = new DateTimeOffset(date.Value, VietnamOffset).AddHours(toHour);
                if (start <= now && now < end) return (text, start, end);
            }
            return null;
        }

        private static DateTime? SafeDate(int year, int month, int day)
            => day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day) : null;

        private static List<ScrapedProduct> ParseCards(HtmlNode box)
        {
            var products = new List<ScrapedProduct>();
            var seen = new HashSet<string>();

            foreach (var card in ProductCards(box))
            {
                string href = HtmlEntity.DeEntitize(card.GetAttributeValue("href", "")).Trim();
                string absolute = href.StartsWith('/') ? BaseUrl + href : href;
                if (!CellphoneSUrlParser.TryGetSlug(absolute, out var slug) || !seen.Add(slug)) continue;

                var prices = PriceLeaves(card).ToList();
                if (prices.Count == 0) continue;

                // Giá bán = giá đầu tiên; giá gốc = thẻ <del> (flash sale) hoặc giá còn lại cao hơn (deal)
                decimal price = prices[0].Value;
                decimal? original = prices.Where(p => p.Struck).Select(p => (decimal?)p.Value).FirstOrDefault()
                    ?? prices.Skip(1).Select(p => (decimal?)p.Value).FirstOrDefault(v => v > price);

                var image = card.Descendants("img").FirstOrDefault();
                string name = Text(card.Descendants("h3").FirstOrDefault())
                    ?? Attr(card.Descendants("p").FirstOrDefault(p => p.Attributes.Contains("title")), "title")
                    ?? Attr(image, "alt");

                products.Add(new ScrapedProduct
                {
                    ExternalId = slug,
                    Name = name,
                    Price = price,
                    OriginalPrice = original,
                    ImageUrl = ProductImage(image),
                    Url = CellphoneSUrlParser.NormalizeProductUrl(slug),
                    // "Đã bán 1/10 suất" của flash sale → hiện kèm sản phẩm
                    Description = Leaves(card).Select(Text).FirstOrDefault(t => t != null && SoldQuota.IsMatch(t))
                });
            }

            return products;
        }

        /// <summary>
        /// Ảnh giữ chỗ (ảnh tải lỗi → trang đổi sang "placehoder.png") hoặc ảnh nhúng data: → coi như không có ảnh,
        /// để không hiện ảnh trống cho người dùng.
        /// </summary>
        private static string ProductImage(HtmlNode image)
        {
            string src = Attr(image, "src");
            if (src == null || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            return src.Contains("placehoder", StringComparison.OrdinalIgnoreCase) || src.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
                ? null
                : src;
        }

        /// <summary>Thẻ sản phẩm = link tới trang .html có hiện giá.</summary>
        private static IEnumerable<HtmlNode> ProductCards(HtmlNode box)
            => box.Descendants("a").Where(a =>
                a.GetAttributeValue("href", "").Split('?', '#')[0].EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                && PriceLeaves(a).Any());

        /// <summary>Các con số giá trong thẻ, bỏ "Smember giảm đến 14.000đ" (giá nằm cạnh chữ "giảm").</summary>
        private static IEnumerable<(decimal Value, bool Struck)> PriceLeaves(HtmlNode card)
        {
            foreach (var leaf in Leaves(card))
            {
                string text = Text(leaf);
                if (text == null || !Price.IsMatch(text)) continue;
                if (Text(leaf.ParentNode)?.Contains("giảm", StringComparison.OrdinalIgnoreCase) == true) continue;

                string digits = new string(text.Where(char.IsDigit).ToArray());
                if (!decimal.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0) continue;

                bool struck = leaf.Name is "del" or "s" || leaf.Ancestors().TakeWhile(a => a != card).Any(a => a.Name is "del" or "s");
                yield return (value, struck);
            }
        }

        private static IEnumerable<HtmlNode> Leaves(HtmlNode root)
            => root.Descendants().Where(n => n.NodeType == HtmlNodeType.Element
                && n.Name is not ("script" or "style")
                && !n.ChildNodes.Any(c => c.NodeType == HtmlNodeType.Element && c.Name != "br"));

        private static string Text(HtmlNode node)
        {
            if (node == null) return null;
            string text = Whitespace.Replace(HtmlEntity.DeEntitize(node.InnerText), " ").Trim();
            return text.Length == 0 ? null : text;
        }

        private static string Attr(HtmlNode node, string name)
        {
            string value = node?.GetAttributeValue(name, null);
            return string.IsNullOrWhiteSpace(value) ? null : HtmlEntity.DeEntitize(value).Trim();
        }

        private static string Slugify(string text)
            => NonSlug.Replace(VietnameseText.RemoveDiacritics(text).ToLowerInvariant(), "-").Trim('-');
    }
}
