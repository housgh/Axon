namespace Axon.Core.Helpers;

public class CronExpression
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;

    private CronExpression(HashSet<int> minutes, HashSet<int> hours, HashSet<int> daysOfMonth, HashSet<int> months, HashSet<int> daysOfWeek)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    public static CronExpression Parse(string expression)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException($"Cron expression must have 5 fields (minute hour day-of-month month day-of-week): '{expression}'");

        return new CronExpression(
            ParseField(fields[0], 0, 59),
            ParseField(fields[1], 0, 23),
            ParseField(fields[2], 1, 31),
            ParseField(fields[3], 1, 12),
            ParseField(fields[4], 0, 6));
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        var values = new HashSet<int>();

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (range, stepText) = part.Contains('/')
                ? (part[..part.IndexOf('/')], part[(part.IndexOf('/') + 1)..])
                : (part, "1");

            var step = int.Parse(stepText);

            int rangeStart, rangeEnd;
            if (range == "*")
            {
                rangeStart = min;
                rangeEnd = max;
            }
            else if (range.Contains('-'))
            {
                var bounds = range.Split('-');
                rangeStart = int.Parse(bounds[0]);
                rangeEnd = int.Parse(bounds[1]);
            }
            else
            {
                rangeStart = rangeEnd = int.Parse(range);
            }

            for (var i = rangeStart; i <= rangeEnd; i += step)
                values.Add(i);
        }

        return values;
    }

    public DateTimeOffset GetNextOccurrence(DateTimeOffset after)
    {
        var candidate = new DateTimeOffset(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Offset)
            .AddMinutes(1);

        var limit = after.AddYears(5);
        while (candidate < limit)
        {
            if (_months.Contains(candidate.Month)
                && _daysOfMonth.Contains(candidate.Day)
                && _daysOfWeek.Contains((int)candidate.DayOfWeek)
                && _hours.Contains(candidate.Hour)
                && _minutes.Contains(candidate.Minute))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);
        }

        throw new InvalidOperationException($"Could not find a matching occurrence within 5 years for cron expression.");
    }
}
