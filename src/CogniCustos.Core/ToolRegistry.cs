using System.Text;
using CogniCustos.Contracts;

namespace CogniCustos.Core;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ITool> All => _tools.Values.ToList();

    public void Register(ITool tool)
    {
        var d = tool.Describe();
        _tools[d.Name] = tool;
    }

    public ITool? Find(string name) => _tools.TryGetValue(name, out var t) ? t : null;

    public string DescribeForPrompt()
    {
        var sb = new StringBuilder();
        foreach (var t in _tools.Values)
        {
            var d = t.Describe();
            var args = string.Join(", ", d.Parameters.Select(p => p.Required ? p.Name : p.Name + "?"));
            sb.AppendLine($"- {d.Name}({args}) [{d.Kind}] : {d.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<ToolResult> InvokeAsync(string name, IReadOnlyDictionary<string, string> args, CancellationToken ct = default)
    {
        var tool = Find(name);
        if (tool is null) return ToolResult.Fail($"unknown tool '{name}'");
        try
        {
            return await tool.InvokeAsync(args, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"{name} threw: {ex.Message}");
        }
    }
}
