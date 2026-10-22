using System.Text;
using System.Text.Json;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// GraphQL NỘI BỘ mà website CellphoneS dùng (host api.cellphones.com.vn – không có robots.txt).
    /// API không công bố nên có thể đổi bất cứ lúc nào: mọi nơi dùng đều có đường dự phòng
    /// (gợi ý: bỏ qua sàn; sự kiện: render trang chủ; giá: đọc JSON-LD trang sản phẩm).
    /// Chung giới hạn tốc độ với trang web CellphoneS (Scraping:Platforms:CellphoneS:MinIntervalMs).
    /// </summary>
    public interface ICellphoneSApi
    {
        /// <summary>Ô tìm kiếm của website (tối đa ~5 kết quả, xếp theo độ liên quan).</summary>
        Task<IReadOnlyList<CellphoneSApiProduct>> QuickSearchAsync(string terms, CancellationToken cancellationToken = default);

        /// <summary>Mã các nhóm flash sale đang bày trên trang chủ.</summary>
        Task<IReadOnlyList<string>> GetHomeFlashSaleGroupUdsAsync(CancellationToken cancellationToken = default);

        /// <returns>null nếu không có nhóm này.</returns>
        Task<CellphoneSFlashSaleGroup> GetFlashSaleGroupAsync(string groupUd, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CellphoneSFlashSaleSlot>> GetFlashSaleSlotsAsync(int programId, CancellationToken cancellationToken = default);

        /// <summary>Link, giá đang bán, giá gốc của nhiều sản phẩm trong MỘT request.</summary>
        Task<IReadOnlyList<CellphoneSApiProduct>> GetProductsByIdsAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken = default);

        /// <summary>Mã sản phẩm CellphoneS của một link. Không phải trang sản phẩm → null.</summary>
        Task<long?> GetProductIdByUrlAsync(string productUrl, CancellationToken cancellationToken = default);
    }

    public class CellphoneSApi : ICellphoneSApi
    {
        public const string HttpClientName = "CellphoneS.Api";
        public const string BaseAddress = "https://api.cellphones.com.vn/";

        /// <summary>Tỉnh mặc định của website (TP.HCM) – giá có thể khác theo tỉnh.</summary>
        public const int ProvinceId = 30;

        private const string SearchPath = "graphql-search/v2/graphql/query";
        private const string DashboardPath = "graphql-dashboard/graphql/query";
        private const string CatalogPath = "v2/graphql/query";
        private const string UrlPath = "graphql-url/graphql/query";
        private const string FlashSaleConfigKey = "config_hotsale_flashsale";

        private readonly IHttpClientFactory _httpClientFactory;

        public CellphoneSApi(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task<IReadOnlyList<CellphoneSApiProduct>> QuickSearchAsync(string terms, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(terms)) return Array.Empty<CellphoneSApiProduct>();

            string query = $@"query quick_search {{ quick_search(user_query: {{ terms: {Literal(terms.Trim())}, province: {ProvinceId} }}) {{
                products {{ product_id name product_condition url_path thumbnail display_price display_root_price }} }} }}";

            using var json = await PostAsync(SearchPath, query, cancellationToken);
            return CellphoneSApiParser.ParseQuickSearch(json.RootElement);
        }

        public async Task<IReadOnlyList<string>> GetHomeFlashSaleGroupUdsAsync(CancellationToken cancellationToken = default)
        {
            string query = $@"query D {{ dynamic_config(key: {Literal(FlashSaleConfigKey)}) {{ key is_active value_json }} }}";

            using var json = await PostAsync(DashboardPath, query, cancellationToken);
            return CellphoneSApiParser.ParseHomeFlashSaleGroupUds(json.RootElement);
        }

        public async Task<CellphoneSFlashSaleGroup> GetFlashSaleGroupAsync(string groupUd, CancellationToken cancellationToken = default)
        {
            string query = $@"query G {{ flash_sale_groups(group_ud: {Literal(groupUd)}) {{
                ud title_desktop title_mobile list_program {{ id title is_active started_at ended_at }} }} }}";

            using var json = await PostAsync(DashboardPath, query, cancellationToken);
            return CellphoneSApiParser.ParseFlashSaleGroup(json.RootElement);
        }

        public async Task<IReadOnlyList<CellphoneSFlashSaleSlot>> GetFlashSaleSlotsAsync(int programId, CancellationToken cancellationToken = default)
        {
            string query = $@"query F {{ flash_sale_programs(flash_sale_id: {programId} province_id: {ProvinceId}) {{
                id flash_sale_slots {{ product_id max_slot sold is_active product_name product_thumbnail
                    flash_sale_prices {{ province_id product_normal_price product_special_price product_flash_sale_price }} }} }} }}";

            using var json = await PostAsync(DashboardPath, query, cancellationToken);
            return CellphoneSApiParser.ParseFlashSaleSlots(json.RootElement);
        }

        public async Task<IReadOnlyList<CellphoneSApiProduct>> GetProductsByIdsAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken = default)
        {
            if (productIds == null || productIds.Count == 0) return Array.Empty<CellphoneSApiProduct>();

            string ids = string.Join(",", productIds.Distinct().Select(id => Literal(id.ToString())));
            string query = $@"query P {{ products(filter: {{ static: {{ province_id: {ProvinceId}, product_id: [{ids}] }} }}, size: {productIds.Count}) {{
                general {{ product_id name url_path }} filterable {{ display_price display_root_price thumbnail }} }} }}";

            using var json = await PostAsync(CatalogPath, query, cancellationToken);
            return CellphoneSApiParser.ParseCatalogProducts(json.RootElement);
        }

        public async Task<long?> GetProductIdByUrlAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            if (!CellphoneSUrlParser.TryGetSlug(productUrl, out var slug)) return null;

            string query = $@"query U {{ url_info(request_path: {Literal(slug + ".html")}) {{ product_id type }} }}";

            using var json = await PostAsync(UrlPath, query, cancellationToken);
            return CellphoneSApiParser.ParseUrlInfoProductId(json.RootElement);
        }

        private async Task<JsonDocument> PostAsync(string path, string query, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(JsonSerializer.Serialize(new { query, variables = new { } }), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(path, content, cancellationToken);

            // 422 = GraphQL không hợp lệ (API đổi schema) → vẫn đọc body để báo lỗi rõ ràng
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 422)
                response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        // Chuỗi trong GraphQL dùng cùng quy tắc escape với JSON → không bị chèn câu truy vấn qua từ khoá tìm kiếm
        private static string Literal(string value) => JsonSerializer.Serialize(value ?? "");
    }
}
