using ePriTrackerBackend.Scraping.CellphoneS;

namespace ePriTrackerBackend.Tests.Helpers
{
    /// <summary>API CellphoneS giả: dữ liệu cấu hình sẵn, ghi lại các lần gọi; <see cref="Failure"/> → mọi hàm ném lỗi.</summary>
    internal sealed class FakeCellphoneSApi : ICellphoneSApi
    {
        public Exception? Failure { get; set; }

        /// <summary>Kết quả tìm theo đúng câu tìm; câu khác → rỗng.</summary>
        public Dictionary<string, List<CellphoneSApiProduct>> SearchResults { get; } = new();
        public List<string> SearchedTerms { get; } = new();

        public List<string> HomeGroupUds { get; } = new();
        public Dictionary<string, CellphoneSFlashSaleGroup> Groups { get; } = new();
        public Dictionary<int, List<CellphoneSFlashSaleSlot>> Slots { get; } = new();

        public Dictionary<long, CellphoneSApiProduct> Catalog { get; } = new();
        public List<IReadOnlyCollection<long>> CatalogRequests { get; } = new();
        /// <summary>Lô chứa mã này → ném lỗi (mô phỏng một lô hỏng).</summary>
        public HashSet<long> FailingCatalogIds { get; } = new();

        public Dictionary<string, long> IdsByUrl { get; } = new();
        public List<string> UrlLookups { get; } = new();

        public Task<IReadOnlyList<CellphoneSApiProduct>> QuickSearchAsync(string terms, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            SearchedTerms.Add(terms);
            return Task.FromResult<IReadOnlyList<CellphoneSApiProduct>>(SearchResults.TryGetValue(terms, out var r) ? r : new List<CellphoneSApiProduct>());
        }

        public Task<IReadOnlyList<string>> GetHomeFlashSaleGroupUdsAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            return Task.FromResult<IReadOnlyList<string>>(HomeGroupUds);
        }

        public Task<CellphoneSFlashSaleGroup> GetFlashSaleGroupAsync(string groupUd, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            return Task.FromResult(Groups.GetValueOrDefault(groupUd)!);
        }

        public Task<IReadOnlyList<CellphoneSFlashSaleSlot>> GetFlashSaleSlotsAsync(int programId, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            return Task.FromResult<IReadOnlyList<CellphoneSFlashSaleSlot>>(Slots.TryGetValue(programId, out var s) ? s : new List<CellphoneSFlashSaleSlot>());
        }

        public Task<IReadOnlyList<CellphoneSApiProduct>> GetProductsByIdsAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            CatalogRequests.Add(productIds);
            if (productIds.Any(FailingCatalogIds.Contains)) throw new HttpRequestException("Lô lỗi");
            return Task.FromResult<IReadOnlyList<CellphoneSApiProduct>>(productIds.Where(Catalog.ContainsKey).Select(id => Catalog[id]).ToList());
        }

        public Task<long?> GetProductIdByUrlAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            ThrowIfFailing();
            UrlLookups.Add(productUrl);
            return Task.FromResult<long?>(IdsByUrl.TryGetValue(productUrl, out var id) ? id : null);
        }

        public static CellphoneSApiProduct Product(long id, string slug, decimal price, decimal root = 0, string condition = "Mới", string name = null!)
            => new() { ProductId = id, Name = name ?? slug, UrlPath = slug + ".html", DisplayPrice = price, RootPrice = root, Thumbnail = $"/x/{slug}.png", Condition = condition };

        private void ThrowIfFailing()
        {
            if (Failure != null) throw Failure;
        }
    }
}
