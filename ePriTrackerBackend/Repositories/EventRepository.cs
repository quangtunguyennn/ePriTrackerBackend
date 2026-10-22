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
        /// <summary>Mọi sự kiện của một sàn (có theo dõi thay đổi để cập nhật/xoá).</summary>
        Task<List<Event>> GetByPlatformAsync(string platform);
        Task<List<Event>> GetByPlatformNewestFirstAsync(string platform);

        /// <summary>Sự kiện đang bật hiển thị của mọi sàn, mới nhất trước. Lọc hết hạn (EndsAt) ở tầng service.</summary>
        Task<List<Event>> GetPublishedNewestFirstAsync();
        Task<List<Event>> GetCreatedBeforeAsync(DateTime createdBeforeUtc, string platform);
        Task<Event> FindByIdAsync(Guid eventId);
        Task<Event> FindByTitleOrLinkAsync(string title, string eventLink, string platform);

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

        public Task<List<Event>> GetByPlatformAsync(string platform)
            => _context.Event.Where(e => e.Platform == platform).ToListAsync();

        public Task<List<Event>> GetByPlatformNewestFirstAsync(string platform)
            => _context.Event.Where(e => e.Platform == platform).OrderByDescending(e => e.CreatedAt).ToListAsync();

        public Task<List<Event>> GetPublishedNewestFirstAsync()
            => _context.Event.Where(e => e.IsPublished == true).OrderByDescending(e => e.CreatedAt).ToListAsync();

        public Task<List<Event>> GetCreatedBeforeAsync(DateTime createdBeforeUtc, string platform)
            => _context.Event.Where(e => e.Platform == platform && e.CreatedAt < createdBeforeUtc).ToListAsync();

        public async Task<Event> FindByIdAsync(Guid eventId) => await _context.Event.FindAsync(eventId);

        public Task<Event> FindByTitleOrLinkAsync(string title, string eventLink, string platform)
            => _context.Event.FirstOrDefaultAsync(e => e.Platform == platform && (e.Title == title || e.EventLink == eventLink));

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
