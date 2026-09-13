using System.Data;
using System.Text.Json;
using Dapper;

namespace Axon.SqlServer;

public class JobArgumentsTypeHandler : SqlMapper.TypeHandler<List<object?>>
{
    public override void SetValue(IDbDataParameter parameter, List<object?>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = JsonSerializer.Serialize(value ?? []);
    }

    public override List<object?> Parse(object value)
    {
        if (value is not string json || string.IsNullOrEmpty(json))
            return [];

        return JsonSerializer.Deserialize<List<object?>>(json) ?? [];
    }
}
