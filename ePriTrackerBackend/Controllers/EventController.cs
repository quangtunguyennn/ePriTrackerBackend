using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Services.Events;
using ePriTrackerBackend.Services.Products;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ePriTrackerBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly IEventService _eventService;
        private readonly IProductTrackingService _tracking;

        public EventController(IEventService eventService, IProductTrackingService tracking)
        {
            _eventService = eventService;
            _tracking = tracking;
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
                return Ok(await _eventService.GetPreviewAsync());
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
                var result = await _eventService.PublishAsync(tikiEvent);

                return Ok(new
                {
                    message = result == PublishEventResult.Created
                        ? "Đã đăng sự kiện thành công!"
                        : "Sự kiện này đã có sẵn, đã cập nhật trạng thái hiển thị!"
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Admin lấy TẤT CẢ sự kiện (cả ẩn và hiện) để quản lý
        [HttpGet("getAllEventsAdmin")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAllEventsAdmin()
        {
            return Ok(await _eventService.GetAllForAdminAsync());
        }

        // Bật/Tắt trạng thái IsPublished (Soft Delete / Gỡ bài / Đăng lại)
        [HttpPut("togglePublish/{eventId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> TogglePublish(Guid eventId)
        {
            var isPublished = await _eventService.TogglePublishAsync(eventId);
            if (isPublished == null) return NotFound(new { message = "Không tìm thấy sự kiện." });

            return Ok(new { message = isPublished.Value ? "Đã khôi phục (Đăng lại) sự kiện!" : "Đã gỡ bài thành công!" });
        }

        // =======================================================
        // PHẦN 2: API DÀNH CHO USER VÀ ADMIN ĐỂ HIỂN THỊ
        // =======================================================

        [HttpGet("getPublishedEvents")]
        [Authorize(Roles = "User, Admin")] // Yêu cầu đăng nhập, cả User và Admin đều xem được
        public async Task<IActionResult> GetPublishedEvents()
        {
            return Ok(await _eventService.GetPublishedAsync());
        }

        [HttpGet("getLiveEventProducts/{eventId}")]
        [Authorize(Roles = "User, Admin")]
        public async Task<IActionResult> GetLiveEventProducts(Guid eventId)
        {
            try
            {
                var result = await _eventService.GetLiveProductsAsync(eventId);

                return result.Status switch
                {
                    LiveProductsStatus.EventNotFound => NotFound(new { message = "Không tìm thấy sự kiện trong hệ thống." }),
                    LiveProductsStatus.NoProducts => BadRequest(new { message = "Sự kiện này đã kết thúc trên Tiki hoặc không có sản phẩm." }),
                    _ => Ok(result.Products)
                };
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("trackDeal")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> TrackDeal([FromBody] TrackDealDto request)
        {
            try
            {
                // Lấy thông tin User đang đăng nhập từ Token
                var userId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value);

                var result = await _tracking.TrackDealAsync(userId, request);

                if (result == TrackDealResult.AlreadyTracking)
                    return BadRequest(new { message = "Bạn đã theo dõi sản phẩm này rồi!" });

                return Ok(new { message = "Đã thêm vào danh sách theo dõi thành công!" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("deleteEvent/{eventId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteEvent(Guid eventId)
        {
            try
            {
                if (!await _eventService.DeleteAsync(eventId))
                    return NotFound(new { message = "Không tìm thấy sự kiện để xóa." });

                return Ok(new { message = "Đã xóa sự kiện thành công." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
