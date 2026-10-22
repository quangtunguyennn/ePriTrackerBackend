using ePriTrackerBackend.Scraping.CellphoneS;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// HTML mẫu rút gọn từ trang chủ CellphoneS đã render (khảo sát 21/10/2026, bỏ class Tailwind).
    /// </summary>
    public class CellphoneSHomeEventParserTests
    {
        private static readonly TimeSpan Vn = TimeSpan.FromHours(7);
        private static readonly DateTimeOffset During = new(2026, 10, 21, 20, 6, 0, Vn);

        private static string FlashCard(string slug, string name, string price, string original, string sold) =>
            $"<a href=\"/{slug}.html\"><div><span><img alt=\"{name}\" src=\"https://cdn2.cellphones.com.vn/{slug}.png\"></span></div>" +
            $"<h3>{name}</h3><div><div><div>{price}</div><del>{original}</del></div><div><div>{sold}</div><div></div>" +
            "<div><span><img alt=\"Flash Sale Icon\" src=\"https://cdn2.cellphones.com.vn/flash_2.png\"></span></div></div></div></a>";

        private static string DealCard(string slug, string name, string price, string original) =>
            $"<a target=\"_self\" rel=\"nofollow\" href=\"/{slug}.html\"><span><img alt=\"{name}\" src=\"https://cdn2.cellphones.com.vn/{slug}.png\"></span>" +
            $"<p title=\"{name}\">{name}</p><div></div><div><p>{price}</p><p>{original}</p></div>" +
            "<div>Smember giảm đến <span>14.000đ</span></div><div>Giảm <span>22%</span></div></a>";

        private const string Menu =
            "<nav><div><a href=\"/danh-sach-khuyen-mai\"><div><p>Deal sốc<br> đến 50%</p></div></a></div>" +
            "<a href=\"/mobile.html\">Điện thoại</a><a href=\"/laptop.html\">Laptop</a></nav>";

        private static string Flash(params string[] slots) =>
            "<div><div><div><div><div>FLASHSALE TỰU TRƯỜNG</div></div></div>" +
            "<div>" + string.Concat(slots.Select(s => $"<div><div>{s}</div></div>")) + "</div>" +
            "<div>KẾT THÚC SAU</div><div>" +
            FlashCard("laptop-acer-gaming-aspire-7-a715-59g-59rd", "Laptop Acer Gaming Aspire 7 A715-59G-59RD", "24.290.000đ", "27.990.000đ", "Đã bán 1/10 suất") +
            FlashCard("laptop-lenovo-ideapad-slim-3-14iph11-83uq003nvn", "Laptop Lenovo IdeaPad Slim 3", "22.490.000đ", "27.990.000đ", "Đã bán 0/10 suất") +
            FlashCard("laptop-dell-15-dc15250-71084747", "Laptop Dell 15 DC15250", "22.940.000đ", "25.990.000đ", "Đã bán 4/10 suất") +
            FlashCard("laptop-asus-tuf-gaming-a16-fa607nuq-rl007w", "Laptop ASUS TUF Gaming A16 FA607NUQ-RL007W", "32.390.000đ", "44.490.000đ", "Đã bán 2/10 suất") +
            "</div></div>";

        private static readonly string Deal =
            "<div><div><span>DEAL SỐC<br> MỖI NGÀY</span></div><div><button>SẢN PHẨM HOT TREND</button><button>HÀNG MỚI VỀ</button></div><div>" +
            DealCard("ban-phim-co-gaming-e-dra-ek398x-beta-den-xam", "Bàn phím cơ gaming E-Dra EK398X Beta đen xám", "690.000đ", "890.000đ") +
            DealCard("apple-earpods-lightning", "Tai nghe Apple EarPods Lightning", "510.000đ", "790.000đ") +
            DealCard("chuot-choi-game-co-day-logitech-g502-hero", "Chuột Gaming Logitech G502 Hero", "920.000đ", "1.390.000đ") +
            DealCard("combo-camera-hanh-dong-dji-action-4", "DJI Action 4 Combo", "8.150.000đ", "11.690.000đ") +
            DealCard("combo-camera-hanh-dong-dji-action-4", "Trùng link – bỏ", "1.000đ", "2.000đ") +
            "</div></div>";

        private static string Page(string body) => $"<html><body>{Menu}<main>{body}</main></body></html>";

        [Fact]
        public void FlashSale_ActiveSlot_ProductsPricesAndEnd()
        {
            var events = CellphoneSHomeEventParser.Parse(Page(Flash("20-22h 20/10", "20-22h 21/10") + Deal), During);

            var flash = events.Single(e => e.Title.StartsWith("FLASHSALE"));
            Assert.Equal("FLASHSALE TỰU TRƯỜNG (20-22h 21/10)", flash.Title);
            Assert.Equal("https://cellphones.com.vn/#flashsale-tuu-truong-20261021-20", flash.Url);
            Assert.Equal(new DateTimeOffset(2026, 10, 21, 22, 0, 0, Vn), flash.EndsAt);
            Assert.Equal(4, flash.Products.Count);

            var asus = flash.Products.Single(p => p.ExternalId == "laptop-asus-tuf-gaming-a16-fa607nuq-rl007w");
            Assert.Equal("Laptop ASUS TUF Gaming A16 FA607NUQ-RL007W", asus.Name);
            Assert.Equal(32390000m, asus.Price);
            Assert.Equal(44490000m, asus.OriginalPrice);
            Assert.Equal("https://cellphones.com.vn/laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.html", asus.Url);
            Assert.Equal("https://cdn2.cellphones.com.vn/laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.png", asus.ImageUrl);
            Assert.Equal("Đã bán 2/10 suất", asus.Description);
            Assert.Equal(flash.Products[0].ImageUrl, flash.ImageUrl);
        }

        [Theory]
        [InlineData(2026, 10, 21, 19, 59)] // trước giờ mở
        [InlineData(2026, 10, 21, 22, 0)]  // vừa hết khung
        [InlineData(2026, 10, 22, 20, 30)] // ngày khác
        public void FlashSale_NoActiveSlot_NotReturned(int y, int mo, int d, int h, int mi)
        {
            var events = CellphoneSHomeEventParser.Parse(Page(Flash("20-22h 20/10", "20-22h 21/10") + Deal), new DateTimeOffset(y, mo, d, h, mi, 0, Vn));

            Assert.DoesNotContain(events, e => e.Title.StartsWith("FLASHSALE"));
            Assert.Contains(events, e => e.Title == "DEAL SỐC MỖI NGÀY");
        }

        [Fact]
        public void FlashSale_SlotAcrossNewYear_UsesNearestYear()
        {
            var events = CellphoneSHomeEventParser.Parse(Page(Flash("22-24h 31/12")), new DateTimeOffset(2026, 12, 31, 23, 0, 0, Vn));

            Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, Vn), Assert.Single(events).EndsAt);
        }

        [Fact]
        public void DealBlock_PicksRealBlockNotMenu_SkipsMemberDiscount_AndDuplicates()
        {
            var events = CellphoneSHomeEventParser.Parse(Page(Flash("20-22h 21/10") + Deal), During);

            var deal = events.Single(e => e.Title == "DEAL SỐC MỖI NGÀY"); // không phải "Deal sốc đến 50%" ở menu
            Assert.Equal("https://cellphones.com.vn/#deal-soc-moi-ngay", deal.Url);
            Assert.Null(deal.EndsAt);
            Assert.Equal(4, deal.Products.Count);

            var g502 = deal.Products.Single(p => p.ExternalId == "chuot-choi-game-co-day-logitech-g502-hero");
            Assert.Equal("Chuột Gaming Logitech G502 Hero", g502.Name);
            Assert.Equal(920000m, g502.Price);          // không lấy "Smember giảm đến 14.000đ"
            Assert.Equal(1390000m, g502.OriginalPrice);
            Assert.Null(g502.Description);
            Assert.Equal(8150000m, deal.Products.Single(p => p.ExternalId == "combo-camera-hanh-dong-dji-action-4").Price);
        }

        [Fact]
        public void PlaceholderImage_IsTreatedAsMissing()
        {
            // Ảnh tải lỗi → CellphoneS đổi src sang ảnh giữ chỗ (khảo sát 22/10/2026)
            string placeholder = "https://cdn2.cellphones.com.vn/insecure/rs:fill:300:300/q:70/plain/https://cellphones.com.vn/media/wysiwyg/placehoder.png";
            string deal = Deal.Replace("https://cdn2.cellphones.com.vn/apple-earpods-lightning.png", placeholder);

            var events = CellphoneSHomeEventParser.Parse(Page(deal), During);

            var products = Assert.Single(events).Products;
            Assert.Null(products.Single(p => p.ExternalId == "apple-earpods-lightning").ImageUrl);
            Assert.Equal("https://cdn2.cellphones.com.vn/chuot-choi-game-co-day-logitech-g502-hero.png",
                products.Single(p => p.ExternalId == "chuot-choi-game-co-day-logitech-g502-hero").ImageUrl);
        }

        [Fact]
        public void NoKnownBlock_Throws_SoCallerDoesNotDeleteEvents()
        {
            Assert.Throws<InvalidOperationException>(() => CellphoneSHomeEventParser.Parse(Page("<div>Trang tải thiếu</div>"), During));
        }
    }
}
