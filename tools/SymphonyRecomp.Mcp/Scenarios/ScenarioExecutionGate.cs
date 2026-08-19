using ModelContextProtocol;

namespace SymphonyRecomp.Mcp.Scenarios;

public sealed class ScenarioExecutionGate
{
    private readonly object _sync = new();
    private bool _scenarioRunning;
    private int _activeMutations;

    public IDisposable TryEnterScenario()
    {
        lock (_sync)
        {
            if (_scenarioRunning || _activeMutations != 0)
                throw new McpException("Scenario execution is unavailable while another scenario or direct mutation is active.");
            _scenarioRunning = true;
            return new Lease(this, scenario: true);
        }
    }

    public IDisposable TryEnterMutation()
    {
        lock (_sync)
        {
            if (_scenarioRunning)
                throw new McpException("A direct mutation is unavailable while a scenario is running.");
            _activeMutations++;
            return new Lease(this, scenario: false);
        }
    }

    private void Exit(bool scenario)
    {
        lock (_sync)
        {
            if (scenario) _scenarioRunning = false;
            else _activeMutations--;
        }
    }

    private sealed class Lease(ScenarioExecutionGate owner, bool scenario) : IDisposable
    {
        private ScenarioExecutionGate? _owner = owner;

        public void Dispose()
        {
            ScenarioExecutionGate? current = Interlocked.Exchange(ref _owner, null);
            current?.Exit(scenario);
        }
    }
}
