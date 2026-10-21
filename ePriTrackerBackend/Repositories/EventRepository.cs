using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ePriTrackerBackend.Repositories
{
    /// <summary>
    /// Truy cập bảng Event và EventProduct. (EventRepository cũ thực chất là scraper – đã chuyển sang TikiScraper.)
    /// </summary>
    public interface IEventRepository
    {
        Task<List<Event>> GetAllAsync();
        Task<List<Event>> GetAllNewestFirstAsync();
        Task<List<Event>> GetPublishedNewestFirstAsync();
        Task<List<Event>> GetCreatedBeforeAsync(DateTime createdBeforeUtc);
        Task<Event> FindByIdAsync(Guid eventId);
        Task<Event> FindByTitleOrLinkAsync(string title, string eventLink);

        /// <summary>Sự kiện đầu tiên có link chứa mã chiến dịch (không phân biệt hoa thường).</summary>
        Task<Event> FindByLinkContainingAsync(string campaignCode);

        void Add(Event evt);
        void Remove(Event evt);
        void RemoveRange(IEnumerable<Event> events);

        Task<bool> HasProductAsync(Guid eventId, Guid productId);
        void AddProduct(EventProduct eventProduct);
    }

    public class EventRepository : IEventRepository
    {
        private readonly ePriTrackerContext _context;

        public EventRepository(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<List<Event>> GetAllAsync() => _context.Event.ToListAsync();

        public Task<List<Event>> GetAllNewestFirstAsync()
            => _context.Event.OrderByDescending(e => e.CreatedAt).ToListAsync();

        public Task<List<Event>> GetPublishedNewestFirstAsync()
            => _context.Event.Where(e => e.IsPublished == true).OrderByDescending(e => e.CreatedAt).ToListAsync();

        public Task<List<Event>> GetCreatedBeforeAsync(DateTime createdBeforeUtc)
            => _context.Event.Where(e => e.CreatedAt < createdBeforeUtc).ToListAsync();

        public async Task<Event> FindByIdAsync(Guid eventId) => await _context.Event.FindAsync(eventId);

        public Task<Event> FindByTitleOrLinkAsync(string title, string eventLink)
            => _context.Event.FirstOrDefaultAsync(e => e.Title == title || e.EventLink == eventLink);

        public Task<Event> FindByLinkContainingAsync(string campaignCode)
            => _context.Event.FirstOrDefaultAsync(e => e.EventLink.ToLower().Contains(campaignCode));

        public void Add(Event evt) => _context.Event.Add(evt);

        public void Remove(Event evt) => _context.Event.Remove(evt);

        public void RemoveRange(IEnumerable<Event> events) => _context.Event.RemoveRange(events);

        public Task<bool> HasProductAsync(Guid eventId, Guid productId)
            => _context.EventProduct.AnyAsync(ep => ep.EventId == eventId && ep.ProductId == productId);

        public void AddProduct(EventProduct eventProduct) => _context.EventProduct.Add(eventProduct);
    }
}
