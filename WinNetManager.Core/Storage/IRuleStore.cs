
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Storage;

public interface IRuleStore
{
    List<AutomationRule> Load();
    void Save(IEnumerable<AutomationRule> rules);
}
