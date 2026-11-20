using ePriTrackerBackend.Models.DTOs;
using Hangfire;
using Hangfire.Storage;

namespace ePriTrackerBackend.Services.Admin
{
    /// <summary>Đọc trạng thái job nền – tách khỏi Hangfire để test không cần kho Hangfire.</summary>
    public interface IJobStatusReader
    {
        IReadOnlyList<RecurringJobStatusDTO> GetRecurringJobs();
        JobCountsDTO GetJobCounts();
    }

    /// <summary>Đọc từ kho Hangfire đang dùng (SQL Server) – cùng dữ liệu với Dashboard /hangfire.</summary>
    public class HangfireJobStatusReader : IJobStatusReader
    {
        public IReadOnlyList<RecurringJobStatusDTO> GetRecurringJobs()
        {
            using var connection = JobStorage.Current.GetConnection();
            return connection.GetRecurringJobs()
                .OrderBy(j => j.Id)
                .Select(j => new RecurringJobStatusDTO
                {
                    Id = j.Id,
                    Cron = j.Cron,
                    LastExecution = AsUtc(j.LastExecution),
                    LastState = j.LastJobState,
                    NextExecution = AsUtc(j.NextExecution),
                    Error = j.Error
                })
                .ToList();
        }

        public JobCountsDTO GetJobCounts()
        {
            var stats = JobStorage.Current.GetMonitoringApi().GetStatistics();
            return new JobCountsDTO
            {
                Succeeded = stats.Succeeded,
                Failed = stats.Failed,
                Processing = stats.Processing,
                Enqueued = stats.Enqueued,
                Scheduled = stats.Scheduled
            };
        }

        // Hangfire lưu giờ UTC dạng DateTime
        private static DateTimeOffset? AsUtc(DateTime? value)
            => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null;
    }
}
