using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.CellphoneS;
using ePriTrackerBackend.Scraping.Core;
using ePriTrackerBackend.Scraping.Tiki;
using ePriTrackerBackend.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping
{
    public static class ScrapingServiceCollectionExtensions
    {
        /// <summary>
        /// Đăng ký toàn bộ hạ tầng scraping: telemetry, các sàn TMĐT (strategy) và bộ chọn sàn.
        /// </summary>
        public static IServiceCollection AddScraping(this IServiceCollection services)
        {
            // Telemetry: một instance duy nhất, cấp ra qua 2 interface (ghi / đọc)
            services.AddSingleton<ScraperMetricsService>();
            services.AddSingleton<IScraperMetrics>(sp => sp.GetRequiredService<ScraperMetricsService>());
            services.AddSingleton<IScraperMetricsReader>(sp => sp.GetRequiredService<ScraperMetricsService>());

            services.AddTikiScraper();
            services.AddCellphoneSScraper();
            // services.AddXxxScraper(); ← thêm sàn mới tại đây

            // Bộ chọn sàn nhận tất cả IPlatformScraper đã đăng ký ở trên
            services.AddSingleton<IPlatformScraperResolver, PlatformScraperResolver>();

            return services;
        }

        private static IServiceCollection AddTikiScraper(this IServiceCollection services)
        {
            // HttpClient cho Tiki qua IHttpClientFactory (kèm giới hạn tốc độ theo cấu hình, mặc định không giới hạn)
            services.AddPlatformHttpClient(TikiScraper.PlatformName, TikiHttpFetchStrategy.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

            services.AddSingleton<ITikiBrowserService, TikiBrowserService>();
            services.AddSingleton<TikiHttpFetchStrategy>();
            services.AddSingleton<TikiPlaywrightFetchStrategy>();

            // Hybrid Scraping của Tiki – thứ tự mảng = thứ tự fallback: HttpClient (nhanh) → Playwright (tàng hình)
            services.AddKeyedSingleton<IHybridFetcher>(TikiScraper.PlatformName, (sp, _) => new HybridFetcher(
                new IFetchStrategy[]
                {
                    sp.GetRequiredService<TikiHttpFetchStrategy>(),
                    sp.GetRequiredService<TikiPlaywrightFetchStrategy>()
                },
                sp.GetRequiredService<IScraperMetrics>(),
                sp.GetRequiredService<ILogger<HybridFetcher>>()));

            // Một instance TikiScraper phục vụ cả 2 vai trò: strategy sản phẩm và nguồn sự kiện
            services.AddSingleton<TikiScraper>();
            services.AddSingleton<IPlatformScraper>(sp => sp.GetRequiredService<TikiScraper>());
            services.AddSingleton<IEventScraper>(sp => sp.GetRequiredService<TikiScraper>());

            return services;
        }

        private static IServiceCollection AddCellphoneSScraper(this IServiceCollection services)
        {
            // Trang HTML có JSON-LD – không cần Playwright. Tốc độ theo "Scraping:Platforms:CellphoneS:MinIntervalMs".
            services.AddPlatformHttpClient(CellphoneSScraper.PlatformName, CellphoneSScraper.PlatformName, client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");
                client.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9");
            });

            services.AddKeyedSingleton<IHybridFetcher>(CellphoneSScraper.PlatformName, (sp, _) => new HybridFetcher(
                new IFetchStrategy[]
                {
                    new HtmlJsonLdFetchStrategy(sp.GetRequiredService<IHttpClientFactory>(), CellphoneSScraper.PlatformName,
                        new Dictionary<string, string> { [CellphoneSScraper.DescriptionHtmlField] = CellphoneSScraper.DescriptionXPath })
                },
                sp.GetRequiredService<IScraperMetrics>(),
                sp.GetRequiredService<ILogger<HybridFetcher>>()));

            services.AddSingleton<CellphoneSScraper>();
            services.AddSingleton<IPlatformScraper>(sp => sp.GetRequiredService<CellphoneSScraper>());

            // Sự kiện tự đăng từ trang chủ (cần trình duyệt vì dữ liệu nạp bằng JavaScript)
            services.TryAddSingleton<IPageRenderer, PlaywrightPageRenderer>();
            services.AddSingleton<IAutoEventSource, CellphoneSEventSource>();

            return services;
        }

        /// <summary>
        /// Named HttpClient của một sàn + giới hạn tốc độ đọc từ "Scraping:Platforms:{platform}:MinIntervalMs"
        /// (thiếu cấu hình → 0 = không giới hạn). Bộ đếm thời gian là singleton theo sàn nên dùng chung giữa mọi request.
        /// </summary>
        private static IHttpClientBuilder AddPlatformHttpClient(this IServiceCollection services, string platform, string httpClientName, Action<HttpClient> configure)
        {
            services.AddKeyedSingleton(platform, (sp, _) =>
            {
                int ms = sp.GetService<IConfiguration>()?.GetValue<int?>($"Scraping:Platforms:{platform}:MinIntervalMs") ?? 0;
                return new MinIntervalGate(TimeSpan.FromMilliseconds(Math.Max(0, ms)));
            });

            return services.AddHttpClient(httpClientName, configure)
                .AddHttpMessageHandler(sp => new RateLimitingHandler(sp.GetRequiredKeyedService<MinIntervalGate>(platform)));
        }
    }
}
