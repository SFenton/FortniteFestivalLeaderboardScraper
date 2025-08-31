using System.Threading.Tasks;
using FortniteFestival.Core.Config;

namespace FortniteFestival.Core.Persistence
{
    public interface ISettingsPersistence
    {
        Task<Settings> LoadSettingsAsync();
        Task SaveSettingsAsync(Settings settings);
    }
}
