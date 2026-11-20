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
    }
}
