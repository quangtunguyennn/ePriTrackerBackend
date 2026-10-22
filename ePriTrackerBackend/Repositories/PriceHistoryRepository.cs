// Repositories/IPriceHistoryRepository.cs
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Models.Context;
using Microsoft.EntityFrameworkCore;
using System;

namespace ePriTrackerBackend.Repositories
{
    public interface IPriceHistoryRepository
    {
        /// <summary>Thêm m?t ?i?m giá. Ch?a l?u: UnitOfWork.SaveChangesAsync s? l?u cùng các thay ??i khác.</summary>
        void Add(PriceHistory history);

        /// <summary>L?ch s? giá c?a m?t s?n ph?m, c? ? m?i. from = null: l?y toàn b?.</summary>
        Task<List<PriceHistory>> GetByProductAsync(Guid productId, DateTimeOffset? from, CancellationToken cancellationToken = default);
    }

    public class PriceHistoryRepository : IPriceHistoryRepository
    {
        private readonly ePriTrackerContext _db; // ??i theo tên DbContext c?a b?n

        public PriceHistoryRepository(ePriTrackerContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        public void Add(PriceHistory history) => _db.PriceHistory.Add(history);

        public Task<List<PriceHistory>> GetByProductAsync(Guid productId, DateTimeOffset? from, CancellationToken cancellationToken = default)
            => _db.PriceHistory
                .AsNoTracking()
                .Where(h => h.ProductId == productId && (from == null || h.CheckedAt >= from))
                .OrderBy(h => h.CheckedAt)
                .ToListAsync(cancellationToken);
    }
}