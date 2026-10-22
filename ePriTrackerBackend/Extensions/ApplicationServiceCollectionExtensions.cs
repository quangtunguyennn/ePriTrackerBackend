using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Services;
using ePriTrackerBackend.Services.Auth;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Services.Products;
using ePriTrackerBackend.Services.Suggestions;

namespace ePriTrackerBackend.Extensions
{
    public static class ApplicationServiceCollectionExtensions
    {
        /// <summary>
        /// Repositories: chỉ truy cập DB, dùng chung DbContext (scoped) → <see cref="IUnitOfWork"/> lưu một lần.
        /// </summary>
        public static IServiceCollection AddRepositories(this IServiceCollection services)
        {
            services.AddScoped<IUnitOfWork, EfUnitOfWork>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<IProductRepository, ProductRepository>();
            services.AddScoped<ITrackedItemRepository, TrackedItemRepository>();
            services.AddScoped<ISuggestionRepository, SuggestionRepository>();
            services.AddScoped<IEventRepository, EventRepository>();
            services.AddScoped<IPriceHistoryRepository, PriceHistoryRepository>();
            return services;
        }

        /// <summary>
        /// Services nghiệp vụ và điểm vào của các job Hangfire.
        /// </summary>
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            // Auth
            services.AddSingleton<IPasswordService, PasswordService>();
            services.AddSingleton<IJwtTokenService, JwtTokenService>();
            services.AddScoped<IAuthService, AuthService>();

            // Sản phẩm & gợi ý
            services.AddScoped<IProductTrackingService, ProductTrackingService>();
            services.AddSingleton<ISuggestionMatcher, SuggestionMatcher>(); // Thuật toán gợi ý, không có trạng thái
            services.AddSingleton<ISuggestionFinder, SuggestionFinder>();   // Tìm + lọc gợi ý, dùng chung lazy-load và job
            services.AddScoped<ISuggestionService, SuggestionService>();

            // Sự kiện
            services.AddScoped<IEventService, EventService>();
            services.AddScoped<IAutoEventSyncService, AutoEventSyncService>(); // sàn tự đăng (CellphoneS) – cũng là job "sync-auto-events-job"

            // Job Hangfire (giữ nguyên tên interface – Hangfire lưu tên kiểu dưới DB)
            services.AddScoped<IPriceCrawlerService, PriceCrawlerService>();
            services.AddScoped<ISuggestionsCrawlerService, SuggestionCrawlService>();
            services.AddScoped<IEventCacheService, EventCacheService>();

            return services;
        }
    }
}
