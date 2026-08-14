using ePriTrackerBackend.Models.Context;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly IEventRepository _eventRepository;
        private readonly IProductRepository _productRepository;
        private readonly ePriTrackerContext _context;

        public EventController(IEventRepository eventRepo, IProductRepository productRepo, ePriTrackerContext context)
        {
            _eventRepository = eventRepo;
            _productRepository = productRepo;
            _context = context;
        }

        // =======================================================
        // PHẦN 1: API DÀNH CHO ADMIN QUẢN LÝ SỰ KIỆN
        // =======================================================

        [HttpGet("crawlPreview")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CrawlPreviewEvents()
        {
            try
            {
                var scrapedEvents = await _eventRepository.GetCurrentTikiEvents();
                if (scrapedEvents == null || scrapedEvents.Count == 0)
                    return NotFound(new { message = "Không tìm thấy sự kiện nào hoặc API Tiki đã thay đổi." });

                var dbEvents = await _context.Event.ToListAsync();

                foreach (var scraped in scrapedEvents)
                {
                    // Dùng Title để đối chiếu thay vì TikiEventId
                    var existingDbEvent = dbEvents.FirstOrDefault(e => e.Title == scraped.Title);

                    if (existingDbEvent != null)
                    {
                        // Lấy đúng trạng thái và EventId từ DB cũ sang để đồng bộ
                        scraped.IsPublished = existingDbEvent.IsPublished;
                        scraped.EventId = existingDbEvent.EventId;
                    }
                    else
                    {
                        scraped.IsPublished = false;
                    }
                }

                return Ok(scrapedEvents);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("postEvent")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> PostEvent([FromBody] Event tikiEvent)
        {
            try
            {
                // Kiểm tra dựa trên Tiêu đề (Title) hoặc Đường dẫn (EventLink) của sự kiện
                var existingEvent = await _context.Event
                    .FirstOrDefaultAsync(e => e.Title == tikiEvent.Title || e.EventLink == tikiEvent.EventLink);

                if (existingEvent == null)
                {
                    // Nếu chưa có, tạo mới hoàn toàn
                    tikiEvent.EventId = Guid.NewGuid();
                    tikiEvent.CreatedAt = DateTime.UtcNow;
                    tikiEvent.IsPublished = true;

                    _context.Event.Add(tikiEvent);
                    await _context.SaveChangesAsync();

                    return Ok(new { message = "Đã đăng sự kiện thành công!" });
                }
                else
                {
                    // Nếu đã tồn tại trong DB, chỉ cần cập nhật lại trạng thái thành Published = true và giữ nguyên ID cũ
                    existingEvent.IsPublished = true;
                    existingEvent.CreatedAt = DateTime.UtcNow; // Cập nhật lại thời gian mới nhất nếu muốn

                    await _context.SaveChangesAsync();
                    return Ok(new { message = "Sự kiện này đã có sẵn, đã cập nhật trạng thái hiển thị!" });
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // API MỚI: Dành cho Admin lấy TẤT CẢ sự kiện (cả ẩn và hiện) để quản lý
        [HttpGet("getAllEventsAdmin")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAllEventsAdmin()
        {
            var events = await _context.Event.OrderByDescending(e => e.CreatedAt).ToListAsync();
            return Ok(events);
        }

        // API MỚI: Bật/Tắt trạng thái IsPublished (Soft Delete / Gỡ bài / Đăng lại)
        [HttpPut("togglePublish/{eventId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> TogglePublish(Guid eventId)
        {
            var evt = await _context.Event.FindAsync(eventId);
            if (evt == null) return NotFound(new { message = "Không tìm thấy sự kiện." });

            evt.IsPublished = !evt.IsPublished; // Đảo ngược trạng thái
            await _context.SaveChangesAsync();

            return Ok(new { message = evt.IsPublished ? "Đã khôi phục (Đăng lại) sự kiện!" : "Đã gỡ bài thành công!" });
        }

        // =======================================================
        // PHẦN 2: API DÀNH CHO USER VÀ ADMIN ĐỂ HIỂN THỊ
        // =======================================================

        [HttpGet("getPublishedEvents")]
        [Authorize(Roles = "User, Admin")] // Yêu cầu đăng nhập, cả User và Admin đều xem được
        public async Task<IActionResult> GetPublishedEvents()
        {
            // TÍNH NĂNG TỰ ĐỘNG XÓA: Lấy ra các sự kiện cũ hơn 14 ngày
            var expirationDate = DateTime.UtcNow.AddDays(-14);
            var expiredEvents = await _context.Event.Where(e => e.CreatedAt < expirationDate).ToListAsync();

            if (expiredEvents.Any())
            {
                _context.Event.RemoveRange(expiredEvents); // Dọn rác cứng khỏi DB
                await _context.SaveChangesAsync();
            }

            // CHỈ TRẢ VỀ CHO USER CÁC SỰ KIỆN ĐANG BẬT ISPUBLISHED = TRUE
            var events = await _context.Event
                .Where(e => e.IsPublished == true)
                .OrderByDescending(e => e.CreatedAt)
                .ToListAsync();

            return Ok(events);
        }

        [HttpGet("getLiveEventProducts/{eventId}")]
        [Authorize(Roles = "User, Admin")]
        public async Task<IActionResult> GetLiveEventProducts(Guid eventId)
        {
            try
            {
                var evt = await _context.Event.FindAsync(eventId);
                if (evt == null) return NotFound(new { message = "Không tìm thấy sự kiện trong hệ thống." });

                var products = await _productRepository.GetLiveProductsFromEventAsync(evt.EventLink);
                // TÍNH NĂNG TỰ ĐỘNG XÓA: Nếu Tiki trả về rỗng -> Sự kiện đã kết thúc
                if (products == null || products.Count == 0)
                {
                    //_context.Event.Remove(evt);
                    //await _context.SaveChangesAsync();
                    return BadRequest(new { message = "Sự kiện này đã kết thúc trên Tiki và tự động được dọn dẹp khỏi hệ thống." });
                }

                return Ok(products);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}