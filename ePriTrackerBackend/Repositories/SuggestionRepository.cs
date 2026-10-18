using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Repositories
{
    public interface ISuggestionRepository
    {
        /// <param name="asTracking">true khi sẽ xoá các bản ghi này.</param>
        Task<List<SuggestionProduct>> GetByProductIdAsync(Guid productId, bool asTracking = false);
        void AddRange(IEnumerable<SuggestionProduct> suggestions);
        void RemoveRange(IEnumerable<SuggestionProduct> suggestions);

        /// <summary>
        /// Xoá toàn bộ gợi ý cũ của các sản phẩm rồi ghi gợi ý mới, trong MỘT transaction riêng
        /// (tự lưu, không chờ IUnitOfWork). Lỗi thì rollback và ném lại.
        /// </summary>
        Task ReplaceForProductsAsync(IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<SuggestionProduct> newSuggestions, CancellationToken cancellationToken = default);
    }

    public class SuggestionRepository : ISuggestionRepository
    {
        private readonly ePriTrackerContext _context;

        public SuggestionRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<List<SuggestionProduct>> GetByProductIdAsync(Guid productId, bool asTracking = false)
        {
            var query = asTracking ? _context.SuggestionProduct : _context.SuggestionProduct.AsNoTracking();
            return query.Where(s => s.ProductId == productId).ToListAsync();
        }

        public void AddRange(IEnumerable<SuggestionProduct> suggestions) => _context.SuggestionProduct.AddRange(suggestions);

        public void RemoveRange(IEnumerable<SuggestionProduct> suggestions) => _context.SuggestionProduct.RemoveRange(suggestions);

        public async Task ReplaceForProductsAsync(IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<SuggestionProduct> newSuggestions, CancellationToken cancellationToken = default)
        {
            // Transaction cục bộ cho lô này (Giải phóng Lock nhanh)
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // Xóa hàng loạt trực tiếp dưới DB bằng EF Core 7+ ExecuteDeleteAsync
                await _context.SuggestionProduct
                    .Where(s => productIds.Contains(s.ProductId))
                    .ExecuteDeleteAsync(cancellationToken);

                if (newSuggestions.Any())
                {
                    await _context.SuggestionProduct.AddRangeAsync(newSuggestions, cancellationToken);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }
}
