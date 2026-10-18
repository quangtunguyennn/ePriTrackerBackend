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
    }
}
