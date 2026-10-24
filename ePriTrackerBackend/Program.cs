using ePriTrackerBackend.Extensions;
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
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:SecretKey"]!))
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
builder.Services.AddApplicationServices();  // Nghiệp vụ + điểm vào job Hangfire

// Services cho luồng cào giá Adaptive & Thông báo
builder.Services.AddScoped<ePriTrackerBackend.Services.Notification.INotificationService, ePriTrackerBackend.Services.Notification.NotificationService>();
builder.Services.AddScoped<IPriceCrawlerWorker, PriceCrawlerWorker>();
builder.Services.AddTransient<ePriTrackerBackend.Services.Notification.IEmailService, ePriTrackerBackend.Services.Notification.EmailService>();
builder.Services.AddControllers();

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

app.Run();