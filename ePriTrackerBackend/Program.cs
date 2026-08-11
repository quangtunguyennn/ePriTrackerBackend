using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Services;
using Hangfire;
using Hangfire.SqlServer; // Thêm namespace này cho Hangfire SQL
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. C?U HÌNH DATABASE & HTTP CLIENT
// ==========================================

// Config DbContext
builder.Services.AddDbContext<ePriTrackerContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DBConnection")));

// B?T BU?C: ??ng ký HttpClient Factory cho PriceCrawlerService s? d?ng
builder.Services.AddHttpClient();

// ==========================================
// 2. C?U HÌNH AUTHENTICATION (JWT) & CORS
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
// 3. ??NG KÝ REPOSITORIES & SERVICES (DI)
// ==========================================
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IPriceCrawlerService, PriceCrawlerService>();

builder.Services.AddControllers();

// ==========================================
// 4. C?U HÌNH HANGFIRE
// ==========================================
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    // ?ã ??i sang "DBConnection" cho ??ng b? v?i DbContext
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DBConnection"), new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.Zero,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));

builder.Services.AddHangfireServer();

// ==========================================
// 5. C?U HÌNH SWAGGER
// ==========================================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // ?ã ??i Title thành ePriTracker API
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

// B?t Dashboard Hangfire (URL: https://localhost:<port>/hangfire)
app.UseHangfireDashboard();

// ??ng ký Job t? ??ng crawl giá m?i 6 ti?ng
RecurringJob.AddOrUpdate<IPriceCrawlerService>(
    "update-prices-job",
    service => service.UpdateAllTrackedProductPricesAsync(),
    "0 */6 * * *"
);

app.MapControllers();

app.Run();