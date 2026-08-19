using RecompOne.Runtime.Modding;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ModDiagnosticProviderTests
{
    [Fact]
    public void ExactConventionCreatesCallableProvider()
    {
        var instance = new ExactProvider();
        ModDiagnosticProvider provider = ModDiagnosticProvider.Discover([instance])!;

        Assert.Equal("{\"frame\":42}", provider.Capture(42));
        Assert.True(provider.TryReset(new string('a', 32), 7));
        Assert.Equal((new string('a', 32), 7), instance.ResetIdentity);
    }

    [Fact]
    public void IncompleteOrWrongConventionIsIgnored()
    {
        Assert.Null(ModDiagnosticProvider.Discover([new MissingReset()]));
        Assert.Null(ModDiagnosticProvider.Discover([new WrongCaptureReturn()]));
    }

    [Fact]
    public void MultipleProvidersFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModDiagnosticProvider.Discover([new ExactProvider(), new ExactProvider()]));
    }

    sealed class ExactProvider : IMod
    {
        public (string Session, int Generation)? ResetIdentity { get; private set; }
        public void OnLoad() { }
        public string CaptureAutomationDiagnostics(long automationFrame) => $"{{\"frame\":{automationFrame}}}";
        public bool TryResetAutomationDiagnostics(string sessionId, int expectedGeneration)
        {
            ResetIdentity = (sessionId, expectedGeneration);
            return true;
        }
    }

    sealed class MissingReset : IMod
    {
        public void OnLoad() { }
        public string CaptureAutomationDiagnostics(long automationFrame) => "{}";
    }

    sealed class WrongCaptureReturn : IMod
    {
        public void OnLoad() { }
        public object CaptureAutomationDiagnostics(long automationFrame) => new();
        public bool TryResetAutomationDiagnostics(string sessionId, int expectedGeneration) => true;
    }
}
