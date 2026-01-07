
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Common
{
    public class GlobalDef
    {

        public static JsonSerializerOptions _jso = null;

        public static void Init()
        {
            _jso = new JsonSerializerOptions();
            _jso.Converters.Add(new JsonStringEnumConverter());
        }
    }
}
