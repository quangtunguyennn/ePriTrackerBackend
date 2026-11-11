using ePriTrackerBackend.Controllers;
using ePriTrackerBackend.Extensions;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Scraping;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.CellphoneS;
using ePriTrackerBackend.Scraping.Core;
using ePriTrackerBackend.Scraping.Gearvn;
using ePriTrackerBackend.Scraping.Tiki;
using ePriTrackerBackend.Services;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Services.PriceCronJob;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ePriTrackerBackend.Tests
{
    /// <summary>
    /// Dựng DI giống Program.cs (thay SQL Server bằng SQLite) và tạo thử mọi controller + job Hangfire.
    /// Thiếu đăng ký hoặc sai lifetime (scoped trong singleton) sẽ lỗi ngay ở đây thay vì lúc chạy app.
    /// </summary>
    public class AppCompositionTests
    {
        [Fact]
        public async Task AllControllersAndJobs_CanBeConstructed()
        {
            using var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = "test-secret-key-phai-du-dai-it-nhat-32-byte!!",
                ["JwtSettings:Issuer"] = "ePriTracker",
                ["JwtSettings:Audience"] = "ePriTrackerClient",
                ["YouTubeApiKey"] = "test-key" // YouTubeService bắt buộc có key (không gọi API trong test)
            }).Build();

            var services = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddLogging()
                .AddMemoryCache()
                .AddDbContext<ePriTrackerContext>(o => o.UseSqlite(connection))
                .AddScraping()
                .AddRepositories()
                .AddApplicationServices();

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            Assert.NotNull(ActivatorUtilities.CreateInstance<AuthController>(sp));
            Assert.NotNull(ActivatorUtilities.CreateInstance<ProductController>(sp));
            Assert.NotNull(ActivatorUtilities.CreateInstance<EventController>(sp));
            Assert.NotNull(ActivatorUtilities.CreateInstance<MetricsController>(sp));

            Assert.IsType<PriceCrawlerWorker>(sp.GetRequiredService<IPriceCrawlerWorker>());
            Assert.IsType<EventCacheService>(sp.GetRequiredService<IEventCacheService>());
            Assert.IsType<AutoEventSyncService>(sp.GetRequiredService<IAutoEventSyncService>());

            // Sự kiện: Tiki do Admin quản lý, CellphoneS, HACOM và GearVN tự đăng
            Assert.Equal(TikiScraper.PlatformName, sp.GetRequiredService<IEventScraper>().Platform);
            Assert.Equal(new[] { CellphoneSScraper.PlatformName, ePriTrackerBackend.Scraping.Hacom.HacomScraper.PlatformName, GearvnScraper.PlatformName },
                sp.GetServices<IAutoEventSource>().Select(s => s.Platform));
            // Một trình duyệt dùng chung cho render HTML và đọc phản hồi API của trang
            Assert.Same(sp.GetRequiredService<IPageRenderer>(), sp.GetRequiredService<IPageResponseCapture>());

            // Thêm sàn mới không phải sửa service: resolver tự chọn đúng strategy theo link
            var resolver = sp.GetRequiredService<IPlatformScraperResolver>();
            Assert.True(resolver.TryResolve("https://tiki.vn/a-p1.html", out var tiki));
            Assert.IsType<TikiScraper>(tiki);
            Assert.True(resolver.TryResolve("https://cellphones.com.vn/iphone-17-256gb.html", out var cps));
            Assert.IsType<CellphoneSScraper>(cps);

            Assert.True(resolver.TryResolve("https://gearvn.com/products/logitech-g502-hero", out var gearvn));
            Assert.IsType<GearvnScraper>(gearvn);

            // Gợi ý tìm trên Tiki (API công khai), CellphoneS (GraphQL nội bộ), HACOM, GearVN (HTML trang tìm kiếm); CellphoneS có thêm giá theo lô
            Assert.Equal(new[] { TikiScraper.PlatformName, CellphoneSScraper.PlatformName, ePriTrackerBackend.Scraping.Hacom.HacomScraper.PlatformName, GearvnScraper.PlatformName },
                resolver.SearchablePlatforms.Select(p => p.Platform));
            Assert.IsAssignableFrom<IBatchPriceScraper>(cps);
            Assert.IsType<CellphoneSApi>(sp.GetRequiredService<ICellphoneSApi>());
        }

        [Fact]
        public void HangfireJobInterfaces_KeepNamespace()
        {
            // Hangfire lưu tên kiểu dưới DB – đổi tên/namespace sẽ làm job đang chờ bị lỗi "Could not load type"
            Assert.Equal("ePriTrackerBackend.Services.PriceCronJob.IPriceCrawlerWorker", typeof(IPriceCrawlerWorker).FullName);
            Assert.Equal("ePriTrackerBackend.Services.IEventCacheService", typeof(IEventCacheService).FullName);
            Assert.Equal("ePriTrackerBackend.Services.Events.IAutoEventSyncService", typeof(IAutoEventSyncService).FullName);
        }
    }
}
