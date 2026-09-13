using Axon.Server.Services;

namespace Axon.Server.Interfaces;

public interface IAxonRecurringJobStore
{
    Task AddOrUpdate(RecurringJob recurringJob);
    Task<List<RecurringJob>> GetAll();
    Task UpdateNextRun(string recurringJobId, long nextRunAt, long lastRunAt);
    Task Remove(string recurringJobId);
}
