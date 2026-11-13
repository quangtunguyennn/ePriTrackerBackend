using System.Diagnostics;
using System.Net;
using ePriTrackerBackend.Scraping.Core;
using ePriTrackerBackend.Scraping.Hacom;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class HacomEventSourceTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
            public int Calls { get; private set; }
            public Uri? LastUri { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
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

        private sealed class StubCapture : IPageResponseCapture
        {
            private readonly string? _json;
            public StubCapture(string? json) => _json = json; // null = trang cũng lỗi
            public int Calls { get; private set; }
            public string? LastPageUrl { get; private set; }

            public Task<string> CaptureResponseAsync(string pageUrl, string responseUrlContains, TimeSpan timeout, CancellationToken cancellationToken = default)
            {
                Calls++;
                LastPageUrl = pageUrl;
                return _json == null ? throw new TimeoutException("trang không tải API") : Task.FromResult(_json);
            }
        }

        private sealed class ManualTime : TimeProvider
        {
            public DateTimeOffset Now { get; set; }
            public override DateTimeOffset GetUtcNow() => Now;
        }

        // Rút gọn từ phản hồi thật GET .../TblProductDeal/get-list (01/11/2026)
        private const string DealJson = """
            {"success":true,"message":null,"data":[
              {"dealTitle":"Flash Sale Cuối Tuần 31/20/2026","fromDate":"2026-10-31T02:00:00Z","toDate":"2026-11-01T16:30:00Z",
               "url":"man-hinh-aoc-24b36xe-moao0269","productName":"Màn hình AOC 24B36XE","productImgUrl":"https://hacom.vn/media/product/aoc.jpg",
               "dealUnitSellingPrice":1990000,"marketPrice":3290000,"unitSellingPrice":2390000,"active":"A","orderNumber":2},
              {"dealTitle":"Flash Sale Cuối Tuần 31/20/2026","fromDate":"2026-10-31T02:00:00Z","toDate":"2026-11-01T16:30:00Z",
               "url":"/chuot-logitech-g102/","productName":"Chuột Logitech G102","productImgUrl":"https://hacom.vn/media/product/g102.jpg",
               "dealUnitSellingPrice":"299000","marketPrice":0,"unitSellingPrice":299000,"active":"A","orderNumber":1},
              {"dealTitle":"Flash Sale Cuối Tuần 31/20/2026","fromDate":"2026-10-31T02:00:00Z","toDate":"2026-11-01T16:30:00Z",
               "url":"san-pham-tat","productName":"Sản phẩm đã tắt","dealUnitSellingPrice":100000,"active":"D","orderNumber":0},
              {"dealTitle":"Flash Sale Cuối Tuần 31/20/2026","fromDate":"2026-10-31T02:00:00Z","toDate":"2026-11-01T16:30:00Z",
               "url":"chua-co-gia","productName":"Chưa có giá","dealUnitSellingPrice":0,"active":"A","orderNumber":3},
              {"dealTitle":"Deal đã kết thúc","fromDate":"2026-10-20T00:00:00Z","toDate":"2026-10-25T00:00:00Z",
               "url":"deal-cu","productName":"Deal cũ","dealUnitSellingPrice":100000,"active":"A","orderNumber":1},
              {"dealTitle":"Deal sắp mở","fromDate":"2026-11-05T00:00:00Z","toDate":"2026-11-06T00:00:00Z",
               "url":"deal-sap-mo","productName":"Deal sắp mở","dealUnitSellingPrice":100000,"active":"A","orderNumber":1}
            ]}
            """;

        private static (HacomEventSource Source, StubHandler Api, StubCapture Page, ManualTime Time) Create(HttpStatusCode apiStatus, string apiBody, string? pageJson = DealJson)
        {
            var api = new StubHandler(apiStatus, apiBody);
            var page = new StubCapture(pageJson);
            var time = new ManualTime { Now = Now };
            return (new HacomEventSource(new StubFactory(api), page, NullLogger<HacomEventSource>.Instance, time), api, page, time);
        }

        [Fact]
        public void Parser_ReadsFields_AndRejectsBrokenResponses()
        {
            var deals = HacomDealParser.Parse(DealJson);

            Assert.Equal(6, deals.Count);
            var aoc = deals[0];
            Assert.Equal("Flash Sale Cuối Tuần 31/20/2026", aoc.Title);
            Assert.Equal("man-hinh-aoc-24b36xe-moao0269", aoc.Slug);
            Assert.Equal(1990000m, aoc.DealPrice);
            Assert.Equal(3290000m, aoc.MarketPrice);
            Assert.Equal(new DateTimeOffset(2026, 11, 1, 16, 30, 0, TimeSpan.Zero), aoc.ToDate);
            Assert.True(aoc.IsActive);
            Assert.Equal("chuot-logitech-g102", deals[1].Slug); // bỏ dấu "/" thừa
            Assert.Equal(299000m, deals[1].DealPrice);           // giá dạng chuỗi
            Assert.False(deals[2].IsActive);

            Assert.Throws<FormatException>(() => HacomDealParser.Parse(""));
            Assert.Throws<FormatException>(() => HacomDealParser.Parse("""{"items":[]}"""));
            Assert.Throws<FormatException>(() => HacomDealParser.Parse("""{"success":false,"message":"x","data":[]}"""));
        }

        [Fact]
        public async Task ApiOk_OneEventPerRunningProgram_WithValidProductsInHacomOrder()
        {
            var (source, api, page, _) = Create(HttpStatusCode.OK, DealJson);

            var events = await source.GetLiveEventsAsync();

            var evt = Assert.Single(events); // đã kết thúc / sắp mở bị bỏ
            Assert.Equal("Flash Sale Cuối Tuần 31/20/2026", evt.Title);
            Assert.StartsWith("https://hacom.vn/deal#", evt.Url);
            Assert.Equal(new DateTimeOffset(2026, 11, 1, 16, 30, 0, TimeSpan.Zero), evt.EndsAt);
            Assert.False(evt.ImageIsBanner);

            // Bỏ sản phẩm đã tắt và chưa có giá; sắp theo orderNumber
            Assert.Equal(new[] { "https://hacom.vn/chuot-logitech-g102", "https://hacom.vn/man-hinh-aoc-24b36xe-moao0269" }, evt.Products.Select(p => p.Url));
            Assert.Equal("https://hacom.vn/media/product/g102.jpg", evt.ImageUrl);

            var aoc = evt.Products[1];
            Assert.Equal(1990000m, aoc.Price);
            Assert.Equal(3290000m, aoc.OriginalPrice); // lấy giá cao nhất giữa giá niêm yết và giá thường
            Assert.Null(evt.Products[0].OriginalPrice); // không giảm thì không có giá gạch

            Assert.Equal(HacomEventSource.DealApiUrl, api.LastUri!.ToString());
            Assert.Equal(0, page.Calls);
        }

        [Fact]
        public async Task ApiBlocked_FallsBackToPageCapture()
        {
            var (source, _, page, _) = Create(HttpStatusCode.Forbidden, "blocked");

            var events = await source.GetLiveEventsAsync();

            Assert.Single(events);
            Assert.Equal(1, page.Calls);
            Assert.Equal(HacomEventSource.DealPageUrl, page.LastPageUrl);
        }

        [Fact]
        public async Task ApiChangedFormat_FallsBackToPageCapture()
        {
            var (source, _, page, _) = Create(HttpStatusCode.OK, "<html>maintenance</html>");

            Assert.Single(await source.GetLiveEventsAsync());
            Assert.Equal(1, page.Calls);
        }

        [Fact]
        public async Task ApiAndPageFail_Throws_SoSyncKeepsOldEvents()
        {
            var (source, _, _, _) = Create(HttpStatusCode.InternalServerError, "", pageJson: null);

            await Assert.ThrowsAsync<TimeoutException>(() => source.GetLiveEventsAsync());
        }

        [Fact]
        public async Task EmptyDealList_NoEvents_WithoutFallback()
        {
            var (source, _, page, _) = Create(HttpStatusCode.OK, """{"success":true,"data":[]}""");

            Assert.Empty(await source.GetLiveEventsAsync());
            Assert.Equal(0, page.Calls);
        }

        [Fact]
        public async Task Result_IsReused_ForFiveMinutes()
        {
            var (source, api, _, time) = Create(HttpStatusCode.OK, DealJson);

            await source.GetLiveEventsAsync();
            time.Now = Now.AddMinutes(4);
            await source.GetLiveEventsAsync();
            Assert.Equal(1, api.Calls);

            time.Now = Now.AddMinutes(6);
            await source.GetLiveEventsAsync();
            Assert.Equal(2, api.Calls);
        }

        [Fact]
        public void Products_AreCapped_AndDeduplicated()
        {
            var deals = Enumerable.Range(0, 60).Select(i => new HacomDeal
            {
                Title = "Flash Sale",
                FromDate = Now.AddHours(-1),
                ToDate = Now.AddHours(5),
                Slug = $"san-pham-{i % 50}", // 10 mã trùng
                Name = $"SP {i}",
                DealPrice = 100000,
                IsActive = true,
                Order = i
            }).ToList();

            var evt = Assert.Single(HacomEventSource.ToEvents(deals, Now));

            Assert.Equal(HacomEventSource.MaxProductsPerEvent, evt.Products.Count);
            Assert.Equal(evt.Products.Count, evt.Products.Select(p => p.Url).Distinct().Count());
        }
    }

    /// <summary>Chạy THẬT API deal HACOM và dự phòng mở trang /deal bằng Playwright. Bật bằng EPRI_LIVE_TESTS=1.</summary>
    public class HacomEventLiveTests
    {
        private static bool Live => Environment.GetEnvironmentVariable("EPRI_LIVE_TESTS") == "1";

        private readonly ITestOutputHelper _output;
        public HacomEventLiveTests(ITestOutputHelper output) => _output = output;

        private sealed class RealFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
                client.DefaultRequestHeaders.Add("Origin", "https://hacom.vn");
                client.DefaultRequestHeaders.Add("Referer", HacomEventSource.DealPageUrl);
                return client;
            }
        }

        [Fact]
        public async Task DirectApi_ReturnsDeals()
        {
            if (!Live) return;

            await using var renderer = new PlaywrightPageRenderer(NullLogger<PlaywrightPageRenderer>.Instance);
            var source = new HacomEventSource(new RealFactory(), renderer, NullLogger<HacomEventSource>.Instance);

            var sw = Stopwatch.StartNew();
            var events = await source.GetLiveEventsAsync();
            _output.WriteLine($"API: {sw.ElapsedMilliseconds} ms, {events.Count} sự kiện");
            foreach (var e in events)
                _output.WriteLine($"- {e.Title} | hết {e.EndsAt:u} | {e.Products.Count} SP | vd {e.Products.FirstOrDefault()?.Name} {e.Products.FirstOrDefault()?.Price:N0}");

            Assert.All(events, e => Assert.All(e.Products, p => Assert.True(HacomUrlParser.TryGetSlug(p.Url, out _))));
        }

        [Fact]
        public async Task PageCaptureFallback_ReadsSameApi()
        {
            if (!Live) return;

            await using var renderer = new PlaywrightPageRenderer(NullLogger<PlaywrightPageRenderer>.Instance);

            var sw = Stopwatch.StartNew();
            string json = await renderer.CaptureResponseAsync(HacomEventSource.DealPageUrl, "TblProductDeal/get-list", TimeSpan.FromSeconds(30));
            var deals = HacomDealParser.Parse(json);
            _output.WriteLine($"Trình duyệt: {sw.ElapsedMilliseconds} ms, {deals.Count} deal");
        }
    }
}
