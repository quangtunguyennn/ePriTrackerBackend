using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>Thứ tự lấy tên trong thẻ sản phẩm (bước "class name/title" thêm cho GearVN không được đổi kết quả của HACOM).</summary>
    public class SearchResultHtmlParserTests
    {
        private static (string Url, string ExternalId)? Resolve(string url)
            => url.Contains("/p/") ? (url, url.Split('/').Last()) : null;

        private static string Parse(string card)
            => SearchResultHtmlParser.Parse($"<html><body>{card}</body></html>", "https://shop.vn/search", Resolve, 5).Single().Name;

        [Fact]
        public void Heading_StillWinsOverNameClass()
        {
            Assert.Equal("Tên trong tiêu đề", Parse(
                "<div><a href='/p/a'><h3>Tên trong tiêu đề</h3></a><span class='item-name'>Tên phụ khác</span><b class='price'>1.000.000đ</b></div>"));
        }

        [Fact]
        public void NameClass_UsedWhenNoTitleOrHeading_InsteadOfWholeLinkText()
        {
            Assert.Equal("Chuột không dây ABC", Parse(
                "<a href='/p/a'><span>Giảm 10%</span><p class='product-card-name'>Chuột không dây ABC</p><span>Đen</span><b class='price'>500.000đ</b></a>"));
        }

        // Thẻ sản phẩm rút gọn từ trang tìm kiếm HACOM đã render (31/10/2026): mọi link có title "Xem chi tiết", thẻ tiêu đề đầu tiên là mã hàng,
        // tên thật ở <h3 title>, giá gốc gạch ngang bằng class Tailwind "line-through" (không phải <del>), giá bán tách "2.099.000" + <sub>đ</sub>.
        private const string HacomCard = """
            <div class="group/card">
              <a title="Xem chi tiết" href="/p/mx3s"><img alt="Chuột Logitech MX Master 3S Graphite" src="https://cdn.hacom.vn/mx3s.webp"></a>
              <a title="Xem chi tiết" href="/p/mx3s"><section class="rate-tt"></section></a>
              <button><h4 class="ml-auto rounded-md">Mã: MELO0271</h4></button>
              <a class="block" title="Xem chi tiết" href="/p/mx3s"><h3 class="line-clamp-2" title="Chuột Logitech MX Master 3S Graphite (910-007502)">Chuột Logitech MX Master 3S Graphite (910-007502)</h3></a>
              <a title="Xem chi tiết" href="/p/mx3s">
                <section class="tiet-kiem"><span class="text-[11px] text-gray-500 line-through">2.499.000₫ </span><span>(Tiết kiệm 16%)</span></section>
                <div class="flex"><span class="font-bold !text-[#ce0707]">2.099.000</span> <sub class="text-[10px]">đ</sub></div>
              </a>
              <p class="inline-block">Giảm 400.000₫</p>
            </div>
            """;

        [Fact]
        public void HacomCard_RealNameFromInnerTitle_SalePriceNotStrikeThrough()
        {
            var item = SearchResultHtmlParser.Parse($"<html><body>{HacomCard}</body></html>", "https://shop.vn/search", Resolve, 5).Single();

            Assert.Equal("Chuột Logitech MX Master 3S Graphite (910-007502)", item.Name); // không phải "Xem chi tiết" / "Mã: MELO0271"
            Assert.Equal(2099000m, item.Price);                                           // không phải giá gạch ngang 2.499.000
        }

        [Fact]
        public void CodeLabelHeading_Skipped_WhenNoOtherTitle()
        {
            Assert.Equal("Bàn phím cơ XYZ", Parse(
                "<div><h4>Mã: KB0001</h4><a href='/p/a'><h3>Bàn phím cơ XYZ</h3></a><b class='price'>1.000.000đ</b></div>"));
        }

        [Fact]
        public void TitleAttribute_StillFirst()
        {
            Assert.Equal("Tên trong title", Parse(
                "<a href='/p/a' title='Tên trong title'><p class='name'>Tên khác</p><b class='price'>500.000đ</b></a>"));
        }
    }
}
