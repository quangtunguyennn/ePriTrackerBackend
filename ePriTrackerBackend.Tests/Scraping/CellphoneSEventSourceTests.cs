using ePriTrackerBackend.Scraping.CellphoneS;
using ePriTrackerBackend.Scraping.Core;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class CellphoneSEventSourceTests
    {
        private static readonly TimeSpan Vn = TimeSpan.FromHours(7);
        private static readonly DateTimeOffset During = new(2026, 10, 24, 20, 30, 0, Vn); // trong khung 20-22h 24/10

        private sealed class FixedTime : TimeProvider
        {
            public DateTimeOffset Now { get; set; }
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class FakeRenderer : IPageRenderer
        {
            public string Html { get; set; } = HomeHtml(withFlash: false);
            public Exception? Failure { get; set; }
            public int Calls { get; private set; }

            public Task<string> RenderAsync(string url, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Failure != null ? Task.FromException<string>(Failure) : Task.FromResult(Html);
            }

            public Task<string> RenderAsync(string url, PageRenderOptions options, CancellationToken cancellationToken = default)
                => RenderAsync(url, cancellationToken);
        }

        private static string Card(string slug, string price, string original, string extra = "") =>
            $"<a href=\"/{slug}.html\"><img alt=\"{slug}\" src=\"https://cdn/{slug}.png\"><h3>{slug}</h3><div><div>{price}</div><del>{original}</del></div>{extra}</a>";

        /// <summary>Trang chủ rút gọn: luôn có "DEAL SỐC MỖI NGÀY"; tuỳ chọn có khối flash sale khung 20-22h 24/10.</summary>
        private static string HomeHtml(bool withFlash)
        {
            string deal = "<div><div><span>DEAL SỐC<br> MỖI NGÀY</span></div><div>" +
                string.Concat(Enumerable.Range(1, 4).Select(i => Card($"deal-{i}", "690.000đ", "890.000đ"))) + "</div></div>";
            string flash = !withFlash ? "" :
                "<div><div><div>FLASHSALE TỰU TRƯỜNG</div></div><div><div>20-22h 24/10</div></div><div>" +
                string.Concat(Enumerable.Range(1, 4).Select(i => Card($"flash-{i}", "1.000.000đ", "2.000.000đ", "<div>Đã bán 1/10 suất</div>"))) + "</div></div>";
            return $"<html><body><main>{flash}{deal}</main></body></html>";
        }

        private static FakeCellphoneSApi ApiWithFlashSale()
        {
            var api = new FakeCellphoneSApi();
            api.HomeGroupUds.Add("flashsale_cuoituan1_t7");
            api.Groups["flashsale_cuoituan1_t7"] = new CellphoneSFlashSaleGroup
            {
                Ud = "flashsale_cuoituan1_t7",
                Title = "Flashsale Giá Sốc 2h",
                Programs = new[]
                {
                    new CellphoneSFlashSaleProgram { Id = 460, Title = "12-14h 24/10", IsActive = true, StartsAt = new(2026, 10, 24, 12, 0, 0, Vn), EndsAt = new(2026, 10, 24, 14, 0, 0, Vn) },
                    new CellphoneSFlashSaleProgram { Id = 461, Title = "20-22h 24/10", IsActive = true, StartsAt = new(2026, 10, 24, 20, 0, 0, Vn), EndsAt = new(2026, 10, 24, 22, 0, 0, Vn) }
                }
            };
            api.Slots[461] = new()
            {
                new CellphoneSFlashSaleSlot { ProductId = 1, Name = "OPPO Find X5 Pro", Thumbnail = "/o/p/oppo.png", IsActive = true, Sold = 3, MaxSlot = 10, NormalPrice = 15990000, SpecialPrice = 12990000, FlashPrice = 11990000 },
                // Giá flash bị giấu (=0) → dùng giá đặc biệt
                new CellphoneSFlashSaleSlot { ProductId = 2, Name = "SoundPEATS C30", IsActive = true, MaxSlot = 10, NormalPrice = 790000, SpecialPrice = 570000 },
                new CellphoneSFlashSaleSlot { ProductId = 3, Name = "Đã tắt", IsActive = false, NormalPrice = 1, SpecialPrice = 1 },
                new CellphoneSFlashSaleSlot { ProductId = 4, Name = "Không tra được link", IsActive = true, NormalPrice = 2, SpecialPrice = 1 }
            };
            api.Catalog[1] = FakeCellphoneSApi.Product(1, "oppo-find-x5-pro", 12990000, 15990000);
            api.Catalog[2] = FakeCellphoneSApi.Product(2, "tai-nghe-nhet-tai-soundpeats-c30", 570000, 790000);
            return api;
        }

        private static CellphoneSEventSource Source(FakeCellphoneSApi api, FakeRenderer renderer, FixedTime time)
            => new(api, renderer, NullLogger<CellphoneSEventSource>.Instance, time);

        [Fact]
        public async Task FlashSaleFromApi_OnlyRunningSlot_DealFromHomePage()
        {
            var renderer = new FakeRenderer();
            var events = await Source(ApiWithFlashSale(), renderer, new FixedTime { Now = During }).GetLiveEventsAsync();

            Assert.Equal(2, events.Count);
            var flash = events[0];
            Assert.Equal("Flashsale Giá Sốc 2h (20-22h 24/10)", flash.Title);
            Assert.Equal("https://cellphones.com.vn/#flashsale-461", flash.Url);
            Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, Vn), flash.EndsAt);
            Assert.Equal(2, flash.Products.Count); // slot tắt và slot không có link bị bỏ

            var oppo = flash.Products[0];
            Assert.Equal(11990000m, oppo.Price);
            Assert.Equal(15990000m, oppo.OriginalPrice);
            Assert.Equal("Đã bán 3/10 suất", oppo.Description);
            Assert.Equal("https://cellphones.com.vn/oppo-find-x5-pro.html", oppo.Url);
            Assert.Equal("https://cdn2.cellphones.com.vn/358x/media/catalog/product/o/p/oppo.png", oppo.ImageUrl);
            Assert.Equal(570000m, flash.Products[1].Price); // giá flash giấu → giá đặc biệt

            Assert.Equal("DEAL SỐC MỖI NGÀY", events[1].Title);
            Assert.Null(events[1].EndsAt);
            Assert.Equal(1, renderer.Calls);
        }

        [Fact]
        public async Task HomePageRenderedAtMostEveryTwoHours_DealReusedInBetween()
        {
            var renderer = new FakeRenderer();
            var time = new FixedTime { Now = During };
            var source = Source(ApiWithFlashSale(), renderer, time);

            await source.GetLiveEventsAsync();
            time.Now = During.AddMinutes(15);
            var second = await source.GetLiveEventsAsync();
            time.Now = During + CellphoneSEventSource.HomeRenderInterval;
            await source.GetLiveEventsAsync();

            Assert.Contains(second, e => e.Title == "DEAL SỐC MỖI NGÀY"); // dùng lại kết quả cũ, không render
            Assert.Equal(2, renderer.Calls);
        }

        [Fact]
        public async Task ApiFails_FallsBackToHomePageFlashSale()
        {
            var api = ApiWithFlashSale();
            api.Failure = new HttpRequestException("API đổi");
            var renderer = new FakeRenderer { Html = HomeHtml(withFlash: true) };

            var events = await Source(api, renderer, new FixedTime { Now = During }).GetLiveEventsAsync();

            Assert.Contains(events, e => e.Title == "FLASHSALE TỰU TRƯỜNG (20-22h 24/10)" && e.Products.Count == 4);
            Assert.Contains(events, e => e.Title == "DEAL SỐC MỖI NGÀY");
        }

        [Fact]
        public async Task ApiOk_HomePageFails_ReturnsFlashSaleAndPreviousDeal()
        {
            var api = ApiWithFlashSale();
            // Khung kéo dài tới sáng hôm sau để vẫn còn chạy khi thời gian tiến thêm 2 giờ
            api.Groups["flashsale_cuoituan1_t7"] = new CellphoneSFlashSaleGroup
            {
                Ud = "flashsale_cuoituan1_t7",
                Title = "Flashsale Giá Sốc 2h",
                Programs = new[] { new CellphoneSFlashSaleProgram { Id = 461, Title = "20h-6h", IsActive = true, StartsAt = new(2026, 10, 24, 20, 0, 0, Vn), EndsAt = new(2026, 10, 25, 6, 0, 0, Vn) } }
            };
            var renderer = new FakeRenderer();
            var time = new FixedTime { Now = During };
            var source = Source(api, renderer, time);
            await source.GetLiveEventsAsync();

            renderer.Failure = new TimeoutException("Playwright timeout");
            time.Now = During + CellphoneSEventSource.HomeRenderInterval;
            var events = await source.GetLiveEventsAsync();

            Assert.Contains(events, e => e.Title.StartsWith("Flashsale"));
            Assert.Contains(events, e => e.Title == "DEAL SỐC MỖI NGÀY");

            // Kết quả cũ quá hạn → thôi dùng (sự kiện cuốn chiếu tự hết hạn ở phía đồng bộ)
            time.Now = During + CellphoneSEventSource.HomeEventsMaxAge;
            var later = await source.GetLiveEventsAsync();
            Assert.DoesNotContain(later, e => e.Title == "DEAL SỐC MỖI NGÀY");
        }

        [Fact]
        public async Task ApiAndHomePageBothFail_Throws_SoNothingIsDeleted()
        {
            var api = ApiWithFlashSale();
            api.Failure = new HttpRequestException("API đổi");
            var renderer = new FakeRenderer { Failure = new TimeoutException("Playwright timeout") };

            await Assert.ThrowsAsync<TimeoutException>(() => Source(api, renderer, new FixedTime { Now = During }).GetLiveEventsAsync());
        }
    }
}
