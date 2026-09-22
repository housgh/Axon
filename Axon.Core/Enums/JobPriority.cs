namespace Axon.Core.Enums;

public enum JobPriority
{
    // Medium is first (= 0 = default(JobPriority)) so a caller who never sets Priority gets
    // Medium without needing an explicit default value anywhere. Appended values only from here
    // on, same rule as JobState: this is persisted as a raw int (see each store's Priority
    // column and the (int)priority casts throughout), so existing stored values must keep their
    // meaning.
    Medium,
    Low,
    High,
    Critical,
}
