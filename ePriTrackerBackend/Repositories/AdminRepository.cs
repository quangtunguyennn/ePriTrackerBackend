using ePriTrackerBackend.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Repositories
{
    /// <summary>Một dòng sản phẩm rút gọn (chỉ các cột cần cho sức khoẻ dữ liệu / thống kê) – không tải mô tả HTML.</summary>
    public sealed class AdminProductRow
    {
        public Guid ProductId { get; init; }
        public string ProductName { get; init; }
        public string ProductLink { get; init; }
        public string ImageURL { get; init; }
        public decimal? LatestPrice { get; init; }
        public DateTime? AddedAt { get; init; }
        public DateTimeOffset? LastUpdatedAt { get; init; }
        public DateTimeOffset? LastCheckedAt { get; init; }
        public DateTimeOffset? NextCrawlAt { get; init; }
        public bool IsInStock { get; init; }
        public bool IsDelisted { get; init; }
        public int ActiveTrackersCount { get; init; }
    }

    /// <summary>Truy vấn chỉ-đọc cho trang Admin (giám sát, thống kê).</summary>
    public interface IAdminRepository
    {
        Task<List<AdminProductRow>> GetProductRowsAsync(CancellationToken cancellationToken = default);

        /// <summary>Ngày tạo tài khoản (giờ server = giờ VN; tài khoản cũ có thể null).</summary>
        Task<List<DateTime?>> GetUserCreatedDatesAsync(CancellationToken cancellationToken = default);

        Task<List<DateTimeOffset>> GetNotificationCreatedTimesAsync(CancellationToken cancellationToken = default);

        /// <summary>Số lượt theo dõi (dòng Item).</summary>
        Task<int> CountTrackedItemsAsync(CancellationToken cancellationToken = default);

        /// <summary>Lịch sử giá của các sản phẩm cho trước (lọc thời gian ở tầng trên – SQLite không so sánh được DateTimeOffset).</summary>
        Task<List<AdminPricePoint>> GetPriceHistoryAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default);
    }

    public sealed class AdminPricePoint
    {
        public Guid ProductId { get; init; }
        public decimal Price { get; init; }
        public DateTimeOffset CheckedAt { get; init; }
    }

    public class AdminRepository : IAdminRepository
    {
        private readonly ePriTrackerContext _context;

        public AdminRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<List<AdminProductRow>> GetProductRowsAsync(CancellationToken cancellationToken = default)
            => _context.Product.AsNoTracking()
                .Select(p => new AdminProductRow
                {
                    ProductId = p.ProductId,
                    ProductName = p.ProductName,
                    ProductLink = p.ProductLink,
                    ImageURL = p.ImageURL,
                    LatestPrice = p.LatestPrice,
                    AddedAt = p.AddedAt,
                    LastUpdatedAt = p.LastUpdatedAt,
                    LastCheckedAt = p.LastCheckedAt,
                    NextCrawlAt = p.NextCrawlAt,
                    IsInStock = p.IsInStock,
                    IsDelisted = p.IsDelisted,
                    ActiveTrackersCount = p.ActiveTrackersCount
                })
                .ToListAsync(cancellationToken);

        public Task<List<DateTime?>> GetUserCreatedDatesAsync(CancellationToken cancellationToken = default)
            => _context.User.AsNoTracking().Select(u => u.CreatedAt).ToListAsync(cancellationToken);

        public Task<List<DateTimeOffset>> GetNotificationCreatedTimesAsync(CancellationToken cancellationToken = default)
            => _context.Notification.AsNoTracking().Select(n => n.CreatedAt).ToListAsync(cancellationToken);

        public Task<int> CountTrackedItemsAsync(CancellationToken cancellationToken = default)
            => _context.Item.CountAsync(cancellationToken);

        public Task<List<AdminPricePoint>> GetPriceHistoryAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
        {
            if (productIds == null || productIds.Count == 0) return Task.FromResult(new List<AdminPricePoint>());
            var ids = productIds.ToList();
            return _context.PriceHistory.AsNoTracking()
                .Where(h => ids.Contains(h.ProductId))
                .Select(h => new AdminPricePoint { ProductId = h.ProductId, Price = h.Price, CheckedAt = h.CheckedAt })
                .ToListAsync(cancellationToken);
        }
    }
}
