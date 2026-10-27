using OmniLink.Core;
using OmniLink.Core.Strategies;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// Unit Tests สำหรับ Strategy Pattern ในการสลับภาษา (CP353002)
/// </summary>
public class LanguageSyncStrategyTests
{
    [Fact]
    public void Factory_ShouldReturnGlobalCalibrated_ByDefault()
    {
        var strategy = LanguageSyncStrategyFactory.Create("global_calibrated");
        Assert.IsType<GlobalCalibratedSyncStrategy>(strategy);
        Assert.Equal("global_calibrated", strategy.ModeName);

        var fallback = LanguageSyncStrategyFactory.Create(null);
        Assert.IsType<GlobalCalibratedSyncStrategy>(fallback);
    }

    [Fact]
    public void Factory_ShouldReturnTargetOnly_WhenConfigured()
    {
        var strategy = LanguageSyncStrategyFactory.Create("target_only");
        Assert.IsType<TargetOnlySyncStrategy>(strategy);
        Assert.Equal("target_only", strategy.ModeName);
    }
}
