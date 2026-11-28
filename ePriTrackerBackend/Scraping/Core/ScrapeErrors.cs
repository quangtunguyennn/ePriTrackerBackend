using System.Net;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>Phân loại lỗi khi lấy dữ liệu từ sàn.</summary>
    public static class ScrapeErrors
    {
        /// <summary>
        /// Sàn báo trang/sản phẩm không tồn tại: 404 hoặc 410 (tìm cả trong lỗi bên trong).
        /// Khảo sát 17/11/2026: Tiki (API), CellphoneS, HACOM, GearVN đều trả 404 thật cho sản phẩm đã gỡ, không chuyển hướng.
        /// Lỗi mạng, timeout, 403/429 (bị chặn) KHÔNG phải "không tồn tại".
        /// </summary>
        public static bool IsNotFound(Exception exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone }) return true;
                if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsNotFound)) return true;
            }
            return false;
        }

        /// <summary>
        /// Sàn từ chối request: 403 (Forbidden – tường lửa / chống bot) hoặc 429 (Too Many Requests). Sản phẩm vẫn còn,
        /// chỉ là đang bị chặn → không tính là "không tồn tại"; job giá tạm dừng cào sàn đó (xem <see cref="PlatformBlockTracker"/>).
        /// </summary>
        public static bool IsBlocked(Exception exception)
        {
            for (var e = exception; e != null; e = e.InnerException)
            {
                if (e is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }) return true;
                if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsBlocked)) return true;
            }
            return false;
        }
    }
}
