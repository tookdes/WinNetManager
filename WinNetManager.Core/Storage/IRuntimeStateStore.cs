
using WinNetManager.Core.Models;

namespace WinNetManager.Core.Storage;

public interface IRuntimeStateStore
{
    Dictionary<string, RuleRuntimeState> Load();
    void Save(IReadOnlyDictionary<string, RuleRuntimeState> states);
}
