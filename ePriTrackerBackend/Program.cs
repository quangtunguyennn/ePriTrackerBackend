using ePriTrackerBackend.Extensions;
using ePriTrackerBackend.Hubs;
using ePriTrackerBackend.Localization;
using ePriTrackerBackend.Services.Notification;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping;
using ePriTrackerBackend.Services;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Services.PriceCronJob;
using ePriTrackerBackend.Services.YouTube;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. CẤU HÌNH DATABASE
// ==========================================
builder.Services.AddDbContext<ePriTrackerContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DBConnection")));

// ==========================================
// 2. CẤU HÌNH AUTHENTICATION (JWT) & CORS
// ==========================================
// Khoá JWT lấy từ User Secrets (máy dev) / biến môi trường (server) – thiếu thì dừng ngay với hướng dẫn, thay vì lỗi khó hiểu lúc đăng nhập.
// Máy dev chưa có khoá → tự sinh và lưu vào User Secrets (không cần bước cài đặt nào – xem SECRETS.md)
SecretsConfiguration.EnsureDevelopmentJwtKey(builder.Configuration, builder.Environment, SecretsConfiguration.UserSecretsPath<Program>());
var jwtSigningKey = SecretsConfiguration.GetJwtSigningKey(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["JwtSettings:Issuer"],
        ValidAudience = builder.Configuration["JwtSettings:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(jwtSigningKey)
    };

    // SignalR (WebSocket) không gửi được header Authorization → trình duyệt gửi JWT qua query "access_token", chỉ nhận ở đường dẫn hub
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments(NotificationHub.Path))
                context.Token = accessToken;
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// ==========================================
// 3. ĐĂNG KÝ REPOSITORIES & SERVICES (DI)
// ==========================================
builder.Services.AddScraping();             // Telemetry, các sàn TMĐT (Strategy), bộ chọn sàn
builder.Services.AddRepositories();         // Truy cập DB
builder.Services.AddApplicationServices();  // Nghiệp vụ, thông báo + điểm vào job Hangfire
// Đa ngôn ngữ: câu "message" trả cho FE dịch theo Accept-Language (en mặc định, vi) – bản dịch ở Resources/SharedMessages.vi.resx
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddControllers(options => options.Filters.Add<LocalizeMessageFilter>());

// Realtime cho chuông thông báo: hub SignalR + bộ đẩy tín hiệu (NotificationService gọi sau khi lưu thông báo)
builder.Services.AddSignalR();
builder.Services.AddSingleton<INotificationPublisher, SignalRNotificationPublisher>();

// ==========================================
// 4. CẤU HÌNH HANGFIRE
// ==========================================
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DBConnection"), new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.Zero,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));
builder.Services.AddMemoryCache();
builder.Services.AddHangfireServer();

// ==========================================
// 5. CẤU HÌNH SWAGGER
// ==========================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "ePriTracker API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token directly below (do not type 'Bearer ')."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ==========================================
// 6. PIPELINE & MIDDLEWARE
// ==========================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");

// Ngôn ngữ câu chữ (UICulture) của request lấy từ header Accept-Language (FE gửi theo nút EN | VI); lạ/thiếu → tiếng Anh.
// CHỈ đổi UICulture: Culture (định dạng số/ngày dùng khi cào, parse giá) giữ nguyên như trước, không phụ thuộc người dùng.
var serverCulture = System.Globalization.CultureInfo.CurrentCulture;
var uiCultures = new[] { new System.Globalization.CultureInfo("en"), new System.Globalization.CultureInfo("vi") };
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(serverCulture, uiCultures[0]),
    SupportedCultures = new[] { serverCulture },
    SupportedUICultures = uiCultures
});

app.UseAuthentication();
app.UseAuthorization();

// Bật Dashboard Hangfire (URL: https://localhost:<port>/hangfire)
app.UseHangfireDashboard();

// ==========================================
// 7. CẤU HÌNH TIMEZONE & ĐĂNG KÝ HANGFIRE JOBS
// ==========================================
// Lấy múi giờ Việt Nam an toàn cho cả Windows và Linux/Docker
TimeZoneInfo vietnamTz;
try
{
    vietnamTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
}
catch (TimeZoneNotFoundException)
{
    vietnamTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
}

// Chuyển đổi thời gian UTC hiện tại sang giờ Việt Nam
var utcNow = DateTimeOffset.UtcNow;
var startTime = TimeZoneInfo.ConvertTime(utcNow, vietnamTz);

// 1. Job cào giá Adaptive theo Tier (Kích hoạt quét mẻ sản phẩm đến hạn mỗi 1 phút)
RecurringJob.AddOrUpdate<IPriceCrawlerWorker>(
    "adaptive-price-crawler-job",
    worker => worker.ProcessPendingProductsAsync(CancellationToken.None),
    Cron.Minutely(),
    new RecurringJobOptions { TimeZone = vietnamTz }
);

// 2. Job cũ đã bỏ – xoá khỏi DB Hangfire của máy nào còn (lớp đã xoá → "Could not load type" mỗi lần tới lịch):
//    giá: thay bằng job theo Tier ở trên; gợi ý: cào khi người dùng mở trang chi tiết (SuggestionService), không chạy ngầm
RecurringJob.RemoveIfExists("update-prices-job");
RecurringJob.RemoveIfExists("update-suggestions-job");

// 3. Cache Sự kiện
RecurringJob.AddOrUpdate<IEventCacheService>(
    "refresh-event-cache-job",
    service => service.RefreshLiveEventProductsCacheAsync(),
    "*/30 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);
BackgroundJob.Enqueue<IEventCacheService>(service => service.RefreshLiveEventProductsCacheAsync());

// 4. Đồng bộ sự kiện tự động
RecurringJob.AddOrUpdate<IAutoEventSyncService>(
    "sync-auto-events-job",
    service => service.SyncAllAsync(CancellationToken.None),
    "*/15 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);
BackgroundJob.Enqueue<IAutoEventSyncService>(service => service.SyncAllAsync(CancellationToken.None));

app.MapControllers();
app.MapHub<NotificationHub>(NotificationHub.Path);

app.Run();