using ePriTrackerBackend.Scraping.Tiki;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// Các case TryGetProductId / ToAbsoluteUrl / FormatEventTitle GIỮ NGUYÊN expected từ characterization test
    /// giai đoạn 0 (ProductRepository.ProductIdRegex, SuggestionCrawlService.BuildFullTikiUrl, EventRepository.FormatEventTitleFromUrl).
    /// </summary>
    public class TikiUrlParserTests
    {
        [Theory]
        [InlineData("https://tiki.vn/dien-thoai-iphone-15-p184036446.html", "184036446")]
        [InlineData("https://tiki.vn/abc-p123.html?spid=456", "123")]
        [InlineData("https://tiki.vn/product-p999.HTML", "999")]          // không phân biệt hoa thường
        [InlineData("https://tiki.vn/dien-thoai-p12a-p34.html", "34")]
        public void TryGetProductId_ExtractsId(string link, string expectedId)
        {
            Assert.True(TikiUrlParser.TryGetProductId(link, out var id));
            Assert.Equal(expectedId, id);
        }

        [Theory]
        [InlineData("https://tiki.vn/abc-p.html")]
        [InlineData("https://shopee.vn/iphone-i.123.456")]
        [InlineData("")]
        [InlineData(null)]
        public void TryGetProductId_RejectsInvalidLink(string? link)
        {
            Assert.False(TikiUrlParser.TryGetProductId(link!, out var id));
            Assert.Null(id);
        }

        [Theory]
        [InlineData("https://tiki.vn/abc-p123.html", true)]
        [InlineData("http://tiki.vn/abc-p123.html", true)]
        [InlineData("tiki.vn/abc-p123.html", true)]                 // người dùng dán thiếu https://
        [InlineData("  https://TIKI.VN/abc-p123.html  ", true)]
        [InlineData("https://tka.tiki.vn/widget/api", true)]        // tên miền con
        [InlineData("https://shopee.vn/abc-p123.html", false)]      // trước đây bị nhận nhầm là Tiki (chỉ dựa regex)
        [InlineData("https://nottiki.vn/abc-p123.html", false)]
        [InlineData("https://tiki.vn.evil.com/abc-p123.html", false)]
        [InlineData("abc-p123.html", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsTikiUrl(string? url, bool expected)
        {
            Assert.Equal(expected, TikiUrlParser.IsTikiUrl(url!));
        }

        [Fact]
        public void NormalizeProductUrl()
        {
            Assert.Equal("https://tiki.vn/product-p123.html", TikiUrlParser.NormalizeProductUrl("123"));
        }

        // Giữ nguyên cách PriceCrawlerService cũ dựng API path (có spid nếu link có)
        [Theory]
        [InlineData("https://tiki.vn/product-p123.html", "/api/v2/products/123")]
        [InlineData("https://tiki.vn/abc-p123.html?spid=456", "/api/v2/products/123?spid=456")]
        [InlineData("https://tiki.vn/abc-p123.html?itm_campaign=x&spid=789", "/api/v2/products/123?spid=789")]
        [InlineData("tiki.vn/abc-p123.html?spid=1", "/api/v2/products/123?spid=1")]
        public void BuildProductApiPath(string link, string expected)
        {
            Assert.Equal(expected, TikiUrlParser.BuildProductApiPath(link));
        }

        [Fact]
        public void BuildProductApiPath_NoProductId_Throws()
        {
            Assert.Throws<ArgumentException>(() => TikiUrlParser.BuildProductApiPath("https://tiki.vn/khuyen-mai"));
        }

        [Fact]
        public void BuildSearchApiPath_EscapesKeyword()
        {
            Assert.Equal("/api/v2/products?limit=10&q=%C4%91i%E1%BB%87n%20tho%E1%BA%A1i%20apple",
                TikiUrlParser.BuildSearchApiPath("điện thoại apple", 10));
        }

        [Theory]
        [InlineData("apple-iphone-15-p123.html?spid=1", "https://tiki.vn/apple-iphone-15-p123.html?spid=1")]
        [InlineData("/apple-iphone-15-p123.html", "https://tiki.vn/apple-iphone-15-p123.html")]
        [InlineData("https://tiki.vn/x-p1.html", "https://tiki.vn/x-p1.html")]
        [InlineData("http://a.b/c", "http://a.b/c")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("   ", "")]
        public void ToAbsoluteUrl(string? urlPath, string expected)
        {
            Assert.Equal(expected, TikiUrlParser.ToAbsoluteUrl(urlPath!));
        }

        [Theory]
        [InlineData("https://tiki.vn/khuyen-mai/fonterra-chinh-hang", "Fonterra Chinh Hang")]
        [InlineData("https://tiki.vn/khuyen-mai/sieu-sale-11-11?itm_campaign=abc&x=1", "Sieu Sale 11 11")]
        [InlineData("fonterra-chinh-hang", "Fonterra Chinh Hang")]
        [InlineData("a--b", "A B")]
        [InlineData("UPPER-case-words", "UPPER Case Words")]
        [InlineData("https://tiki.vn/khuyen-mai/ngay-hoi-sieu-sale/", TikiUrlParser.DefaultEventTitle)] // dấu "/" cuối → slug rỗng
        [InlineData("https://tiki.vn/", TikiUrlParser.DefaultEventTitle)]
        [InlineData("", TikiUrlParser.DefaultEventTitle)]
        [InlineData(null, TikiUrlParser.DefaultEventTitle)]
        public void FormatEventTitle(string? rawUrl, string expected)
        {
            Assert.Equal(expected, TikiUrlParser.FormatEventTitle(rawUrl!));
        }

        [Fact]
        public void DefaultEventTitle_UnchangedText()
        {
            Assert.Equal("Sự kiện Tiki Khuyến Mãi", TikiUrlParser.DefaultEventTitle);
        }
    }
}
