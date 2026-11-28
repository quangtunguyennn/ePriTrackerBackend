using System.Net;
using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class ScrapeErrorsTests
    {
        public static IEnumerable<object[]> NotFoundErrors()
        {
            yield return new object[] { new HttpRequestException("404", null, HttpStatusCode.NotFound) };
            yield return new object[] { new HttpRequestException("410", null, HttpStatusCode.Gone) };
            // bọc nhiều lớp (vd scraper bọc lại lỗi HTTP)
            yield return new object[] { new Exception("Lấy dữ liệu thất bại.", new HttpRequestException("404", null, HttpStatusCode.NotFound)) };
            yield return new object[] { new AggregateException(new HttpRequestException("404", null, HttpStatusCode.NotFound)) };
        }

        public static IEnumerable<object[]> OtherErrors()
        {
            yield return new object[] { new HttpRequestException("403", null, HttpStatusCode.Forbidden) };   // bị chặn
            yield return new object[] { new HttpRequestException("429", null, HttpStatusCode.TooManyRequests) };
            yield return new object[] { new HttpRequestException("500", null, HttpStatusCode.InternalServerError) };
            yield return new object[] { new HttpRequestException("Connection reset") };                       // lỗi mạng, không có mã
            yield return new object[] { new TaskCanceledException("timeout") };
            yield return new object[] { new InvalidOperationException("Could not read product information from this page.") };
            yield return new object[] { new Exception("Error: HTTP 404") };                                    // chỉ có chữ, không có mã
        }

        [Theory]
        [MemberData(nameof(NotFoundErrors))]
        public void NotFound_Detected(Exception error) => Assert.True(ScrapeErrors.IsNotFound(error));

        [Theory]
        [MemberData(nameof(OtherErrors))]
        public void OtherErrors_AreNotNotFound(Exception error) => Assert.False(ScrapeErrors.IsNotFound(error));

        [Fact]
        public void Null_IsNotNotFound() => Assert.False(ScrapeErrors.IsNotFound(null!));
    }
}
