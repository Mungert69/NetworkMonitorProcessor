using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Processor.Services;
using Xunit;

public class StateSetupTest
{
    private static MonitorPingCollection CreateMonitorPingCollection()
    {
        var loggerMock = new Mock<ILogger>();
        return new MonitorPingCollection(loggerMock.Object);
    }

    private static StateSetup CreateStateSetup(Mock<IFileRepo> fileRepoMock, MonitorPingCollection collection)
    {
        var loggerMock = new Mock<ILogger>();
        return new StateSetup(loggerMock.Object, collection, new SemaphoreSlim(1, 1), fileRepoMock.Object);
    }

    [Fact]
    public async Task TotalReset_SavesBlankState()
    {
        var fileRepoMock = new Mock<IFileRepo>();
        var collection = CreateMonitorPingCollection();

        fileRepoMock.Setup(f => f.SaveStateStringJsonZAsync("ProcessorDataObj", It.IsAny<ProcessorDataObj>()))
            .ReturnsAsync("ok");
        fileRepoMock.Setup(f => f.SaveStateJsonZAsync("MonitorIPs", It.IsAny<List<MonitorIP>>()))
            .ReturnsAsync(Array.Empty<byte>());
        fileRepoMock.Setup(f => f.SaveStateJsonZAsync("PingParams", It.IsAny<PingParams>()))
            .ReturnsAsync(Array.Empty<byte>());

        var stateSetup = CreateStateSetup(fileRepoMock, collection);

        var result = await stateSetup.TotalReset();

        Assert.True(result);
        Assert.Empty(stateSetup.CurrentMonitorPingInfos);
        Assert.Empty(stateSetup.CurrentPingInfos);
        fileRepoMock.Verify(f => f.SaveStateStringJsonZAsync("ProcessorDataObj", It.IsAny<ProcessorDataObj>()), Times.Once());
        fileRepoMock.Verify(f => f.SaveStateJsonZAsync("MonitorIPs", It.IsAny<List<MonitorIP>>()), Times.Once());
        fileRepoMock.Verify(f => f.SaveStateJsonZAsync("PingParams", It.IsAny<PingParams>()), Times.Once());
    }

    [Fact]
    public async Task LoadFromState_PopulatesPropertiesAndCollections()
    {
        var fileRepoMock = new Mock<IFileRepo>();
        var collection = CreateMonitorPingCollection();

        var processorDataObj = new ProcessorDataObj
        {
            PiIDKey = 42,
            MonitorPingInfos = new List<MonitorPingInfo>
            {
                new MonitorPingInfo { MonitorIPID = 7, Enabled = true }
            },
            PingInfos = new List<PingInfo>
            {
                new PingInfo { ID = 99, MonitorPingInfoID = 7 }
            },
            RemoveMonitorPingInfoIDs = new List<int> { 3 },
            SwapMonitorPingInfos = new List<SwapMonitorPingInfo>
            {
                new SwapMonitorPingInfo { ID = 7 }
            },
            RemovePingInfos = new List<RemovePingInfo>
            {
                new RemovePingInfo { ID = 5 }
            }
        };

        var stateMonitorIps = new List<MonitorIP>
        {
            new MonitorIP { ID = 7, Enabled = true }
        };
        var statePingParams = new PingParams { Timeout = 1234 };

        fileRepoMock.Setup(f => f.GetStateStringJsonZAsync<ProcessorDataObj>("ProcessorDataObj"))
            .ReturnsAsync(processorDataObj);
        fileRepoMock.Setup(f => f.GetStateJsonZAsync<List<MonitorIP>>("MonitorIPs"))
            .ReturnsAsync(stateMonitorIps);
        fileRepoMock.Setup(f => f.GetStateJsonZAsync<PingParams>("PingParams"))
            .ReturnsAsync(statePingParams);

        var stateSetup = CreateStateSetup(fileRepoMock, collection);
        var removeIds = new List<int>();
        var swapInfos = new List<SwapMonitorPingInfo>();

        await stateSetup.LoadFromState(initNetConnects: false, piIDKey: 0, _removeMonitorPingInfoIDs: removeIds, _swapMonitorPingInfos: swapInfos, monitorPingCollection: collection);

        Assert.Equal((uint)42, stateSetup.LoadedPiIdKey);
        Assert.Single(stateSetup.CurrentMonitorPingInfos);
        Assert.Single(stateSetup.CurrentPingInfos);
        Assert.Contains(3, removeIds);
        Assert.Single(swapInfos);
        Assert.True(collection.RemovePingInfos.ContainsKey(5));
        fileRepoMock.Verify(f => f.GetStateJsonZAsync<List<MonitorIP>>("MonitorIPs"), Times.Once());
        fileRepoMock.Verify(f => f.GetStateJsonZAsync<PingParams>("PingParams"), Times.Once());
    }

