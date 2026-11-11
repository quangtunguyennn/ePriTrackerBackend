using System.Text.Json;
using ePriTrackerBackend.Scraping.Tiki;
using Microsoft.Playwright;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// Chạy script đọc sản phẩm trang sự kiện Tiki (<see cref="TikiBrowserService.EventProductsScript"/>) trong Chromium thật,
    /// trên HTML mẫu (không cần mạng). Cần trình duyệt Playwright đã cài → bật bằng EPRI_LIVE_TESTS=1 như các test trình duyệt khác.
    /// </summary>
    public class TikiEventScriptTests
    {
        private static bool Live => Environment.GetEnvironmentVariable("EPRI_LIVE_TESTS") == "1";

        // Rút gọn từ https://tiki.vn/bo-suu-tap/tupperwear-b14067 (01/11/2026): thẻ <a class="sc-…"> sinh tự động,
        // link KHÔNG có ".html", giá gốc gạch ngang bằng CSS, nhãn "-20%".
        private const string CollectionPageHtml = """
            <html><body>
              <a href="/khuyen-mai/khac">Xem thêm ưu đãi 50.000 ₫</a>
              <div>
                <a class="sc-73fa1a47-1 jMSTqg" href="https://tiki.vn/bo-4-hop-dung-thuc-pham-tupperware-p27938088">
                  <img alt="sale_rank" src="https://salt.tikicdn.com/ts/upload/4f/dc/1e/huy-chuong-1.png">
                  <img alt="Bộ 4 hộp đựng thực phẩm Tupperware" src="https://salt.tikicdn.com/cache/280x280/ts/product/07/2f/hop.jpg">
                  <div>Bộ 4 hộp đựng thực phẩm Tupperware</div>
                  <div><span>660.000 ₫</span> <span style="text-decoration: line-through">825.000 ₫</span> <span>-20%</span></div>
                </a>
                <a class="sc-73fa1a47-1 jMSTqg" href="/binh-nuoc-tupperware-eco-p12345678?spid=1">
                  <img alt="Bình nước Tupperware Eco" src="https://salt.tikicdn.com/binh.jpg">
                  <div>Bình nước Tupperware Eco 1L</div>
                  <div><span>189.000 ₫</span></div>
                </a>
              </div>
            </body></html>
            """;

        // Thẻ kiểu cũ (trang /khuyen-mai/ đang chạy tốt) – phải giữ nguyên kết quả
        private const string OldStylePageHtml = """
            <html><body>
              <div data-view-id="product_list_item" data-view-content='{"click_data":{"id":111}}'>
                <a href="/sach-hay-p111.html"><h3>Sách hay</h3><span class="price-discount__price">90.000 ₫</span><span class="price-discount__original-price">120.000 ₫</span></a>
              </div>
            </body></html>
            """;

        private static async Task<JsonElement> RunScriptAsync(string html)
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(html);
            return (await page.EvaluateAsync<JsonElement>(TikiBrowserService.EventProductsScript)).GetProperty("data");
        }

        [Fact]
        public async Task CollectionPage_GeneratedClassCards_ReadByFallback()
        {
            if (!Live) return;

            var data = await RunScriptAsync(CollectionPageHtml);

            Assert.Equal(2, data.GetArrayLength());
            var first = data[0];
            Assert.Equal(27938088, first.GetProperty("id").GetInt32());
            Assert.Equal("Bộ 4 hộp đựng thực phẩm Tupperware", first.GetProperty("name").GetString());
            Assert.Equal(660000, first.GetProperty("price").GetInt32());
            Assert.Equal(825000, first.GetProperty("original_price").GetInt32());          // giá gạch ngang
            Assert.Equal("bo-4-hop-dung-thuc-pham-tupperware-p27938088.html", first.GetProperty("url_path").GetString()); // thêm .html để TikiUrlParser nhận
            Assert.Equal("https://salt.tikicdn.com/cache/280x280/ts/product/07/2f/hop.jpg", first.GetProperty("thumbnail_url").GetString()); // không phải huy chương Top 10
            Assert.Equal("https://salt.tikicdn.com/binh.jpg", data[1].GetProperty("thumbnail_url").GetString());                          // thẻ chỉ có 1 ảnh

            var second = data[1];
            Assert.Equal(12345678, second.GetProperty("id").GetInt32());
            Assert.Equal(189000, second.GetProperty("price").GetInt32());
            Assert.Equal(189000, second.GetProperty("original_price").GetInt32());
            Assert.Equal("binh-nuoc-tupperware-eco-p12345678.html", second.GetProperty("url_path").GetString()); // bỏ ?spid=
        }

        [Fact]
        public async Task OldStyleCards_StillPreferred()
        {
            if (!Live) return;

            var item = Assert.Single((await RunScriptAsync(OldStylePageHtml)).EnumerateArray());
            Assert.Equal(111, item.GetProperty("id").GetInt32());
            Assert.Equal("Sách hay", item.GetProperty("name").GetString());
            Assert.Equal(90000, item.GetProperty("price").GetInt32());
            Assert.Equal(120000, item.GetProperty("original_price").GetInt32());
        }

        [Fact]
        public async Task SponsoredCard_TrackingLink_UrlRebuiltFromId()
        {
            if (!Live) return;

            // Sản phẩm quảng cáo (Tiki Ads) trên trang TikiVIP / Mom Club: href là link theo dõi //tka.tiki.vn/pixel/...
            const string html = """
                <html><body>
                  <div data-view-id="product_list_item" data-view-content='{"click_data":{"id":275000001}}'>
                    <a href="//tka.tiki.vn/pixel/pixel.html?data=abc"><h3>Điện Thoại Samsung Galaxy A17 5G</h3><span class="price-discount__price">4.890.000 ₫</span></a>
                  </div>
                </body></html>
                """;

            var item = Assert.Single((await RunScriptAsync(html)).EnumerateArray());
            Assert.Equal(275000001, item.GetProperty("id").GetInt32());
            Assert.Equal("product-p275000001.html", item.GetProperty("url_path").GetString());
        }

        [Fact]
        public async Task PageWithoutProducts_ReturnsEmpty()
        {
            if (!Live) return;

            Assert.Equal(0, (await RunScriptAsync("<html><body><h1>Mã giảm giá TikiVIP</h1><a href='/khuyen-mai/x'>Lưu mã</a></body></html>")).GetArrayLength());
        }
    }
}
