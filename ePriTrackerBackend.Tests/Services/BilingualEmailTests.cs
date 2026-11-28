using ePriTrackerBackend.Services.Notification;

namespace ePriTrackerBackend.Tests.Services
{
    /// <summary>Email gửi từ job nền không biết ngôn ngữ người dùng chọn → luôn có cả tiếng Anh và tiếng Việt.</summary>
    public class BilingualEmailTests
    {
        [Fact]
        public void PriceDrop_HasEnglishAndVietnamese_WithBothPriceFormats()
        {
            var (subject, html) = EmailService.BuildPriceDropEmail("Khôi", "iPhone 17", 1_500_000m, 1_600_000m, "https://tiki.vn/p.html");

            Assert.Equal("[ePriTracker] Price target reached / Đã chạm giá mục tiêu: iPhone 17", subject);
            Assert.Contains("A product you're tracking just dropped to your target price.", html);
            Assert.Contains("Một sản phẩm bạn đang theo dõi vừa giảm đến giá mục tiêu.", html);
            Assert.Contains("1,500,000 ₫", html); // phần tiếng Anh
            Assert.Contains("1.500.000 ₫", html); // phần tiếng Việt
            Assert.Contains("View product / Xem sản phẩm", html);
        }

        [Fact]
        public void Delisted_HasEnglishAndVietnamese_MissingPriceAndName()
        {
            var (subject, html) = EmailService.BuildDelistedEmail("", "Laptop", null, "https://cellphones.com.vn/x.html");

            Assert.Equal("[ePriTracker] Product no longer available / Sản phẩm không còn trên sàn: Laptop", subject);
            Assert.Contains("Hi there,", html);
            Assert.Contains("Xin chào bạn,", html);
            Assert.Contains("not available", html);
            Assert.Contains("không có", html);
            Assert.Contains("đã bị gỡ khỏi sàn", html);
        }

        [Fact]
        public void ProductNameFromPlatform_IsHtmlEncoded()
        {
            var (_, html) = EmailService.BuildPriceDropEmail("A", "<script>alert(1)</script>", 1, 2, "https://x\" onclick=\"y");

            Assert.DoesNotContain("<script>", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("\" onclick=\"", html);
        }
    }
}
