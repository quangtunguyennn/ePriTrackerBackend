using System.Net;
using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Gearvn;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>HTML mẫu trang /khuyen-mai GearVN: dữ liệu nằm trong payload Next.js (self.__next_f.push) chứ không ở thẻ HTML.</summary>
    internal static class GearvnPromotionPages
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

        // Rút gọn từ payload thật (01/11/2026): chương trình 2 dùng tham chiếu "$45:...:promotions:0:primaryProduct" thay cho sản phẩm trùng
        private const string Flight = """
            0:["$","div",null,{}]
            45:{"promotions":[
              {"id":"01a1","title":"[Màn hình] Mua màn hình LG tặng chuột G304","slug":"gift-manlg-g304","offer":"$undefined","category":"Màn hình","dealType":"Quà tặng",
               "startAt":"2026-10-24T17:00:00Z","endAt":"2026-11-30T16:59:00Z","bannerUrl":"https://cdn.gearvn.com/lg-g304.jpg",
               "primaryProduct":{"sku":"MAN-LG-27GX704","role":"condition","name":"Màn hình LG 27GX704A","url":"/products/man-hinh-lg-27gx704a"},
               "products":[{"sku":"MAN-LG-27GX704","role":"condition","name":"Màn hình LG 27GX704A","url":"/products/man-hinh-lg-27gx704a"},
                           {"sku":"MOUSE-G304","role":"gift","name":"Chuột G304","url":"/products/chuot-g304","dealLabel":"Tặng kèm"}]},
              {"id":"01a2","title":"[Màn hình] Màn hình Asus Oled kèm bàn phím Miku 990K","slug":"deal-manasus-k3miku","offer":"PBK-20260619-0083 · Bàn phím Asus TUF Gaming K3 Gen II Hatsune Miku chỉ còn 990K","dealType":"Quà tặng",
               "startAt":"2026-10-19T17:00:00Z","endAt":"2026-12-31T16:59:00Z","bannerUrl":"https://cdn.gearvn.com/miku.jpg",
               "products":["$45:props:children:2:props:promotions:0:primaryProduct",
                           {"sku":"A","role":"condition","name":"Màn hình A","url":"/products/man-hinh-a"},
                           {"sku":"B","role":"condition","name":"Màn hình B","url":"/products/man-hinh-b"},
                           {"sku":"C","role":"condition","name":"Màn hình C (hết hàng)","url":"/products/man-hinh-c"},
                           {"sku":"D","role":"condition","name":"Màn hình D","url":"/products/man-hinh-d"},
                           {"sku":"E","role":"condition","name":"Màn hình E","url":"/products/man-hinh-e"},
                           {"sku":"F","role":"condition","name":"Màn hình F","url":"/products/man-hinh-f"},
                           {"sku":"G","role":"condition","name":"Màn hình G","url":"/products/man-hinh-g"},
                           {"sku":"K3","role":"gift","name":"Bàn phím K3 Miku","url":"/products/ban-phim-k3-miku","dealLabel":"Đồng giá 990.000đ"}]},
              {"id":"01a3","title":"Chương trình đã kết thúc","slug":"da-ket-thuc","dealType":"Giảm giá","startAt":"2026-09-01T00:00:00Z","endAt":"2026-10-31T16:59:00Z","products":[]},
              {"id":"01a4","title":"Chương trình sắp diễn ra","slug":"sap-dien-ra","dealType":"Giảm giá","startAt":"2026-11-10T17:00:00Z","endAt":"2026-11-30T16:59:00Z","products":[]}
            ]}
            """;

        /// <summary>Payload chia làm 2 đoạn push như trang thật (bộ đọc phải ghép lại trước khi cắt JSON).</summary>
        public static string Page1()
        {
            string flight = Flight.Replace("\r", "");
            int middle = flight.IndexOf("\"slug\":\"deal-manasus", StringComparison.Ordinal);
            return "<html><body><div>Chương trình khuyến mãi</div>"
                   + $"<script>self.__next_f.push([1,{JsonSerializer.Serialize(flight[..middle])}])</script>"
                   + $"<script>self.__next_f.push([1,{JsonSerializer.Serialize(flight[middle..])}])</script>"
                   + "</body></html>";
        }

        public const string EmptyPage = "<html><body>Không còn chương trình</body></html>";
    }

    public class GearvnPromotionParserTests
    {
        [Fact]
        public void Parse_ReadsPromotionsFromNextPayload_AndResolvesReferences()
        {
            var promotions = GearvnPromotionParser.Parse(GearvnPromotionPages.Page1());

            Assert.Equal(new[] { "gift-manlg-g304", "deal-manasus-k3miku", "da-ket-thuc", "sap-dien-ra" }, promotions.Select(p => p.Slug));

            var miku = promotions[1];
            Assert.Equal("PBK-20260619-0083 · Bàn phím Asus TUF Gaming K3 Gen II Hatsune Miku chỉ còn 990K", miku.Offer);
            Assert.Null(promotions[0].Offer); // "$undefined" của payload = không có giá trị
            Assert.Equal(new DateTimeOffset(2026, 12, 31, 16, 59, 0, TimeSpan.Zero), miku.EndAt);
            Assert.Equal(new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero), miku.StartAt);
            Assert.Equal("https://cdn.gearvn.com/miku.jpg", miku.BannerUrl);

            // Tham chiếu "$45:...:promotions:0:primaryProduct" → sản phẩm chính của chương trình đầu
            Assert.Equal("/products/man-hinh-lg-27gx704a", miku.Products[0].Url);
            Assert.Equal("gift", miku.Products.Last().Role);
            Assert.Equal("Đồng giá 990.000đ", miku.Products.Last().DealLabel);
        }

        [Theory]
        [InlineData("")]
        [InlineData("<html><body>Trang không có payload</body></html>")]
        [InlineData("<script>self.__next_f.push([1,\"{\\\"other\\\":1}\"])</script>")]
        public void Parse_NoPromotions_ReturnsEmpty(string html)
        {
            Assert.Empty(GearvnPromotionParser.Parse(html));
        }

        [Fact]
        public void Urls()
        {
            Assert.Equal("https://gearvn.com/khuyen-mai?promotion=deal-manasus-k3miku", GearvnPromotionParser.PromotionUrl("deal-manasus-k3miku"));
            Assert.Equal("https://gearvn.com/khuyen-mai", GearvnPromotionParser.ListPageUrl(1));
            Assert.Equal("https://gearvn.com/khuyen-mai?page=2", GearvnPromotionParser.ListPageUrl(2));
        }
    }

    public class GearvnEventSourceTests
    {
        private sealed class FixedTime : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = GearvnPromotionPages.Now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        /// <summary>Trả HTML theo URL; đếm số request.</summary>
        private sealed class PagesHandler : HttpMessageHandler
        {
            public Dictionary<string, (HttpStatusCode Status, string Body)> Pages { get; } = new();
            public List<string> Requested { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string url = request.RequestUri!.AbsoluteUri;
                Requested.Add(url);
                var (status, body) = Pages.TryGetValue(url, out var page) ? page : (HttpStatusCode.OK, GearvnPromotionPages.EmptyPage);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
        }

        private sealed class Factory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public Factory(HttpMessageHandler handler) => _handler = handler;
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        /// <summary>Trang sản phẩm giả: giá theo handle; "man-hinh-c" hết hàng.</summary>
        private sealed class FakeProductPages : IPlatformScraper
        {
            public List<string> Requested { get; } = new();
            public string Platform => GearvnScraper.PlatformName;
            public bool CanHandle(string productUrl) => GearvnUrlParser.IsGearvnUrl(productUrl);

            public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl)
            {
                normalizedUrl = GearvnUrlParser.TryGetHandle(productUrl, out var h) ? GearvnUrlParser.NormalizeProductUrl(h) : null!;
                return normalizedUrl != null;
            }

            public Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default)
            {
                Requested.Add(productUrl);
                GearvnUrlParser.TryGetHandle(productUrl, out var handle);
                bool inStock = handle != "man-hinh-c";
                return Task.FromResult(new ScrapedProduct
                {
                    ExternalId = handle,
                    Name = "SP " + handle,
                    Price = 5_000_000,
                    ImageUrl = $"https://cdn.hstatic.net/{handle}.png",
                    Url = productUrl,
                    IsAvailable = inStock
                });
            }
        }

        private readonly PagesHandler _http = new();
        private readonly FakeProductPages _products = new();
        private readonly FixedTime _time = new();

        private GearvnEventSource Source() => new(new Factory(_http), _products, NullLogger<GearvnEventSource>.Instance, _time);

        public GearvnEventSourceTests()
        {
            _http.Pages[GearvnPromotionParser.ListPageUrl(1)] = (HttpStatusCode.OK, GearvnPromotionPages.Page1());
        }

        [Fact]
        public async Task OnlyRunningPromotions_BecomeEvents_WithExactEndTime()
        {
            var events = await Source().GetLiveEventsAsync();

            Assert.Equal(new[] { "https://gearvn.com/khuyen-mai?promotion=gift-manlg-g304", "https://gearvn.com/khuyen-mai?promotion=deal-manasus-k3miku" },
                events.Select(e => e.Url)); // bỏ chương trình đã kết thúc và chưa bắt đầu
            var miku = events[1];
            Assert.Equal("[Màn hình] Màn hình Asus Oled kèm bàn phím Miku 990K", miku.Title);
            Assert.Equal(new DateTimeOffset(2026, 12, 31, 16, 59, 0, TimeSpan.Zero), miku.EndsAt);
            Assert.Equal("https://cdn.gearvn.com/miku.jpg", miku.ImageUrl);
        }

        [Fact]
        public async Task Products_MainOnly_InStock_CappedAt6_WithOfferAsDescription()
        {
            var miku = (await Source().GetLiveEventsAsync())[1];

            // 8 sản phẩm chính (bỏ quà tặng K3), C hết hàng bị bỏ, lấy tối đa 6
            Assert.Equal(GearvnEventSource.MaxProductsPerEvent, miku.Products.Count);
            Assert.DoesNotContain(miku.Products, p => p.Url.Contains("k3-miku") || p.Url.EndsWith("man-hinh-c"));
            Assert.Equal("https://gearvn.com/products/man-hinh-lg-27gx704a", miku.Products[0].Url);
            Assert.All(miku.Products, p =>
            {
                Assert.Equal("Bàn phím Asus TUF Gaming K3 Gen II Hatsune Miku chỉ còn 990K", p.Description); // bỏ mã nội bộ "PBK-… · "
                Assert.Equal(5_000_000m, p.Price);
                Assert.True(p.IsInStock);
            });
        }

        [Fact]
        public async Task NoOffer_DescriptionFromGift()
        {
            // Chương trình LG: offer = "$undefined" → mô tả lấy từ quà tặng
            var lg = (await Source().GetLiveEventsAsync())[0];
            Assert.Equal("Tặng: Chuột G304", Assert.Single(lg.Products).Description);
        }

        [Fact]
        public async Task ResultCachedTenMinutes_ProductDetailsReusedAcrossPromotions()
        {
            var source = Source();
            await source.GetLiveEventsAsync();
            int listRequests = _http.Requested.Count, productRequests = _products.Requested.Count;

            await source.GetLiveEventsAsync(); // trong 10 phút → không đọc lại gì
            Assert.Equal(listRequests, _http.Requested.Count);
            Assert.Equal(productRequests, _products.Requested.Count);

            // Sản phẩm LG nằm ở cả 2 chương trình nhưng chỉ đọc trang 1 lần
            Assert.Single(_products.Requested, u => u.EndsWith("man-hinh-lg-27gx704a"));

            _time.Now += TimeSpan.FromMinutes(11); // hết hạn kết quả → đọc lại danh sách, chi tiết sản phẩm còn hạn (6 giờ) thì dùng lại
            await source.GetLiveEventsAsync();
            Assert.True(_http.Requested.Count > listRequests);
            Assert.Equal(productRequests, _products.Requested.Count);
        }

        [Fact]
        public async Task ListPages_StopAtFirstEmptyPage()
        {
            await Source().GetLiveEventsAsync();

            Assert.Equal(new[] { GearvnPromotionParser.ListPageUrl(1), GearvnPromotionParser.ListPageUrl(2) }, _http.Requested);
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden, "Just a moment...")]          // Cloudflare chặn
        [InlineData(HttpStatusCode.OK, "<html>Giao diện mới</html>")]      // đổi giao diện → 0 chương trình
        public async Task FirstPageUnreadable_Throws_SoSyncKeepsExistingEvents(HttpStatusCode status, string body)
        {
            _http.Pages[GearvnPromotionParser.ListPageUrl(1)] = (status, body);

            await Assert.ThrowsAnyAsync<Exception>(() => Source().GetLiveEventsAsync());
        }

        [Fact]
        public async Task LaterPageFails_KeepsWhatWasRead()
        {
            _http.Pages[GearvnPromotionParser.ListPageUrl(2)] = (HttpStatusCode.InternalServerError, "lỗi");

            Assert.Equal(2, (await Source().GetLiveEventsAsync()).Count);
        }
    }
}
