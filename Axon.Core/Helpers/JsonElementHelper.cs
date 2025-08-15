using System.Text.Json;

namespace Axon.Core.Helpers;

public class JsonElementHelper
{
    public static object?[] ToObjectArray(object?[] objs)
    {
        var result = new object?[objs.Length];

        for (var i = 0; i < objs.Length; i++)
        {
            if (objs[i] is not JsonElement el)
            {
                result[i] = null;
                continue;
            }

            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    result[i] = el.GetString();
                    break;
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out long l)) result[i] = l;
                    else result[i] = el.GetDouble();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    result[i] = el.GetBoolean();
                    break;
                case JsonValueKind.Null:
                    result[i] = null;
                    break;
                case JsonValueKind.Object:
                    result[i] = el.Deserialize<Dictionary<string, object?>>();
                    break;
                case JsonValueKind.Array:
                    // recursively convert sub-array
                    var arr = ToObjectArray(el.EnumerateArray().Select(o => (object)o).ToArray());
                    result[i] = ToObjectArray(arr);
                    break;
                case JsonValueKind.Undefined:
                default:
                    result[i] = el.ToString();
                    break;
            }
        }

        return result;
    }

}