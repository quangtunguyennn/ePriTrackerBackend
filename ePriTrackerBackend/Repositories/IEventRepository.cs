using ePriTrackerBackend.Models.DTOs;

namespace ePriTrackerBackend.Repositories
{
    public interface IEventRepository
    {
        Task<List<EventDTO>> GetCurrentTikiEvents();
    }
}
