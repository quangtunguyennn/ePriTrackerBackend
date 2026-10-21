using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;

namespace ePriTrackerBackend.Repositories
{
    public interface IPriceHistoryRepository
    {
        void Add(PriceHistory priceHistory);
    }

    public class PriceHistoryRepository : IPriceHistoryRepository
    {
        private readonly ePriTrackerContext _context;

        public PriceHistoryRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Add(PriceHistory priceHistory) => _context.PriceHistory.Add(priceHistory);
    }
}
