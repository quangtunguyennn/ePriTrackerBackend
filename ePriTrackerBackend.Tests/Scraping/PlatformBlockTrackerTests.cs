using System.Net;
using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>Sàn trả 403/429 liên tục → tạm dừng cào sàn đó, tăng dần thời gian, thành công một lần là bình thường lại.</summary>
    public class PlatformBlockTrackerTests
    {
        private sealed class ManualTime : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private readonly ManualTime _time = new();

        private PlatformBlockTracker NewTracker() => new(_time);

        [Fact]
        public void TwoBlocks_NotPaused_ThirdBlock_PausesOneHour()
        {
            var tracker = NewTracker();

            Assert.False(tracker.RecordBlocked("HACOM", "403"));
            Assert.False(tracker.RecordBlocked("HACOM", "403"));
            Assert.False(tracker.IsPaused("HACOM", out _));

            Assert.True(tracker.RecordBlocked("HACOM", "403"));
            Assert.True(tracker.IsPaused("hacom", out var until)); // không phân biệt hoa thường
            Assert.Equal(_time.Now.AddHours(1), until);
        }

        [Fact]
        public void BlockedWhilePaused_DoesNotExtend()
        {
            var tracker = NewTracker();
            for (int i = 0; i < 3; i++) tracker.RecordBlocked("HACOM", "403");
            tracker.IsPaused("HACOM", out var until);

            _time.Now = _time.Now.AddMinutes(10);
            Assert.False(tracker.RecordBlocked("HACOM", "403")); // request song song cùng nhóm

            Assert.True(tracker.IsPaused("HACOM", out var stillUntil));
            Assert.Equal(until, stillUntil);
        }

        [Fact]
        public void StillBlockedAfterPause_PausesAgain_Doubling_UpToSixHours()
        {
            var tracker = NewTracker();
            for (int i = 0; i < 3; i++) tracker.RecordBlocked("HACOM", "403");

            var expectedHours = new[] { 2, 4, 6, 6 };
            foreach (int hours in expectedHours)
            {
                tracker.IsPaused("HACOM", out var until);
                _time.Now = until.AddSeconds(1); // hết tạm dừng → job thử lại một request
                Assert.False(tracker.IsPaused("HACOM", out _));

                Assert.True(tracker.RecordBlocked("HACOM", "403")); // vẫn bị chặn → tạm dừng ngay
                tracker.IsPaused("HACOM", out var next);
                Assert.Equal(_time.Now.AddHours(hours), next);
            }
        }

        [Fact]
        public void Success_ResetsEverything()
        {
            var tracker = NewTracker();
            for (int i = 0; i < 3; i++) tracker.RecordBlocked("HACOM", "403");

            tracker.RecordSuccess("HACOM");

            Assert.False(tracker.IsPaused("HACOM", out _));
            Assert.Null(tracker.GetStatus("HACOM"));
            Assert.False(tracker.RecordBlocked("HACOM", "403")); // đếm lại từ đầu
        }

        [Fact]
        public void PlatformsAreIndependent()
        {
            var tracker = NewTracker();
            for (int i = 0; i < 3; i++) tracker.RecordBlocked("HACOM", "403");

            Assert.True(tracker.IsPaused("HACOM", out _));
            Assert.False(tracker.IsPaused("GearVN", out _));
            Assert.False(tracker.IsPaused(null, out _));
        }

        [Fact]
        public void Status_ReportsCountPauseAndLastError()
        {
            var tracker = NewTracker();
            tracker.RecordBlocked("HACOM", "403 (Forbidden)");
            var status = tracker.GetStatus("HACOM")!;

            Assert.Equal(1, status.ConsecutiveBlocks);
            Assert.Null(status.PausedUntil);
            Assert.Equal("403 (Forbidden)", status.LastError);
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden, true)]
        [InlineData(HttpStatusCode.TooManyRequests, true)]
        [InlineData(HttpStatusCode.NotFound, false)]
        [InlineData(HttpStatusCode.InternalServerError, false)]
        public void IsBlocked_Only403And429(HttpStatusCode code, bool blocked)
        {
            var ex = new InvalidOperationException("wrapper", new HttpRequestException("x", null, code));

            Assert.Equal(blocked, ScrapeErrors.IsBlocked(ex));
            Assert.Equal(code == HttpStatusCode.NotFound, ScrapeErrors.IsNotFound(ex));
        }
    }
}
