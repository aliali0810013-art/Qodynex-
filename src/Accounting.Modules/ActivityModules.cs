using Accounting.Application;

namespace Accounting.Modules;

public enum ActivityPlatform
{
    Windows = 1,
    Android = 2
}

public sealed record ModuleScreenDefinition(
    string Key,
    string Title,
    ActivityPlatform Platform,
    string RequiredFeature);

public interface IActivityModule
{
    string Key { get; }
    string DisplayName { get; }
    IReadOnlySet<string> Features { get; }
    IReadOnlyList<ModuleScreenDefinition> Screens { get; }
    IReadOnlyList<IDocumentPostingRule> PostingRules { get; }
}

public sealed class ModuleRegistry
{
    private readonly IReadOnlyDictionary<string, IActivityModule> _modules;

    public ModuleRegistry(IEnumerable<IActivityModule> modules)
    {
        _modules = modules.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        if (_modules.Count == 0) throw new InvalidOperationException("At least one activity module must be registered.");
    }

    public IReadOnlyList<IActivityModule> ResolveEnabled(IEnumerable<string> enabledModuleKeys)
    {
        var result = new List<IActivityModule>();
        foreach (var key in enabledModuleKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_modules.TryGetValue(key, out var module))
                throw new InvalidOperationException($"Unknown activity module '{key}'.");
            result.Add(module);
        }
        return result;
    }

    public IReadOnlyList<ModuleScreenDefinition> VisibleScreens(IEnumerable<string> enabledModuleKeys, ActivityPlatform platform)
    {
        return ResolveEnabled(enabledModuleKeys)
            .SelectMany(x => x.Screens)
            .Where(x => x.Platform == platform)
            .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<IDocumentPostingRule> PostingRules(IEnumerable<string> enabledModuleKeys) =>
        ResolveEnabled(enabledModuleKeys).SelectMany(x => x.PostingRules).ToArray();

    public bool IsFeatureEnabled(IEnumerable<string> enabledModuleKeys, string feature) =>
        ResolveEnabled(enabledModuleKeys).Any(x => x.Features.Contains(feature));
}
