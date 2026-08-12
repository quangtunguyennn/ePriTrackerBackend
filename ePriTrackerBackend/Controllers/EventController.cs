using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims; // TÔI ĐÃ THÊM DÒNG NÀY ĐỂ ĐỌC TOKEN

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly IEventRepository _eventRepository;
        private readonly ePriTrackerContext _context;

        public EventController(IEventRepository eventRepository, ePriTrackerContext context)
        {
            _eventRepository = eventRepository;
            _context = context;
        }

        [HttpGet("getCurrentEvents")]
        [Authorize(Roles = "User, Admin")]
        public async Task<IActionResult> GetCurrentEvents()
        {
            try
            {
                var events = await _eventRepository.GetCurrentTikiEvents();
                if (events == null || events.Count == 0)
                    return NotFound(new { message = "Không tìm thấy sự kiện nào hoặc API Tiki đã thay đổi." });
                return Ok(events);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("getPublishedEvents")]
        [Authorize(Roles = "User, Admin")]
        public async Task<IActionResult> GetPublishedEvents()
        {
            var events = await _context.Event.OrderByDescending(e => e.CreatedAt).ToListAsync();
            return Ok(events);
        }

        [HttpGet("getUserProductsInEvent/{eventId}")]
        [Authorize(Roles = "User, Admin")]
        public async Task<IActionResult> GetUserProductsInEvent(Guid eventId)
        {
            // --- ĐOẠN ĐƯỢC SỬA: LẤY EMAIL TỪ TOKEN ---
            var userEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue(ClaimTypes.Name);

            if (string.IsNullOrEmpty(userEmail))
                return Unauthorized("Token không hợp lệ hoặc không chứa thông tin User.");
            // ------------------------------------------

            var user = await _context.User.FirstOrDefaultAsync(u => u.Email == userEmail);
            if (user == null) return Unauthorized("Không tìm thấy user");

            var trackedProductIds = await _context.Item
                .Where(i => i.UserId == user.UserId)
                .Select(i => i.ProductId)
                .ToListAsync();

            var productsInEvent = await _context.EventProduct
                .Where(ep => ep.EventId == eventId && trackedProductIds.Contains(ep.ProductId))
                .Include(ep => ep.Product)
                .Select(ep => ep.Product)
                .ToListAsync();

            return Ok(productsInEvent);
        }

        [HttpPost("crawlAndSave")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CrawlAndSaveEvents()
        {
            try
            {
                // 1. Gọi hàm Crawl sự kiện từ Tiki (hàm mà chúng ta đã viết hôm trước)
                var crawledEvents = await _eventRepository.GetCurrentTikiEvents();
                int addedCount = 0;

                foreach (var evt in crawledEvents)
                {
                    // 2. Kiểm tra xem sự kiện này đã lưu trong DB chưa (Dựa vào TikiEventId)
                    bool exists = await _context.Event.AnyAsync(e => e.TikiEventId == evt.Id);

                    if (!exists)
                    {
                        var newEvent = new Event
                        {
                            TikiEventId = evt.Id,
                            Title = evt.Title,
                            ImageUrl = evt.ImageUrl,
                            EventLink = evt.EventLink,
                            Content = evt.Content,
                            GroupZone = evt.GroupZone,
                            CreatedAt = DateTime.UtcNow
                        };
                        _context.Event.Add(newEvent);
                        addedCount++;
                    }
                }

                // 3. Lưu xuống DB nếu có sự kiện mới
                if (addedCount > 0)
                {
                    await _context.SaveChangesAsync();
                }

                return Ok(new { message = $"Crawl hoàn tất. Đã thêm {addedCount} sự kiện mới vào hệ thống." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Lỗi khi Crawl và Lưu: {ex.Message}" });
            }
        }
    }
}