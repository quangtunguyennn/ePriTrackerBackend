using ePriTrackerBackend.Scraping;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.CellphoneS;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// Chạy THẬT trên trang chủ CellphoneS (Playwright + mạng) – kiểm tra bộ đọc còn khớp giao diện hiện tại.
    /// Mặc định bỏ qua; bật bằng biến môi trường EPRI_LIVE_TESTS=1.
    /// </summary>
    public class CellphoneSLiveTests
    {
        private readonly ITestOutputHelper _output;
        public CellphoneSLiveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task HomePage_RealRender_FindsEventsWithProducts()
        {
            if (Environment.GetEnvironmentVariable("EPRI_LIVE_TESTS") != "1") return;

            await using var renderer = new PlaywrightPageRenderer(NullLogger<PlaywrightPageRenderer>.Instance);
            string html = await renderer.RenderAsync(CellphoneSHomeEventParser.HomeUrl);
            if (Environment.GetEnvironmentVariable("EPRI_LIVE_DUMP") is { Length: > 0 } dumpPath)
                await File.WriteAllTextAsync(dumpPath, html); // lưu HTML đã render để soi khi bộ đọc sai
            var events = CellphoneSHomeEventParser.Parse(html, DateTimeOffset.UtcNow);

            Assert.NotEmpty(events);
            foreach (var evt in events)
            {
                _output.WriteLine($"{evt.Title} | {evt.Url} | hết hạn {evt.EndsAt?.ToString() ?? "(cuốn chiếu)"} | {evt.Products.Count} sản phẩm");
                foreach (var p in evt.Products.Take(3))
                    _output.WriteLine($"   {p.Name} | {p.Price:N0} (gốc {p.OriginalPrice:N0}) | {p.Description} | {p.Url}");
                foreach (var p in evt.Products.Where(p => !IsHttpUrl(p.ImageUrl)))
                    _output.WriteLine($"   ⚠ THIẾU ẢNH: {p.Name} | ảnh = \"{p.ImageUrl}\"");

                Assert.True(evt.Products.Count >= 4, evt.Title);
                Assert.All(evt.Products, p => Assert.True(p.Price > 0));
            }
        }

        // ---------- GraphQL nội bộ ----------

        private static bool Live => Environment.GetEnvironmentVariable("EPRI_LIVE_TESTS") == "1";

        /// <summary>DI giống app (HttpClient có giới hạn tốc độ 1 request/giây như cấu hình thật).</summary>
        private static ServiceProvider Services()
            => new ServiceCollection()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scraping:Platforms:CellphoneS:MinIntervalMs"] = "1000"
                }).Build())
                .AddLogging()
                .AddScraping()
                .BuildServiceProvider();

        [Fact]
        public async Task Api_Search_FindsSuggestionCandidates()
        {
            if (!Live) return;
            await using var sp = Services();
            var scraper = sp.GetRequiredService<CellphoneSScraper>();

            foreach (string keyword in new[] { "laptop asus tuf gaming a16", "dien thoai samsung galaxy a08 5g", "chuot gaming logitech g502 hero" })
            {
                var results = await scraper.SearchAsync(keyword, 10);
                _output.WriteLine($"[{keyword}] → {results.Count} kết quả");
                foreach (var p in results) _output.WriteLine($"   {p.Name} | {p.Price:N0} (gốc {p.OriginalPrice:N0}) | {p.Url} | {p.ImageUrl}");
                Assert.NotEmpty(results);
            }
        }

        [Fact]
        public async Task Api_BatchPrices_MatchProductPages()
        {
            if (!Live) return;
            await using var sp = Services();
            var scraper = sp.GetRequiredService<CellphoneSScraper>();
            string[] urls =
            {
                "https://cellphones.com.vn/laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.html",
                "https://cellphones.com.vn/chuot-choi-game-co-day-logitech-g502-hero.html",
                "https://cellphones.com.vn/dien-thoai-samsung-galaxy-a08-5g.html"
            };

            var prices = await scraper.GetCurrentPricesAsync(urls.Select(u => new BatchPriceRequest { ProductUrl = u }).ToList());

            foreach (string url in urls)
            {
                var page = await scraper.GetProductAsync(url); // JSON-LD để đối chiếu
                prices.TryGetValue(url, out var api);
                _output.WriteLine($"{url}\n   API: id={api?.PlatformProductId} giá={api?.Product.Price:N0} còn hàng={api?.Product.IsInStock} | JSON-LD: giá={page.Price:N0} còn hàng={page.IsInStock}");
                Assert.NotNull(api);
                Assert.Equal(page.Price, api!.Product.Price);
            }
        }

        [Fact]
        public async Task Api_FlashSale_UpcomingProgramReadAsIfRunning()
        {
            if (!Live) return;
            await using var sp = Services();
            var api = sp.GetRequiredService<ICellphoneSApi>();

            var uds = await api.GetHomeFlashSaleGroupUdsAsync();
            _output.WriteLine("Nhóm flash sale trang chủ: " + string.Join(", ", uds));
            var programs = new List<CellphoneSFlashSaleProgram>();
            foreach (var ud in uds)
            {
                var group = await api.GetFlashSaleGroupAsync(ud);
                foreach (var p in group?.Programs ?? Array.Empty<CellphoneSFlashSaleProgram>())
                {
                    _output.WriteLine($"   {group!.Title} | {p.Title} | {p.StartsAt:dd/MM HH:mm} → {p.EndsAt:HH:mm} | active={p.IsActive}");
                    programs.Add(p);
                }
            }
            var target = programs.Where(p => p.IsActive && p.EndsAt > DateTimeOffset.UtcNow).OrderBy(p => p.StartsAt).FirstOrDefault();
            if (target == null) { _output.WriteLine("Không có khung flash sale sắp/đang chạy."); return; }

            // Đặt "bây giờ" vào giữa khung để chạy trọn luồng (trước giờ mở bán giá flash bị giấu → dùng giá đặc biệt)
            var source = new CellphoneSEventSource(api, new FailingRenderer(), NullLogger<CellphoneSEventSource>.Instance,
                new FixedTime(target.StartsAt.AddMinutes(1)));
            var events = await source.GetLiveEventsAsync();

            var flash = Assert.Single(events, e => e.Url.EndsWith("#flashsale-" + target.Id));
            _output.WriteLine($"{flash.Title} | {flash.Url} | hết hạn {flash.EndsAt} | {flash.Products.Count} sản phẩm");
            foreach (var p in flash.Products) _output.WriteLine($"   {p.Name} | {p.Price:N0} (gốc {p.OriginalPrice:N0}) | {p.Description} | {p.Url}");
            Assert.All(flash.Products, p => Assert.StartsWith("https://cellphones.com.vn/", p.Url));
        }

        private sealed class FixedTime : TimeProvider
        {
            private readonly DateTimeOffset _now;
            public FixedTime(DateTimeOffset now) => _now = now;
            public override DateTimeOffset GetUtcNow() => _now;
        }

        private sealed class FailingRenderer : IPageRenderer
        {
            public Task<string> RenderAsync(string url, CancellationToken cancellationToken = default)
                => Task.FromException<string>(new NotSupportedException("Không render trong test API"));
        }

        private static bool IsHttpUrl(string url) => url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }
}
