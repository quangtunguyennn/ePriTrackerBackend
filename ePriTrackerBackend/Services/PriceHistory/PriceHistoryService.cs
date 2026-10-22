// Services/PriceHistoryService.cs
using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;

namespace ePriTrackerBackend.Services
{
    public interface IPriceHistoryService
    {
        /// <param name="days">Số ngày gần nhất; 0 = toàn bộ.</param>
        /// <returns>null nếu không có sản phẩm.</returns>
        Task<PriceHistoryDTO> GetAsync(Guid productId, int days, CancellationToken cancellationToken = default);
    }

    public class PriceHistoryService : IPriceHistoryService
    {
        private const int MaxPoints = 500;
        private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

        private readonly IProductRepository _products;
        private readonly IPriceHistoryRepository _history;

        public PriceHistoryService(IProductRepository products, IPriceHistoryRepository history)
        {
            _products = products ?? throw new ArgumentNullException(nameof(products));
            _history = history ?? throw new ArgumentNullException(nameof(history));
        }

        public async Task<PriceHistoryDTO> GetAsync(Guid productId, int days, CancellationToken cancellationToken = default)
        {
            var product = await _products.FindByIdAsync(productId);
            if (product == null) return null;

            DateTimeOffset? from = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days) : null;
            var rows = await _history.GetByProductAsync(productId, from, cancellationToken);

            var points = rows
                .Select(r => new PricePointDTO { CheckedAt = r.CheckedAt, Price = r.Price })
                .ToList();

            if (points.Count > MaxPoints)
            {
                points = points
                    .GroupBy(p => p.CheckedAt.ToOffset(VietnamOffset).Date)
                    .Select(g => g.OrderBy(p => p.CheckedAt).Last())
                    .OrderBy(p => p.CheckedAt)
                    .ToList();
            }

            return new PriceHistoryDTO
            {
                ProductId = product.ProductId,
                ProductName = product.ProductName,
                CurrentPrice = product.LatestPrice ?? points.LastOrDefault()?.Price,
                LowestPrice = points.Count > 0 ? points.Min(p => p.Price) : null,
                HighestPrice = points.Count > 0 ? points.Max(p => p.Price) : null,
                Points = points
            };
        }
    }
}