using System.Text.Json;
using Axon.Core.Enums;
using Axon.Core.Models;
using Axon.Server.Interfaces;
using Axon.Server.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Axon.MongoDb;

public class AxonMongoStore(string connectionString, string databaseName) : IAxonJobStore
{
    private readonly MongoClient _client = new(connectionString);

    private IMongoCollection<BsonDocument> Jobs => _client.GetDatabase(databaseName).GetCollection<BsonDocument>(CollectionNames.Jobs);
    private IMongoCollection<BsonDocument> JobHistory => _client.GetDatabase(databaseName).GetCollection<BsonDocument>(CollectionNames.JobHistory);
    private IMongoCollection<BsonDocument> ConcurrencyLocks => _client.GetDatabase(databaseName).GetCollection<BsonDocument>(CollectionNames.ConcurrencyLocks);

    // Arguments/RetryPolicy are stored as JSON strings rather than native BSON, mirroring the
    // SQL backends' JobArgumentsTypeHandler/JobRetryPolicyTypeHandler - Arguments is a
    // List<object?> of arbitrary job-method-argument values, which BSON's static class mapping
    // can't round-trip without a discriminator per element; going through JSON keeps this
    // store's on-the-wire representation - and therefore its round-trip test expectations -
    // consistent with the other three backends.
    private static BsonDocument ToDocument(Job job) => new()
    {
        { "_id", job.JobId },
        { "DeviceName", job.DeviceName },
        { "Arguments", JsonSerializer.Serialize(job.Arguments) },
        { "MethodName", job.MethodName },
        { "Assembly", job.Assembly },
        { "DeclaringType", job.DeclaringType },
        { "ScheduledFor", job.ScheduledFor is null ? BsonNull.Value : job.ScheduledFor.Value },
        { "State", (int)job.State },
        { "Attempts", job.Attempts },
        { "MaxAttempts", job.MaxAttempts },
        { "ProcessingDeadline", BsonNull.Value },
        { "EnqueuedAt", job.EnqueuedAt },
        { "RetryPolicy", job.RetryPolicy is null ? BsonNull.Value : JsonSerializer.Serialize(job.RetryPolicy) },
        { "ConcurrencyKey", job.ConcurrencyKey is null ? BsonNull.Value : job.ConcurrencyKey },
        { "MaxConcurrent", job.MaxConcurrent is null ? BsonNull.Value : job.MaxConcurrent.Value },
        { "ParentJobId", job.ParentJobId is null ? BsonNull.Value : job.ParentJobId },
        { "ContinueOnParentFailure", job.ContinueOnParentFailure },
        { "IsDeleted", false },
        { "Priority", (int)job.Priority }
    };

    private static Job FromDocument(BsonDocument doc) => new(new JobInfo
    {
        Arguments = JsonSerializer.Deserialize<List<object?>>(doc["Arguments"].AsString) ?? [],
        MethodName = doc["MethodName"].AsString,
        Assembly = doc["Assembly"].AsString,
        DeclaringType = doc["DeclaringType"].AsString,
        RetryPolicy = doc["RetryPolicy"].IsBsonNull ? null : JsonSerializer.Deserialize<AxonRetryPolicy>(doc["RetryPolicy"].AsString),
        ConcurrencyKey = doc["ConcurrencyKey"].IsBsonNull ? null : doc["ConcurrencyKey"].AsString,
        MaxConcurrent = doc["MaxConcurrent"].IsBsonNull ? null : doc["MaxConcurrent"].AsInt32,
        Priority = doc.Contains("Priority") ? (JobPriority)doc["Priority"].AsInt32 : JobPriority.Medium
    })
    {
        JobId = doc["_id"].AsString,
        DeviceName = doc["DeviceName"].AsString,
        ScheduledFor = doc["ScheduledFor"].IsBsonNull ? null : doc["ScheduledFor"].ToInt64(),
        State = (JobState)doc["State"].AsInt32,
        Attempts = doc["Attempts"].AsInt32,
        MaxAttempts = doc["MaxAttempts"].AsInt32,
        ProcessingDeadline = doc["ProcessingDeadline"].IsBsonNull ? null : doc["ProcessingDeadline"].ToInt64(),
        ParentJobId = doc["ParentJobId"].IsBsonNull ? null : doc["ParentJobId"].AsString,
        ContinueOnParentFailure = doc["ContinueOnParentFailure"].AsBoolean
    };

