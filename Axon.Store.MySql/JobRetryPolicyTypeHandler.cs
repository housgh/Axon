using System.Data;
using System.Text.Json;
using Axon.Core.Models;
using Dapper;

namespace Axon.MySql;

public class JobRetryPolicyTypeHandler : SqlMapper.TypeHandler<AxonRetryPolicy?>
{
    public override void SetValue(IDbDataParameter parameter, AxonRetryPolicy? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value);
    }

    public override AxonRetryPolicy? Parse(object value)
    {
        if (value is not string json || string.IsNullOrEmpty(json))
            return null;

        return JsonSerializer.Deserialize<AxonRetryPolicy>(json);
    }
}
