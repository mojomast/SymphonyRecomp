using RecompOne.Runtime.Memory;
using Recompiled;
using SymphonyRecomp.Automation;

if (AutoUpdater.HandleRelaunch(args)) return 0;

bool automation = args.Contains("--automation", StringComparer.Ordinal);
string? cuePath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--disc")
    {
        if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
            throw new ArgumentException("--disc requires a cue path.");
        cuePath = args[i];
    }
    else if (!args[i].StartsWith('-') && cuePath == null)
    {
        cuePath = args[i];
    }
}

AutomationBridge? automationBridge = null;
if (automation)
{
    string pipeName = Environment.GetEnvironmentVariable("SYMPHONYRECOMP_AUTOMATION_PIPE") ?? "";
    string token = Environment.GetEnvironmentVariable("SYMPHONYRECOMP_AUTOMATION_TOKEN") ?? "";
    if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrEmpty(token))
        throw new InvalidOperationException(
            "Automation mode requires SYMPHONYRECOMP_AUTOMATION_PIPE and SYMPHONYRECOMP_AUTOMATION_TOKEN.");
    RecompOne.Runtime.Runtime.AutomationMode = true;
    automationBridge = new AutomationBridge(pipeName, token);
    Environment.SetEnvironmentVariable("SYMPHONYRECOMP_AUTOMATION_PIPE", null);
    Environment.SetEnvironmentVariable("SYMPHONYRECOMP_AUTOMATION_TOKEN", null);
}

try
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();

    if (Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(".languages.json", StringComparison.OrdinalIgnoreCase)) is { } languagesRes)
        RecompOne.Runtime.Runtime.AddLanguages(asm, languagesRes);

    RecompOne.Runtime.Runtime.SetStartupNotice("startup.beta", "startup.title", "SymphonyRecompBetaAck");

    DiscCheck.Register();
    WidescreenPatch.Register();
    WidescreenSettings.Register();
    ThroneLeftFill.Register();
    GameMenu.Register();
    CheatMenu.Register();
    QualityOfLifeMenu.Register();
    TrackerMenu.Register();
    RandoMenu.Register();
    AutoUpdater.Register();
    HelpMenu.Register();

    var title = AutoUpdater.CurrentTag is { } tag ? $"SymphonyRecomp {tag}" : "SymphonyRecomp"; //get version too

    if (Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(".SymphonyRecomp.ico", StringComparison.OrdinalIgnoreCase)) is { } iconRes)
    {
        using var iconStream = asm.GetManifestResourceStream(iconRes)!;
        using var iconMem = new MemoryStream();
        iconStream.CopyTo(iconMem);
        RecompOne.Runtime.Runtime.SetIcon(iconMem.ToArray());
    }

    RecompOne.Runtime.Runtime.Run(() => Entry.Run(new PSMemory(), cuePath, title));
    return 0;
}
finally
{
    automationBridge?.Dispose();
}
