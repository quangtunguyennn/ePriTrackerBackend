using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Tiki;
using System.Net;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class TikiHttpFetchStrategyTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
            public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
            public Uri? LastUri { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastUri = request.RequestUri;
                return _respond(request, cancellationToken);
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

        private static StubHandler Respond(HttpStatusCode status, string body = "{}")
            => new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

        [Fact]
        public async Task BuildsTikiUrl_UsesNamedClient_AndParsesJson()
        {
            var handler = Respond(HttpStatusCode.OK, "{\"name\":\"iPhone\",\"price\":1000}");
            var factory = new StubFactory(handler);

            var json = await new TikiHttpFetchStrategy(factory).FetchJsonAsync("/api/v2/products/123", FetchOptions.Default);

            Assert.Equal("https://tiki.vn/api/v2/products/123", handler.LastUri!.ToString());
            Assert.Equal(TikiHttpFetchStrategy.HttpClientName, factory.RequestedName);
            Assert.Equal("iPhone", json.GetProperty("name").GetString());
            Assert.Equal(FetchStrategyNames.HttpClient, new TikiHttpFetchStrategy(factory).Name);
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.NotFound)]
        public async Task NonSuccessStatus_Throws_SoHybridFetcherFallsBack(HttpStatusCode status)
        {
            var strategy = new TikiHttpFetchStrategy(new StubFactory(Respond(status)));

            await Assert.ThrowsAsync<HttpRequestException>(() => strategy.FetchJsonAsync("/x", FetchOptions.Default));
        }

        [Fact]
        public async Task Timeout_CancelsSlowRequest()
        {
            var slow = new StubHandler(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var strategy = new TikiHttpFetchStrategy(new StubFactory(slow));
            var options = new FetchOptions { Timeout = TimeSpan.FromMilliseconds(100) };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => strategy.FetchJsonAsync("/x", options));
        }

        [Fact]
        public async Task AbsoluteTikiUrl_IsUsedAsIs()
        {
            // Bug B (đã sửa): trước đây URL tuyệt đối bị ghép thành "https://tiki.vnhttps://tka..." → luôn rơi xuống Playwright
            var handler = Respond(HttpStatusCode.OK);
            await new TikiHttpFetchStrategy(new StubFactory(handler))
                .FetchJsonAsync("https://tka.tiki.vn/widget/api/v1/banners-group?group=x", FetchOptions.Default);

            Assert.Equal("https://tka.tiki.vn/widget/api/v1/banners-group?group=x", handler.LastUri!.ToString());
        }

        [Fact]
        public async Task AbsoluteNonTikiUrl_IsRejected_WithoutSendingRequest()
        {
            var handler = Respond(HttpStatusCode.OK);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                new TikiHttpFetchStrategy(new StubFactory(handler)).FetchJsonAsync("https://evil.example/api", FetchOptions.Default));
            Assert.Null(handler.LastUri);
        }
    }
}
