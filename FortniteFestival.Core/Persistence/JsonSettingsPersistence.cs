using System.IO;
using System.Text;
using System.Threading.Tasks;
using FortniteFestival.Core.Config;
using Newtonsoft.Json;

namespace FortniteFestival.Core.Persistence
{
    public class JsonSettingsPersistence : ISettingsPersistence
    {
        private readonly string _path;

        public JsonSettingsPersistence(string path)
        {
            _path = path;
        }

        public async Task<Settings> LoadSettingsAsync()
        {
            try
            {
                if (!File.Exists(_path))
                    return new Settings();
                using (
                    var fs = new FileStream(
                        _path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        4096,
                        true
                    )
                )
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    var json = await sr.ReadToEndAsync().ConfigureAwait(false);
                    var obj = JsonConvert.DeserializeObject<Settings>(json) ?? new Settings();
                    return obj;
                }
            }
            catch
            {
                return new Settings();
            }
        }

        public async Task SaveSettingsAsync(Settings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                using (
                    var fs = new FileStream(
                        _path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        true
                    )
                )
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    await sw.WriteAsync(json).ConfigureAwait(false);
                }
            }
            catch { }
        }
    }
}
