using Axon.Server.Services;

namespace Axon.Server.Interfaces;

public interface IAxonRecurringJobStore
{
    Task AddOrUpdate(RecurringJob recurringJob);

    /// <summary>The recurring job with this id, or null if none exists.</summary>
    Task<RecurringJob?> GetById(string recurringJobId);

    /// <summary>
    /// Recurring jobs ordered by <c>NextRunAt</c>, <paramref name="skip"/>/<paramref name="take"/>
    /// paginated the same way <see cref="Axon.Server.Interfaces.IAxonJobStore.GetJobs"/> paginates
    /// jobs (default page size 20) - pass a large enough <paramref name="take"/> (e.g.
    /// <see cref="int.MaxValue"/>) when the caller genuinely needs every recurring job in one
    /// call, such as a poll loop deciding what's currently due.
    /// </summary>
    Task<List<RecurringJob>> GetAll(int skip = 0, int take = 20);

    Task UpdateNextRun(string recurringJobId, long nextRunAt, long lastRunAt);
    Task Remove(string recurringJobId);
}
