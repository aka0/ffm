using System.Diagnostics;

namespace FFM.App;

public sealed record AiProvider(string Name, string Website)
{
    public override string ToString() => Name;
}

public static class AiProviders
{
    public static IReadOnlyList<AiProvider> All { get; } =
    [
        new("ChatGPT", "https://chatgpt.com/"),
        new("Grok", "https://grok.com/")
    ];

    public static AiProvider Find(string name) => All.FirstOrDefault(provider => provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static void Open(string name) => Process.Start(new ProcessStartInfo(Find(name).Website) { UseShellExecute = true });
}
