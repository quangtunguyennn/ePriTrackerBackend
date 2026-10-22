using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// Sự kiện CellphoneS TỰ ĐĂNG: mọi sự kiện (Flash sale, Deal sốc) nằm trên trang chủ và nạp bằng JavaScript
    /// → render trang chủ MỘT lần là có đủ sự kiện lẫn sản phẩm. Ưu đãi thẻ ngân hàng (/uu-dai-doi-tac) không lấy:
    /// giá trên trang không giảm.
    /// </summary>
    public sealed class CellphoneSEventSource : IAutoEventSource
    {
        private readonly IPageRenderer _renderer;
        private readonly TimeProvider _time;

        public CellphoneSEventSource(IPageRenderer renderer, TimeProvider timeProvider = null)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _time = timeProvider ?? TimeProvider.System;
        }

        public string Platform => CellphoneSScraper.PlatformName;

        public async Task<IReadOnlyList<ScrapedEvent>> GetLiveEventsAsync(CancellationToken cancellationToken = default)
        {
            string html = await _renderer.RenderAsync(CellphoneSHomeEventParser.HomeUrl, cancellationToken);
            return CellphoneSHomeEventParser.Parse(html, _time.GetUtcNow());
        }
    }
}
