using Mostlylucid.BotDetection.Licensing;

namespace Mostlylucid.BotDetection.Test.Licensing;

public class LicenseStateTests
{
    [Fact]
    public void FossLicenseState_AlwaysActive_NeverFrozen()
    {
        ILicenseState state = new FossLicenseState();

        Assert.True(state.IsActive);
        Assert.False(state.IsInGrace);
        Assert.False(state.LearningFrozen);
        Assert.False(state.LogOnly);
        Assert.Null(state.ExpiresAt);
        Assert.Null(state.GraceEndsAt);
    }
}
