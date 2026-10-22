using ePriTrackerBackend.Services.Products;

namespace ePriTrackerBackend.Tests.Services
{
    public class ProductDescriptionTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_ReturnsEmptyString(string? html)
        {
            Assert.Equal("", ProductDescription.Sanitize(html));
        }

        [Fact]
        public void RemovesScriptsEventHandlersAndJavascriptLinks()
        {
            string result = ProductDescription.Sanitize(
                "<p onclick=\"alert(1)\">A</p><script>alert(2)</script><img src=\"x.png\" onerror=\"alert(3)\"><a href=\"javascript:alert(4)\">B</a>");

            Assert.DoesNotContain("alert", result);
            Assert.DoesNotContain("<script", result);
            Assert.Contains("<p>A</p>", result);
        }

        [Fact]
        public void RemovesStyleClassIdAndForms()
        {
            string result = ProductDescription.Sanitize(
                "<div id=\"cpsKsp\" class=\"x\" style=\"max-height:5px\"><p>Nội dung</p><form><input name=\"q\"><button>Gửi</button></form></div>");

            Assert.DoesNotContain("style=", result);
            Assert.DoesNotContain("class=", result);
            Assert.DoesNotContain("id=", result);
            Assert.DoesNotContain("<form", result);
            Assert.DoesNotContain("<input", result);
            Assert.DoesNotContain("<button", result);
            Assert.Contains("<p>Nội dung</p>", result);
        }

        [Fact]
        public void KeepsContentMarkup_AndVietnameseText()
        {
            const string html =
                "<h2>Tính năng nổi bật</h2><p><strong>Màn hình</strong> 144Hz</p><ul><li>RAM 16GB</li></ul>" +
                "<table><tbody><tr><td>CPU</td><td>i7</td></tr></tbody></table><img src=\"https://cdn.vn/a.webp\" alt=\"Ảnh\">";

            string result = ProductDescription.Sanitize(html);

            Assert.Contains("<h2>Tính năng nổi bật</h2>", result);
            Assert.Contains("<strong>Màn hình</strong>", result);
            Assert.Contains("<li>RAM 16GB</li>", result);
            Assert.Contains("<td>CPU</td>", result);
            Assert.Contains("src=\"https://cdn.vn/a.webp\"", result);
        }

        [Fact]
        public void PlainText_StaysTheSame()
        {
            Assert.Equal("Mua điện thoại Samsung Galaxy A08 5G chính hãng", ProductDescription.Sanitize("Mua điện thoại Samsung Galaxy A08 5G chính hãng"));
        }
    }
}