    private static BsonDocument ToHistoryDocument(string jobId, JobState state, string? note) => new()
    {
        { "JobId", jobId },
        { "State", (int)state },
        { "Timestamp", DateTime.UtcNow.Ticks },
        { "Note", note is null ? BsonNull.Value : note }
    };

    private static JobHistoryEntry FromHistoryDocument(BsonDocument doc) => new()
    {
        JobId = doc["JobId"].AsString,
        State = (JobState)doc["State"].AsInt32,
        Timestamp = doc["Timestamp"].ToInt64(),
        Note = doc["Note"].IsBsonNull ? null : doc["Note"].AsString
    };

    public Task AddJob(Job job) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        using var session = await _client.StartSessionAsync();
        // A replica set (even a single-node one) is required to open a session-scoped
        // transaction - see the README for why Axon.Store.MongoDb requires one. AddJob only
        // needs the transaction to keep the job insert and its initial history entry atomic,
        // the same guarantee the SQL backends get from a plain database transaction.
        session.StartTransaction();
        try
        {
            await Jobs.InsertOneAsync(session, ToDocument(job));
            await JobHistory.InsertOneAsync(session, ToHistoryDocument(job.JobId, job.State, null));
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    });

    public Task<Job?> GetJob(string id) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id) & Builders<BsonDocument>.Filter.Eq("IsDeleted", false);
        var doc = await Jobs.Find(filter).FirstOrDefaultAsync();
        return doc is null ? null : FromDocument(doc);
    });

    public Task<List<Job>> GetJobs(int skip = 0, int take = 20, JobState[]? states = null) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var match = new BsonDocument { { "IsDeleted", false } };
        if (states is { Length: > 0 })
        {
            match["State"] = new BsonDocument("$in", new BsonArray(states.Select(s => (int)s)));
        }

        // Effective dispatch score: COALESCE(ScheduledFor, EnqueuedAt) - Boost[Priority],
        // ascending (lower score = dispatched sooner) - mirrors the SQL backends' ORDER BY
        // expression (see e.g. AxonSqlServerStore.GetJobs's EffectiveScoreOrderBy). $switch's
        // branch values must match Axon.Core.Enums.JobPriorityBoost.Ticks exactly - BSON query
        // text can't reference that dictionary directly. JobPriority's enum order is Medium=0,
        // Low=1, High=2, Critical=3. A document with no Priority field (written before this
        // field existed) falls through to the $switch's default, treated as Medium.
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
        [
            new BsonDocument("$match", match),
            new BsonDocument("$addFields", new BsonDocument
            {
                {
                    "EffectiveScore",
                    new BsonDocument("$subtract", new BsonArray
                    {
                        new BsonDocument("$ifNull", new BsonArray { "$ScheduledFor", "$EnqueuedAt" }),
                        new BsonDocument("$switch", new BsonDocument
                        {
                            {
                                "branches", new BsonArray
                                {
                                    new BsonDocument { { "case", new BsonDocument("$eq", new BsonArray { "$Priority", 0 }) }, { "then", 3_000_000_000L } }, // Medium: 5 min
                                    new BsonDocument { { "case", new BsonDocument("$eq", new BsonArray { "$Priority", 1 }) }, { "then", 0L } },             // Low: 0
                                    new BsonDocument { { "case", new BsonDocument("$eq", new BsonArray { "$Priority", 2 }) }, { "then", 9_000_000_000L } }, // High: 15 min
                                    new BsonDocument { { "case", new BsonDocument("$eq", new BsonArray { "$Priority", 3 }) }, { "then", 36_000_000_000L } } // Critical: 60 min
                                }
                            },
                            { "default", 3_000_000_000L } // no Priority field yet -> treated as Medium
                        })
                    })
                }
            }),
            new BsonDocument("$sort", new BsonDocument("EffectiveScore", 1)),
            new BsonDocument("$skip", skip),
            new BsonDocument("$limit", take),
            new BsonDocument("$unset", "EffectiveScore")
        ]);

        var docs = await Jobs.Aggregate(pipeline).ToListAsync();
        return docs.Select(FromDocument).ToList();
    });

    public Task UpdateState(string id, JobState state, string? note = null) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", id) & Builders<BsonDocument>.Filter.Eq("IsDeleted", false);
            var update = Builders<BsonDocument>.Update.Set("State", (int)state);
            await Jobs.UpdateOneAsync(session, filter, update);
            await JobHistory.InsertOneAsync(session, ToHistoryDocument(id, state, note));
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    });

    public Task RequeueForRetry(string id, long scheduledFor, string? note = null) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", id) & Builders<BsonDocument>.Filter.Eq("IsDeleted", false);
            var update = Builders<BsonDocument>.Update
                .Set("State", (int)JobState.Scheduled)
                .Set("ScheduledFor", scheduledFor)
                .Inc("Attempts", 1);
            await Jobs.UpdateOneAsync(session, filter, update);
            await JobHistory.InsertOneAsync(session, ToHistoryDocument(id, JobState.Scheduled, note));
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    });

    public Task Requeue(string id, string note = "Requeued manually") => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", id) & Builders<BsonDocument>.Filter.Eq("IsDeleted", false);
            var update = Builders<BsonDocument>.Update
                .Set("State", (int)JobState.Enqueued)
                .Set("ScheduledFor", BsonNull.Value)
                .Set("Attempts", 0);
            await Jobs.UpdateOneAsync(session, filter, update);
            await JobHistory.InsertOneAsync(session, ToHistoryDocument(id, JobState.Enqueued, note));
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    });

    public Task DeleteJob(string id) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var update = Builders<BsonDocument>.Update.Set("IsDeleted", true);
        await Jobs.UpdateOneAsync(filter, update);
    });

    public Task RecordFailure(string id, string? error) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        await JobHistory.InsertOneAsync(ToHistoryDocument(id, JobState.Failed, error));
    });

    public Task<List<JobHistoryEntry>> GetHistory(string jobId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("JobId", jobId);
        var docs = await JobHistory.Find(filter).SortBy(d => d["Timestamp"]).ToListAsync();
        return docs.Select(FromHistoryDocument).ToList();
    });

    public Task<bool> TryClaimJob(string id, long processingDeadline, string? note = null) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        // Two concurrent claims against the same ConcurrencyKey can both pass
        // TryClaimJobCore's count check before either commits (see its comment), in which case
        // MongoDB detects the write conflict at commit time and throws - the same
        // "loser retries" pattern as the SQL backends' deadlock retries (SQL Server 1205,
        // Postgres 40001, MySQL 1213).
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var session = await _client.StartSessionAsync();
            session.StartTransaction();
            try
            {
                var claimed = await TryClaimJobCore(session, id, processingDeadline, note);
                if (claimed)
                {
                    await session.CommitTransactionAsync();
                }
                else
                {
                    await session.AbortTransactionAsync();
                }
                return claimed;
            }
            catch (MongoCommandException e) when (HasWriteConflictLabel(e) && attempt < maxAttempts)
            {
                // CommitTransactionAsync itself can throw this (the conflict is only detected
                // server-side at commit time), in which case the driver has already ended the
                // transaction as failed and AbortTransactionAsync would throw
                // InvalidOperationException on top of it - so only abort if the transaction is
                // still in a state where that's a valid call (i.e. the failure happened in
                // TryClaimJobCore's reads/writes, before commit was ever attempted).
                if (session.IsInTransaction)
                {
                    await session.AbortTransactionAsync();
                }
                await Task.Delay(Random.Shared.Next(10, 50) * attempt);
            }
        }

        // Unreachable: the loop either returns or the final attempt's exception propagates.
        throw new InvalidOperationException("TryClaimJob retry loop exited without returning or throwing.");
    });

    private static bool HasWriteConflictLabel(MongoCommandException e) =>
        e.ErrorLabels.Contains("TransientTransactionError");

    private async Task<bool> TryClaimJobCore(IClientSessionHandle session, string id, long processingDeadline, string? note)
    {
        // Every write in this method runs inside the caller's session-scoped transaction, which
        // is what makes the count-then-update atomic against other concurrent claims - but only
        // for documents this transaction actually *writes*. MongoDB's write-conflict detection
        // is per-document: two transactions that both merely *read* the same ConcurrencyKey's
        // Processing count, then each write to a *different* job document, never collide from
        // MongoDB's point of view - there's no shared document either of them modified, so both
        // commit cleanly and the count check is worthless (this was caught by
        // TryClaimJob_ConcurrencyLimitedJobs_ConcurrentCallersAgainstRealMongo_NeverExceedLimit
        // during development: all 10 concurrent claims succeeded instead of being capped at 3).
        // The fix is ConcurrencyLocks: one document per ConcurrencyKey that every claim against
        // that key writes to (via FindOneAndUpdate's $inc below), specifically so two concurrent
        // claims against the same key are forced to contend for the same document - which is
        // what makes MongoDB's optimistic write-conflict check actually fire, the same role
        // SQL Server's UPDLOCK/HOLDLOCK range lock and Postgres's pg_advisory_xact_lock play
        // (see AxonSqlServerStore.TryClaimJob / AxonPostgresStore.TryClaimJob).
        var target = await Jobs.Find(session, Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
        if (target is null) return false;

        var concurrencyKey = target["ConcurrencyKey"].IsBsonNull ? null : target["ConcurrencyKey"].AsString;
        var maxConcurrent = target["MaxConcurrent"].IsBsonNull ? (int?)null : target["MaxConcurrent"].AsInt32;

        long processingCount = 0;
        if (concurrencyKey is not null && maxConcurrent is not null)
        {
            await ConcurrencyLocks.FindOneAndUpdateAsync(
                session,
                Builders<BsonDocument>.Filter.Eq("_id", concurrencyKey),
                Builders<BsonDocument>.Update.Inc("Touch", 1),
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true });

            var countFilter = Builders<BsonDocument>.Filter.Eq("ConcurrencyKey", concurrencyKey)
                & Builders<BsonDocument>.Filter.Eq("State", (int)JobState.Processing)
                & Builders<BsonDocument>.Filter.Eq("IsDeleted", false);
            processingCount = await Jobs.CountDocumentsAsync(session, countFilter);

            if (processingCount >= maxConcurrent.Value) return false;
        }

        var claimFilter = Builders<BsonDocument>.Filter.Eq("_id", id)
            & Builders<BsonDocument>.Filter.Eq("IsDeleted", false)
            & Builders<BsonDocument>.Filter.In("State", new[] { (int)JobState.Enqueued, (int)JobState.Scheduled });
        var claimUpdate = Builders<BsonDocument>.Update
            .Set("State", (int)JobState.Processing)
            .Set("ProcessingDeadline", processingDeadline);
        var result = await Jobs.UpdateOneAsync(session, claimFilter, claimUpdate);

        if (result.ModifiedCount == 0) return false;

        await JobHistory.InsertOneAsync(session, ToHistoryDocument(id, JobState.Processing, note));
        return true;
    }

    public Task<List<Job>> GetOrphanedProcessingJobs(long asOf) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("IsDeleted", false)
            & Builders<BsonDocument>.Filter.Eq("State", (int)JobState.Processing)
            & Builders<BsonDocument>.Filter.Ne("ProcessingDeadline", BsonNull.Value)
            & Builders<BsonDocument>.Filter.Lt("ProcessingDeadline", asOf);
        var docs = await Jobs.Find(filter).ToListAsync();
        return docs.Select(FromDocument).ToList();
    });

    public Task<List<Job>> GetProcessingJobsForDevice(string deviceName) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("IsDeleted", false)
            & Builders<BsonDocument>.Filter.Eq("State", (int)JobState.Processing)
            & Builders<BsonDocument>.Filter.Eq("DeviceName", deviceName);
        var docs = await Jobs.Find(filter).ToListAsync();
        return docs.Select(FromDocument).ToList();
    });

    public Task<List<Job>> GetContinuationsWaitingOn(string parentJobId) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        var filter = Builders<BsonDocument>.Filter.Eq("IsDeleted", false)
            & Builders<BsonDocument>.Filter.Eq("State", (int)JobState.AwaitingParent)
            & Builders<BsonDocument>.Filter.Eq("ParentJobId", parentJobId);
        var docs = await Jobs.Find(filter).ToListAsync();
        return docs.Select(FromDocument).ToList();
    });

    public Task<int> DeleteCompletedJobsOlderThan(long cutoff) => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        // A job's "finished at" isn't its own field - it's the timestamp of its most recent
        // JobHistory document - so identify candidates via an aggregation join rather than a
        // dedicated completed-at field, mirroring the SQL backends' equivalent JOIN query. Only
        // Succeeded jobs are cleaned up - Failed and Skipped are kept indefinitely regardless of
        // age.
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
        [
            new BsonDocument("$match", new BsonDocument
            {
                { "IsDeleted", false },
                { "State", (int)JobState.Succeeded }
            }),
            new BsonDocument("$lookup", new BsonDocument
            {
                { "from", CollectionNames.JobHistory },
                { "localField", "_id" },
                { "foreignField", "JobId" },
                { "as", "History" }
            }),
            new BsonDocument("$addFields", new BsonDocument
            {
                { "LastTimestamp", new BsonDocument("$max", "$History.Timestamp") }
            }),
            new BsonDocument("$match", new BsonDocument("LastTimestamp", new BsonDocument("$lt", cutoff))),
            new BsonDocument("$project", new BsonDocument("_id", 1))
        ]);

        var jobIds = (await Jobs.Aggregate(pipeline).ToListAsync())
            .Select(d => d["_id"].AsString)
            .ToList();

        if (jobIds.Count == 0) return 0;

        using var session = await _client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            var deleteFilter = Builders<BsonDocument>.Filter.In("_id", jobIds);
            await Jobs.UpdateManyAsync(session, deleteFilter, Builders<BsonDocument>.Update.Set("IsDeleted", true));

            var historyFilter = Builders<BsonDocument>.Filter.In("JobId", jobIds);
            await JobHistory.DeleteManyAsync(session, historyFilter);

            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }

        return jobIds.Count;
    });

    public Task<Dictionary<JobState, int>> CountJobsByState() => MongoExceptionTranslator.Run(connectionString, async () =>
    {
        // Live jobs in any state, plus soft-deleted (cleaned-up) jobs only when terminal - so a
        // Succeeded job purged by DeleteCompletedJobsOlderThan still counts, but IsDeleted never
        // applies to a non-terminal state in practice anyway.
        var terminalStates = new[] { (int)JobState.Succeeded, (int)JobState.Failed, (int)JobState.Skipped };
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
        [
            new BsonDocument("$match", new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("IsDeleted", false),
                new BsonDocument("State", new BsonDocument("$in", new BsonArray(terminalStates)))
            })),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$State" },
                { "Count", new BsonDocument("$sum", 1) }
            })
        ]);

        var rows = await Jobs.Aggregate(pipeline).ToListAsync();
        return rows.ToDictionary(d => (JobState)d["_id"].AsInt32, d => d["Count"].AsInt32);
    });
}
