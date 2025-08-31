using System.IO;
using Newtonsoft.Json;

namespace FortniteFestival.Core.IO
{
    public static class JsonSerializer
    {
        public static T Load<T>(string path)
            where T : new()
        {
            if (!File.Exists(path))
                return new T();
            try
            {
                var json = File.ReadAllText(path);
                var obj = JsonConvert.DeserializeObject<T>(json);
                if (obj == null)
                    return new T();
                return obj;
            }
            catch
            {
                return new T();
            }
        }

        public static void Save<T>(string path, T obj)
        {
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.Indented));
            }
            catch { }
        }
    }
}
