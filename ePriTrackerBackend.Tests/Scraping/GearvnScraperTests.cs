using System.Net;
using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Gearvn;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class GearvnUrlParserTests
    {
        [Theory]
        [InlineData("https://gearvn.com/products/logitech-g502-hero", "logitech-g502-hero")]
        [InlineData("https://www.gearvn.com/products/Logitech-G502-Hero?variant=123#mo-ta", "logitech-g502-hero")]
        [InlineData("http://gearvn.com/products/logitech-g502-hero/", "logitech-g502-hero")]
        [InlineData("gearvn.com/products/logitech-g502-hero", "logitech-g502-hero")]                           // dán thiếu https://
        [InlineData("https://gearvn.com/collections/chuot-choi-game/products/logitech-g502-hero", "logitech-g502-hero")] // kiểu Haravan
        [InlineData("https://gearvn.com/products/logitech-g502-hero Chuột Logitech G502", "logitech-g502-hero")] // dán kèm tiêu đề
        public void ProductLinks_AreRecognized_AndNormalized(string url, string handle)
        {
            Assert.True(GearvnUrlParser.TryGetHandle(url, out var actual));
            Assert.Equal(handle, actual);
            Assert.Equal($"https://gearvn.com/products/{handle}", GearvnUrlParser.NormalizeProductUrl(actual));
        }

        [Theory]
        [InlineData("https://gearvn.com/")]
        [InlineData("https://gearvn.com/collections/chuot-choi-game")]   // danh mục
        [InlineData("https://gearvn.com/pages/chinh-sach-bao-hanh")]
        [InlineData("https://gearvn.com/blogs/thu-thuat/abc")]
        [InlineData("https://gearvn.com/products/")]
        [InlineData("https://gearvn.com/products/a/b")]
        [InlineData("https://gearvn.com/products/chu%E1%BB%99t")]         // ký tự lạ
        [InlineData("https://fakegearvn.com/products/abc")]
        [InlineData("https://tiki.vn/products/abc")]
        [InlineData("")]
        public void OtherLinks_AreRejected(string url)
        {
            Assert.False(GearvnUrlParser.TryGetHandle(url, out _));
        }
    }

    public class GearvnScraperTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
            public Uri? LastUri { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastUri = request.RequestUri;
                return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
            }
        }

        private sealed class StubFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public StubFactory(HttpMessageHandler handler) => _handler = handler;
            public string? RequestedName { get; private set; }
            public HttpClient CreateClient(string name) { RequestedName = name; return new HttpClient(_handler, disposeHandler: false); }
        }

        private sealed class StubFetcher : IHybridFetcher
        {
            private readonly string _json;
            public StubFetcher(string json) => _json = json;
            public string? LastPath { get; private set; }
            public Task<JsonElement> FetchJsonAsync(string path, FetchOptions? options = null, CancellationToken cancellationToken = default)
            {
                LastPath = path;
                return Task.FromResult(JsonDocument.Parse(_json).RootElement.Clone());
            }
        }

        // Rút gọn từ JSON-LD trang https://gearvn.com/products/ban-phim-co-asus-rog-falchion-ace-75-he-v2x-red (31/10/2026)
        private const string ProductJson = """
            {"@context":"https://schema.org","@type":"Product","name":"Bàn phím cơ ASUS ROG Falchion Ace 75 HE V2X Red",
             "sku":"KB-ASU-ROG-FALCHION-ACE-75-HE-V2X-RED","description":"Bàn phím cơ ASUS ROG Falchion Ace 75 HE V2X Red chính hãng",
             "image":["https://product.hstatic.net/200000722513/product/falchion.png"],
             "offers":{"@type":"Offer","price":5890000,"priceCurrency":"VND","availability":"https://schema.org/InStock"}}
            """;

        // Thẻ sản phẩm rút gọn từ HTML thật của https://gearvn.com/search?q=chuot+logitech+g502 (31/10/2026):
        // link bọc cả thẻ, nhãn "Giảm 15%", tên trong <p class="product-card-name">, màu sắc, giá gốc + giá giảm.
        private const string SearchHtml = """
            <html><body>
            <nav><a href="/collections/chuot-logitech">Chuột Logitech</a><a href="/pages/khuyen-mai">Khuyến mãi 500.000đ</a></nav>
            <div data-testid="catalog-product-grid">
              <a class="product-card group" href="/products/logitech-g502-hero">
                <div><span>Giảm <!-- -->15<!-- -->%</span>
                  <div class="product-card-image-slot"><img alt="Chuột Logitech G502 Hero Gaming" src="https://product.hstatic.net/200000722513/product/g502.png" class="product-card-image"/></div></div>
                <div data-slot="product-card-content">
                  <p class="product-card-name cursor-text line-clamp-2">Chuột Logitech G502 Hero Gaming</p>
                  <div class="product-card-specs"><span class="product-card-spec"><span>Đen</span></span><span class="product-card-spec"><span>Có dây</span></span></div>
                  <div class="product-card-price"><span class="product-card-price-original">1.090.000đ</span><span class="product-card-price-sale">920.000đ</span></div>
                </div>
              </a>
              <a class="product-card group" href="/products/chuot-logitech-g502-x-plus-lightspeed-black">
                <div class="product-card-image-slot"><img alt="Chuột Logitech G502 X Plus" src="https://product.hstatic.net/200000722513/product/g502x.png" class="product-card-image"/></div>
                <div data-slot="product-card-content">
                  <p class="product-card-name">Chuột Logitech G502 X Plus Lightspeed Black</p>
                  <div class="product-card-price"><span class="product-card-price-sale">3.490.000đ</span></div>
                </div>
              </a>
            </div>
            </body></html>
            """;

        [Fact]
        public async Task GetProduct_FetchesNormalizedUrl_AndMapsJsonLd()
        {
            var fetcher = new StubFetcher(ProductJson);
            var scraper = new GearvnScraper(fetcher, new StubFactory(new StubHandler(HttpStatusCode.OK, "")), NullLogger<GearvnScraper>.Instance);

            var product = await scraper.GetProductAsync("https://www.gearvn.com/products/Ban-Phim-Co-Asus-Rog-Falchion-Ace-75-He-V2x-Red?variant=1");

            Assert.Equal("https://gearvn.com/products/ban-phim-co-asus-rog-falchion-ace-75-he-v2x-red", fetcher.LastPath);
            Assert.Equal("ban-phim-co-asus-rog-falchion-ace-75-he-v2x-red", product.ExternalId);
            Assert.Equal("Bàn phím cơ ASUS ROG Falchion Ace 75 HE V2X Red", product.Name);
            Assert.Equal(5890000m, product.Price);
            Assert.Equal("https://product.hstatic.net/200000722513/product/falchion.png", product.ImageUrl);
            Assert.True(product.IsInStock);
            Assert.Equal(fetcher.LastPath, product.Url);
        }

        [Theory]
        [InlineData("https://schema.org/OutOfStock", 5890000)]
        [InlineData("https://schema.org/InStock", 0)] // giá 0 = chưa có giá → coi như hết hàng
        public void OutOfStock_Detected(string availability, int price)
        {
            string json = ProductJson.Replace("https://schema.org/InStock", availability).Replace("5890000", price.ToString());
            var product = GearvnScraper.ToScrapedProduct(JsonDocument.Parse(json).RootElement, "x", "u");
            Assert.False(product.IsInStock);
        }

        [Fact]
        public async Task GetProduct_NonProductLink_Throws()
        {
            var scraper = new GearvnScraper(new StubFetcher(ProductJson), new StubFactory(new StubHandler(HttpStatusCode.OK, "")), NullLogger<GearvnScraper>.Instance);
            await Assert.ThrowsAsync<ArgumentException>(() => scraper.GetProductAsync("https://gearvn.com/collections/chuot-choi-game"));
        }

        [Fact]
        public async Task Search_ReadsCards_NameFromNameClass_SalePrice_ProductLinksOnly()
        {
            var handler = new StubHandler(HttpStatusCode.OK, SearchHtml);
            var factory = new StubFactory(handler);
            var scraper = new GearvnScraper(new StubFetcher(ProductJson), factory, NullLogger<GearvnScraper>.Instance);

            var results = await scraper.SearchAsync("chuột logitech g502", 10);

            Assert.Equal("https://gearvn.com/search?q=chu%E1%BB%99t%20logitech%20g502", handler.LastUri!.AbsoluteUri);
            Assert.Equal(GearvnScraper.PlatformName, factory.RequestedName);
            Assert.Equal(2, results.Count);

            var g502 = results[0];
            Assert.Equal("Chuột Logitech G502 Hero Gaming", g502.Name);   // không lẫn "Giảm 15%", "Đen"
            Assert.Equal(920000m, g502.Price);                           // giá giảm, không phải giá gốc 1.090.000
            Assert.Equal("https://gearvn.com/products/logitech-g502-hero", g502.Url);
            Assert.Equal("logitech-g502-hero", g502.ExternalId);
            Assert.Equal("https://product.hstatic.net/200000722513/product/g502.png", g502.ImageUrl);

            Assert.Equal("Chuột Logitech G502 X Plus Lightspeed Black", results[1].Name);
            Assert.Equal(3490000m, results[1].Price);
        }

        [Fact]
        public async Task Search_RespectsLimit_AndBlankKeyword()
        {
            var scraper = new GearvnScraper(new StubFetcher(ProductJson), new StubFactory(new StubHandler(HttpStatusCode.OK, SearchHtml)), NullLogger<GearvnScraper>.Instance);

            Assert.Single(await scraper.SearchAsync("g502", 1));
            Assert.Empty(await scraper.SearchAsync("  ", 10));
        }

        [Fact]
        public async Task Search_Blocked_Throws_SoFinderSkipsPlatform()
        {
            // Cloudflare chặn (403) → ném lỗi; SuggestionFinder ghi log rồi bỏ qua sàn này, các sàn khác vẫn có gợi ý
            var scraper = new GearvnScraper(new StubFetcher(ProductJson), new StubFactory(new StubHandler(HttpStatusCode.Forbidden, "Just a moment...")), NullLogger<GearvnScraper>.Instance);
            await Assert.ThrowsAsync<HttpRequestException>(() => scraper.SearchAsync("g502", 10));
        }
    }
}
