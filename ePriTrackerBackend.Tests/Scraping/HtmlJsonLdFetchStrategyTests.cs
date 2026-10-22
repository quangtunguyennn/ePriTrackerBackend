using System.Net;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class HtmlJsonLdFetchStrategyTests
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

            public HttpClient CreateClient(string name)
            {
                RequestedName = name;
                return new HttpClient(_handler, disposeHandler: false);
            }
        }

        private const string ProductHtml =
            "<html><script type=\"application/ld+json\">{\"@type\":\"Product\",\"name\":\"Robot\",\"offers\":{\"@type\":\"Offer\",\"price\":\"24990000\"}}</script></html>";

        private const string CategoryHtml =
            "<html><script type=\"application/ld+json\">{\"@type\":\"Product\",\"name\":\"Laptop ASUS\",\"offers\":{\"@type\":\"AggregateOffer\",\"lowPrice\":\"1\"}}</script></html>";

        [Fact]
        public async Task ReturnsJsonLdProduct_UsingNamedClient()
        {
            var handler = new StubHandler(HttpStatusCode.OK, ProductHtml);
            var factory = new StubFactory(handler);

            var json = await new HtmlJsonLdFetchStrategy(factory, "CellphoneS").FetchJsonAsync("https://shop.vn/robot.html", FetchOptions.Default);

            Assert.Equal("Robot", json.GetProperty("name").GetString());
            Assert.Equal("CellphoneS", factory.RequestedName);
            Assert.Equal("https://shop.vn/robot.html", handler.LastUri!.ToString());
            Assert.Equal(FetchStrategyNames.HttpClient, new HtmlJsonLdFetchStrategy(factory, "x").Name);
        }

        [Fact]
        public async Task HtmlFields_AreAddedToJson_InDocumentOrder_MissingBlockSkipped()
        {
            const string html =
                "<html><script type=\"application/ld+json\">{\"@type\":\"Product\",\"name\":\"Robot\",\"offers\":{\"@type\":\"Offer\",\"price\":\"1\"}}</script>" +
                "<div id=\"ksp\"><p>Nổi bật</p></div><div id=\"seo\"><p>Chi tiết</p></div></html>";
            var fields = new Dictionary<string, string>
            {
                ["x-desc"] = "//div[@id='ksp'] | //div[@id='seo']",
                ["x-missing"] = "//div[@id='khong-co']"
            };
            var strategy = new HtmlJsonLdFetchStrategy(new StubFactory(new StubHandler(HttpStatusCode.OK, html)), "x", fields);

            var json = await strategy.FetchJsonAsync("https://shop.vn/robot.html", FetchOptions.Default);

            Assert.Equal("Robot", json.GetProperty("name").GetString());
            Assert.Equal("<div id=\"ksp\"><p>Nổi bật</p></div><div id=\"seo\"><p>Chi tiết</p></div>", json.GetProperty("x-desc").GetString());
            Assert.False(json.TryGetProperty("x-missing", out _));
        }

        [Fact]
        public async Task CategoryPage_ThrowsReadableError()
        {
            var strategy = new HtmlJsonLdFetchStrategy(new StubFactory(new StubHandler(HttpStatusCode.OK, CategoryHtml)), "x");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.FetchJsonAsync("https://shop.vn/laptop/asus.html", FetchOptions.Default));
            Assert.Equal("Không đọc được thông tin sản phẩm từ trang này.", ex.Message);
        }

        [Fact]
        public async Task NotFound_ThrowsHttpRequestException()
        {
            var strategy = new HtmlJsonLdFetchStrategy(new StubFactory(new StubHandler(HttpStatusCode.NotFound, "")), "x");

            await Assert.ThrowsAsync<HttpRequestException>(() => strategy.FetchJsonAsync("https://shop.vn/khong-ton-tai.html", FetchOptions.Default));
        }

        [Fact]
        public async Task RelativePath_IsRejected()
        {
            var strategy = new HtmlJsonLdFetchStrategy(new StubFactory(new StubHandler(HttpStatusCode.OK, ProductHtml)), "x");

            await Assert.ThrowsAsync<ArgumentException>(() => strategy.FetchJsonAsync("/robot.html", FetchOptions.Default));
        }
    }
}
