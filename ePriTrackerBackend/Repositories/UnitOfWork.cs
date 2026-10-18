using ePriTrackerBackend.Models.Context;

namespace ePriTrackerBackend.Repositories
{
    /// <summary>
    /// Ghi mọi thay đổi đang chờ của các repository xuống DB trong một lần.
    /// Các repository dùng chung một DbContext (scoped) nên service quyết định thời điểm lưu.
    /// </summary>
    public interface IUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }

    public class EfUnitOfWork : IUnitOfWork
    {
        private readonly ePriTrackerContext _context;

        public EfUnitOfWork(ePriTrackerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => _context.SaveChangesAsync(cancellationToken);
    }
}
