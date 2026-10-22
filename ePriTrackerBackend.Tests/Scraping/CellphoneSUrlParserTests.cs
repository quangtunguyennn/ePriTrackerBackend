using ePriTrackerBackend.Scraping.CellphoneS;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>Các dạng link lấy từ khảo sát thực tế CellphoneS (20/10/2026).</summary>
    public class CellphoneSUrlParserTests
    {
        private const string Slug = "dien-thoai-honor-x9c-smart-5g-12gb-256gb-cu-dep";
        private const string Normalized = "https://cellphones.com.vn/" + Slug + ".html";

        [Theory]
        [InlineData("https://cellphones.com.vn/" + Slug + ".html")]
        [InlineData("https://cellphones.com.vn/" + Slug + ".html?utm_source=zalo&ref=abc#thong-so")] // query + #
        [InlineData("https://cellphones.com.vn/" + Slug + ".html/")]                                 // "/" cuối
        [InlineData("https://cellphones.com.vn/DIEN-THOAI-HONOR-X9C-SMART-5G-12GB-256GB-CU-DEP.html")] // chữ hoa
        [InlineData("http://cellphones.com.vn/" + Slug + ".html")]
        [InlineData("https://www.cellphones.com.vn/" + Slug + ".html")]
        [InlineData("cellphones.com.vn/" + Slug + ".html")]                                          // thiếu https://
        public void ProductLinks_NormalizeToSameUrl(string url)
        {
            Assert.True(CellphoneSUrlParser.TryGetSlug(url, out var slug));
            Assert.Equal(Slug, slug);
            Assert.Equal(Normalized, CellphoneSUrlParser.NormalizeProductUrl(slug));
        }

        [Theory]
        [InlineData("https://cellphones.com.vn/" + Slug)]          // thiếu .html → trang 404
        [InlineData("https://cellphones.com.vn/laptop/asus.html")]  // trang danh mục nhiều đoạn
        [InlineData("https://cellphones.com.vn/")]
        [InlineData("https://cellphones.com.vn/.html")]
        [InlineData("https://cellphones.com.vn/iphone_17.html")]    // ký tự lạ
        [InlineData("https://tiki.vn/" + Slug + ".html")]
        [InlineData("https://cellphones.com.vn.evil.com/" + Slug + ".html")]
        [InlineData("")]
        [InlineData(null)]
        public void InvalidLinks_AreRejected(string? url)
        {
            Assert.False(CellphoneSUrlParser.TryGetSlug(url!, out var slug));
            Assert.Null(slug);
        }

        [Theory]
        [InlineData("https://cellphones.com.vn/mobile.html", true)]   // host đúng (dù là danh mục – JSON-LD sẽ loại sau)
        [InlineData("https://www.cellphones.com.vn/x.html", true)]
        [InlineData("https://sforum.cellphones.com.vn/x", false)]     // trang tin tức, không phải cửa hàng
        [InlineData("https://tiki.vn/x-p1.html", false)]
        [InlineData(null, false)]
        public void IsCellphoneSUrl(string? url, bool expected)
        {
            Assert.Equal(expected, CellphoneSUrlParser.IsCellphoneSUrl(url!));
        }
    }
}
