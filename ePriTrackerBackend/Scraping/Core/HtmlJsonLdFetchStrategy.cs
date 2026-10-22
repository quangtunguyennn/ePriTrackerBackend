using System.Text.Json;
using System.Text.Json.Nodes;
using ePriTrackerBackend.Scraping.Abstractions;
using HtmlAgilityPack;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>
    /// Tải trang sản phẩm bằng HttpClient rồi trả khối JSON-LD "Product" (xem <see cref="JsonLdProductReader"/>).
    /// Dùng chung cho các sàn công bố dữ liệu schema.org trong HTML.
    /// </summary>
    public class HtmlJsonLdFetchStrategy : IFetchStrategy
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _httpClientName;
        private readonly IReadOnlyDictionary<string, string> _htmlFields;

        /// <param name="htmlFields">
        /// Lấy thêm HTML nằm ngoài JSON-LD (vd mô tả đầy đủ): tên thuộc tính sẽ gắn vào JSON → XPath (nhiều node thì nối lại).
        /// </param>
        public HtmlJsonLdFetchStrategy(IHttpClientFactory httpClientFactory, string httpClientName, IReadOnlyDictionary<string, string> htmlFields = null)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _httpClientName = string.IsNullOrWhiteSpace(httpClientName)
                ? throw new ArgumentException("Thiếu tên HttpClient.", nameof(httpClientName))
                : httpClientName;
            _htmlFields = htmlFields ?? new Dictionary<string, string>();
        }

        public string Name => FetchStrategyNames.HttpClient;

        /// <param name="path">URL tuyệt đối của trang sản phẩm.</param>
        public async Task<JsonElement> FetchJsonAsync(string path, FetchOptions options, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var url))
                throw new ArgumentException("Cần URL tuyệt đối của trang sản phẩm.", nameof(path));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options?.Timeout is TimeSpan timeout)
                timeoutCts.CancelAfter(timeout);

            var client = _httpClientFactory.CreateClient(_httpClientName);
            using var response = await client.GetAsync(url, timeoutCts.Token);
            response.EnsureSuccessStatusCode(); // 404 (link sai / sản phẩm bị gỡ), 403, 429 → HttpRequestException

            string html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var document = new HtmlDocument();
            document.LoadHtml(html);

            var product = JsonLdProductReader.FindProduct(document)
                ?? throw new InvalidOperationException("Không đọc được thông tin sản phẩm từ trang này.");

            return _htmlFields.Count == 0 ? product : AddHtmlFields(product, document);
        }

        private JsonElement AddHtmlFields(JsonElement product, HtmlDocument document)
        {
            var json = JsonNode.Parse(product.GetRawText()).AsObject();
            foreach (var (property, xpath) in _htmlFields)
            {
                var nodes = document.DocumentNode.SelectNodes(xpath);
                if (nodes == null) continue; // trang không có khối này → scraper tự dùng dữ liệu JSON-LD

                json[property] = string.Concat(nodes.Select(n => n.OuterHtml));
            }
            return JsonSerializer.SerializeToElement(json);
        }
    }
}
