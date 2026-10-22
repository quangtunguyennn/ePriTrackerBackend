using ePriTrackerBackend.Extensions;
using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Scraping;
using ePriTrackerBackend.Services;
using ePriTrackerBackend.Services.Events;
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
// (HttpClient của từng sàn được đăng ký trong AddScraping() bên dưới)

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
builder.Services.AddScraping();             // Telemetry, các sàn TMĐT (Strategy), bộ chọn sàn – Scraping/ScrapingServiceCollectionExtensions.cs
builder.Services.AddRepositories();         // Truy cập DB – Extensions/ApplicationServiceCollectionExtensions.cs
builder.Services.AddApplicationServices();  // Nghiệp vụ + điểm vào job Hangfire – cùng file trên

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
// Thiết lập múi giờ Việt Nam an toàn đa nền tảng không cần thư viện ngoài
TimeZoneInfo vietnamTz;
try
{
    vietnamTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); // Dành cho Linux / Docker
}
catch (TimeZoneNotFoundException)
{
    vietnamTz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); // Dành cho Windows / IIS
}

// 1. Job tự động crawl giá mỗi 6 tiếng
RecurringJob.AddOrUpdate<IPriceCrawlerService>(
    "update-prices-job",
    service => service.UpdateAllTrackedProductPricesAsync(),
    "0 */6 * * *",
    //"*/2 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);

// 2. Job cập nhật gợi ý mỗi 6 tiếng
RecurringJob.AddOrUpdate<ISuggestionsCrawlerService>(
    "update-suggestions-job",
    service => service.UpdateAllTrackedProductSuggestionsAsync(CancellationToken.None),
    "0 */6 * * *",
    //"*/2 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);

RecurringJob.AddOrUpdate<IEventCacheService>(
    "refresh-event-cache-job",
    service => service.RefreshLiveEventProductsCacheAsync(),
    "*/30 * * * *",
    //"*/2 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);
BackgroundJob.Enqueue<IEventCacheService>(service => service.RefreshLiveEventProductsCacheAsync());

// 4. Sự kiện sàn tự đăng (CellphoneS): đăng sự kiện đang chạy, xoá sự kiện hết hạn – không qua Admin
RecurringJob.AddOrUpdate<IAutoEventSyncService>(
    "sync-auto-events-job",
    service => service.SyncAllAsync(CancellationToken.None),
    "*/15 * * * *",
    new RecurringJobOptions { TimeZone = vietnamTz }
);
BackgroundJob.Enqueue<IAutoEventSyncService>(service => service.SyncAllAsync(CancellationToken.None));

app.MapControllers();

app.Run();