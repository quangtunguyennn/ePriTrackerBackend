using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Repositories
{
    public class ProductRepository : IProductRepository
    {
        private readonly ePriTrackerContext _context;

        public ProductRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<Product> FindByIdAsync(Guid productId)
            => _context.Product.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId);

        public Task<Product> FindByLinkAsync(string productLink)
            => _context.Product.FirstOrDefaultAsync(p => p.ProductLink == productLink);

        public void Add(Product product) => _context.Product.Add(product);

        // BỔ SUNG PHƯƠNG THỨC NÀY
        public void Update(Product product) => _context.Product.Update(product);

        public async Task<List<Product>> GetTrackedByUserAsync(Guid userId)
        {
            var trackedProductIds = await _context.Item
                .AsNoTracking()
                .Where(i => i.UserId == userId)
                .Select(i => i.ProductId)
                .ToListAsync();

            if (!trackedProductIds.Any()) return new List<Product>();

            return await _context.Product
                .AsNoTracking()
                .Where(p => trackedProductIds.Contains(p.ProductId))
                .OrderByDescending(p => p.AddedAt)
                .ToListAsync();
        }

        public Task<List<Product>> GetAllTrackedAsync(bool asTracking, CancellationToken cancellationToken = default)
        {
            var query = asTracking ? _context.Product : _context.Product.AsNoTracking();
            return query
                .Where(p => _context.Item.Any(i => i.ProductId == p.ProductId))
                .ToListAsync(cancellationToken);
        }
    }
}