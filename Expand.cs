using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Common
{
    public static class Expand
    {
        public static short ToShort(this string s)
        {
            return Convert.ToInt16(s);
        }
        public static int ToInt32(this string s)
        {
            return Convert.ToInt32(s);
        }
        public static decimal ToDecimal(this string s)
        {
            return Convert.ToDecimal(s);
        }

        public static bool ToBool(this string s)
        {
            return Convert.ToBoolean(s);
        }

        public static string ToJson(this object o)
        {
            return JsonSerializer.Serialize(o);
        }

        public static T ToJsonObj<T>(this JsonElement s)
        {
            return JsonSerializer.Deserialize<T>(s, GlobalDef._jso);
        }

        public static T ToJsonObj<T>(this string s)
        {
            return JsonSerializer.Deserialize<T>(s,GlobalDef._jso);
        }

        public static T ToJsonObj<T>(this UntypedNode je)
        {
            var str=KiotaJsonSerializer.SerializeAsStringAsync(je).GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<T>(str,GlobalDef._jso);
        }

    }
}
