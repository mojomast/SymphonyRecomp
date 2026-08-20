using ModelContextProtocol;
using ModelContextProtocol.Server;
using SymphonyRecomp.Mcp;
using System.Diagnostics;
using System.Reflection;

namespace SymphonyRecomp.Automation.Tests;

public sealed class McpBoundaryTests
{
    [Fact]
    public void LogRingRetainsOnlyNewestSanitizedLines()
    {
        var ring = new BoundedLogRing(2);
        ring.Add("stdout", "one");
        ring.Add("stderr", "secret-two");
        ring.Add("stdout", "three");

        string[] lines = ring.Snapshot(10, value => value.Replace("secret", "[redacted]"));

        Assert.Equal(["[stderr] [redacted]-two", "[stdout] three"], lines);
    }

    [Fact]
    public async Task ContradictoryDirectionsAreRejectedBeforeBridgeAccess()
    {
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client);

        McpException error = await Assert.ThrowsAsync<McpException>(() => tools.RunInput(
            0, [new InputStep(["Left", "Right"], 1)], CancellationToken.None));

        Assert.Contains("Left and Right", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeutralInputStepPassesValidation()
    {
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client);

        McpException error = await Assert.ThrowsAsync<McpException>(() => tools.RunInput(
            1, [new InputStep([], 2)], CancellationToken.None));

        Assert.Contains("Launch the game first", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimelineTotalIsBounded()
    {
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client);

        McpException error = await Assert.ThrowsAsync<McpException>(() => tools.RunInput(
            0, [new InputStep(["Right"], 1000), new InputStep([], 801)], CancellationToken.None));

        Assert.Contains("cannot exceed 1800", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildEnvironmentDoesNotInheritUnlistedSecrets()
    {
        const string name = "SOTN_MCP_TEST_API_KEY";
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "must-not-leak");
            var startInfo = new ProcessStartInfo();

            GameProcessManager.ConfigureChildEnvironment(startInfo);

            Assert.False(startInfo.Environment.ContainsKey(name));
            Assert.False(startInfo.Environment.ContainsKey("SYMPHONYRECOMP_AUTOMATION_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void ToolInventoryIsStableAndFullyAnnotated()
    {
        string[] expected =
        [
            "sotn_cancel_campaign", "sotn_capture_screenshot", "sotn_clear_input", "sotn_get_campaign",
            "sotn_get_logs", "sotn_get_mod_diagnostics", "sotn_get_state", "sotn_hard_reset",
            "sotn_launch_game", "sotn_list_entities", "sotn_list_mods",
            "sotn_process_status", "sotn_read_memory", "sotn_reload_mod", "sotn_reset_mod_diagnostics",
            "sotn_run_input", "sotn_run_scenario", "sotn_set_mod_enabled", "sotn_start_campaign",
            "sotn_stop_game", "sotn_wait_for_state",
        ];
        McpServerToolAttribute[] tools = typeof(SotnTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute != null)
            .Cast<McpServerToolAttribute>()
            .OrderBy(attribute => attribute.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, tools.Select(tool => tool.Name));
        Assert.All(tools, tool => Assert.False(tool.OpenWorld));

        McpServerToolAttribute scenario = Assert.Single(tools,
            tool => tool.Name == "sotn_run_scenario");
        Assert.False(scenario.ReadOnly);
        Assert.True(scenario.Destructive);
        Assert.False(scenario.Idempotent);
        McpServerToolAttribute campaign = Assert.Single(tools,
            tool => tool.Name == "sotn_start_campaign");
        Assert.False(campaign.ReadOnly);
        Assert.True(campaign.Destructive);
        Assert.False(campaign.Idempotent);
        Assert.True(Assert.Single(tools, tool => tool.Name == "sotn_get_campaign").ReadOnly);
    }

    [Fact]
    public async Task DiagnosticArgumentsAreRejectedBeforeBridgeAccess()
    {
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client);

        await Assert.ThrowsAsync<McpException>(() =>
            tools.GetModDiagnostics("bad/id", CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() =>
            tools.ResetModDiagnostics("coop", "not-a-session", 0, true, CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() =>
            tools.ResetModDiagnostics("coop", new string('a', 32), -1, true, CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() =>
            tools.ResetModDiagnostics("coop", new string('a', 32), 0, false, CancellationToken.None));
    }
}