    [Fact]
    public async Task MergeState_UsesStateWhenInitMissing()
    {
        var fileRepoMock = new Mock<IFileRepo>();
        var collection = CreateMonitorPingCollection();

        var processorDataObj = new ProcessorDataObj();
        var stateMonitorIps = new List<MonitorIP>
        {
            new MonitorIP { ID = 1, Enabled = true }
        };
        var statePingParams = new PingParams { Timeout = 999 };

        fileRepoMock.Setup(f => f.GetStateStringJsonZAsync<ProcessorDataObj>("ProcessorDataObj"))
            .ReturnsAsync(processorDataObj);
        fileRepoMock.Setup(f => f.GetStateJsonZAsync<List<MonitorIP>>("MonitorIPs"))
            .ReturnsAsync(stateMonitorIps);
        fileRepoMock.Setup(f => f.GetStateJsonZAsync<PingParams>("PingParams"))
            .ReturnsAsync(statePingParams);

        var stateSetup = CreateStateSetup(fileRepoMock, collection);
        await stateSetup.LoadFromState(true, 0, new List<int>(), new List<SwapMonitorPingInfo>(), collection);

        var initObj = new ProcessorInitObj();

        await stateSetup.MergeState(initObj);

        Assert.NotNull(initObj.MonitorIPs);
        Assert.Single(initObj.MonitorIPs);
        Assert.Equal(1, initObj.MonitorIPs[0].ID);
        Assert.NotNull(initObj.PingParams);
        Assert.Equal(999, initObj.PingParams.Timeout);
        fileRepoMock.Verify(f => f.SaveStateJsonZAsync("MonitorIPs", It.IsAny<List<MonitorIP>>()), Times.Never());
        fileRepoMock.Verify(f => f.SaveStateJsonZAsync("PingParams", It.IsAny<PingParams>()), Times.Never());
    }

    [Fact]
    public async Task MergeState_PersistsProvidedState()
    {
        var fileRepoMock = new Mock<IFileRepo>();
        var collection = CreateMonitorPingCollection();
        var stateSetup = CreateStateSetup(fileRepoMock, collection);

        var initObj = new ProcessorInitObj
        {
            MonitorIPs = new List<MonitorIP>
            {
                new MonitorIP { ID = 5 }
            },
            PingParams = new PingParams { Timeout = 321 }
        };

        fileRepoMock.Setup(f => f.SaveStateJsonZAsync("MonitorIPs", It.IsAny<List<MonitorIP>>()))
            .ReturnsAsync(Array.Empty<byte>());
        fileRepoMock.Setup(f => f.SaveStateJsonZAsync("PingParams", It.IsAny<PingParams>()))
            .ReturnsAsync(Array.Empty<byte>());

        await stateSetup.MergeState(initObj);

        fileRepoMock.Verify(f => f.SaveStateJsonZAsync("MonitorIPs", It.Is<List<MonitorIP>>(l => l.Count == 1 && l[0].ID == 5)), Times.Once());
        fileRepoMock.Verify(f => f.SaveStateJsonZAsync("PingParams", It.Is<PingParams>(p => p.Timeout == 321)), Times.Once());
    }
}
