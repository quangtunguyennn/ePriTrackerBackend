using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Repositories
{
    /// <summary>
    /// Bảng Item: quan hệ "user đang theo dõi sản phẩm".
    /// </summary>
    public interface ITrackedItemRepository
    {
        Task<bool> ExistsAsync(Guid userId, Guid productId);
        Task<Item> FindAsync(Guid userId, Guid productId);
        void Add(Item item);
        void Remove(Item item);

        /// <summary>Danh sách theo dõi của user, kèm sản phẩm, sản phẩm thêm mới nhất trước.</summary>
        Task<List<Item>> GetByUserWithProductsAsync(Guid userId);

        /// <summary>Sản phẩm vừa có giá lần đầu (thêm lúc hết hàng) → điền giá ban đầu cho các lượt theo dõi còn trống.</summary>
        Task<int> FillMissingInitialPriceAsync(Guid productId, decimal price);
    }

    public class TrackedItemRepository : ITrackedItemRepository
    {
        private readonly ePriTrackerContext _context;

        public TrackedItemRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<bool> ExistsAsync(Guid userId, Guid productId)
            => _context.Item.AnyAsync(i => i.UserId == userId && i.ProductId == productId);

        public Task<Item> FindAsync(Guid userId, Guid productId)
            => _context.Item.FirstOrDefaultAsync(i => i.ProductId == productId && i.UserId == userId);

        public void Add(Item item) => _context.Item.Add(item);

        public void Remove(Item item) => _context.Item.Remove(item);

        public Task<List<Item>> GetByUserWithProductsAsync(Guid userId)
            => _context.Item
                .AsNoTracking()
                .Include(i => i.Product)
                .Where(i => i.UserId == userId)
                .OrderByDescending(i => i.Product.AddedAt)
                .ToListAsync();

        public Task<int> FillMissingInitialPriceAsync(Guid productId, decimal price)
            => _context.Item
                .Where(i => i.ProductId == productId && i.InitialPrice == null) // không bao giờ lưu giá 0 → chỉ cần xét null
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.InitialPrice, (decimal?)price));
    }
}
