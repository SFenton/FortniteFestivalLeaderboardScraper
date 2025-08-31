using System.IO;
using System.Threading.Tasks;
using FortniteFestival.Core.Config;
using Newtonsoft.Json;

namespace FortniteFestival.Core.Persistence
{
    public class JsonSettingsPersistence : ISettingsPersistence
    {
        private readonly string _path;
        public JsonSettingsPersistence(string path){ _path = path; }
        public Task<Settings> LoadSettingsAsync()
        {
            try
            {
                if(!File.Exists(_path)) return Task.FromResult(new Settings());
                var json = File.ReadAllText(_path);
                var obj = JsonConvert.DeserializeObject<Settings>(json) ?? new Settings();
                return Task.FromResult(obj);
            }
            catch { return Task.FromResult(new Settings()); }
        }
        public Task SaveSettingsAsync(Settings settings)
        {
            try { File.WriteAllText(_path, JsonConvert.SerializeObject(settings, Formatting.Indented)); } catch { }
            return Task.CompletedTask;
        }
    }
}
