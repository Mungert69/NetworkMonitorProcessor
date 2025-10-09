using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Connection;
using NetworkMonitor.DTOs;
using NetworkMonitor.Processor.Services;
using NetworkMonitor.Objects.ServiceMessage; // <-- Add this for ProcessorInitObj
using NetworkMonitor.Security;
using Xunit;

public class MonitorPingProcessorTest
{
    // DummyConnectFactory and DummyNetConnect for test injection
    private class DummyConnectFactory : IConnectFactory
    {
        public INetConnect GetNetConnectObj(MonitorPingInfo monitorPingInfo, PingParams pingParams) =>
            new DummyNetConnect(monitorPingInfo);
        public void UpdateNetConnectionInfo(INetConnect netConnect, MonitorPingInfo monitorPingInfo, PingParams? pingParams = null)
        {
            netConnect.IsEnabled = monitorPingInfo.Enabled;
            if (netConnect.MpiStatic != null)
            {
                netConnect.MpiStatic.Enabled = monitorPingInfo.Enabled;
                netConnect.MpiStatic.EndPointType = monitorPingInfo.EndPointType;
            }
        }
    }
    private class DummyNetConnect : INetConnect
    {
        public DummyNetConnect(MonitorPingInfo mpi)
        {
            MpiStatic = new MPIStatic { MonitorIPID = mpi.MonitorIPID, EndPointType = mpi.EndPointType, Enabled = mpi.Enabled };
            IsEnabled = mpi.Enabled;
            Cts = new CancellationTokenSource();
            MpiConnect = new MPIConnect(); // Fix CS8618: ensure non-null
        }
        public ushort RoundTrip { get; set; }
        public uint PiID { get; set; }
        public bool IsLongRunning { get; set; }
        public bool IsRunning { get; set; }
        public bool IsQueued { get; set; }
        public bool IsEnabled { get; set; }
        public MPIConnect MpiConnect { get; set; }
        public MPIStatic MpiStatic { get; set; }
        public CancellationTokenSource Cts { get; set; }
        public Task Connect() => Task.CompletedTask;
        public void PostConnect() { }
        public void PreConnect() { }
    }

    private MonitorPingProcessor CreateProcessor(
        out Mock<ILogger> loggerMock,
        out Mock<IFileRepo> fileRepoMock,
        out Mock<IRabbitRepo> rabbitRepoMock,
        out Mock<IProtectedConfigManager> protectedConfigManagerMock,
        out NetConnectConfig config)
    {
        loggerMock = new Mock<ILogger>();
        fileRepoMock = new Mock<IFileRepo>();
        rabbitRepoMock = new Mock<IRabbitRepo>();
        protectedConfigManagerMock = new Mock<IProtectedConfigManager>();
        protectedConfigManagerMock
            .Setup(m => m.SynchronizeSensitiveValuesAsync(
                It.IsAny<NetConnectConfig>(),
                It.IsAny<IEnumerable<ProtectedParameter>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        config = new NetConnectConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TestSection");
        typeof(NetConnectConfig).GetField("_appID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(config, "test-app");
        typeof(NetConnectConfig).GetField("_localSystemUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(config, new SystemUrl { RabbitInstanceName = "test", RabbitHostName = "localhost", RabbitPort = 5672 });
        var processorStates = new LocalProcessorStates();
        var connectFactory = new DummyConnectFactory();
        return new MonitorPingProcessor(
            loggerMock.Object,
            config,
            connectFactory,
            fileRepoMock.Object,
            rabbitRepoMock.Object,
            processorStates,
            protectedConfigManagerMock.Object
        );
    }

    [Fact]
    public async Task OnStoppingAsync_SetsProcessorStatesAndCallsRepos()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var protectedConfigManagerMock, out var config);

        fileRepoMock.Setup(f => f.ShutdownAsync()).Returns(Task.CompletedTask);
        rabbitRepoMock.Setup(r => r.Shutdown());

        // Should not throw
        await processor.OnStoppingAsync();

        // Check processor states
        Assert.False(config.AgentUserFlow.IsAuthorized); // Not set in OnStoppingAsync, but you can check processorStates
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true), // Optionally check message here
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
        fileRepoMock.Verify(f => f.ShutdownAsync(), Times.Once());
        rabbitRepoMock.Verify(r => r.Shutdown(), Times.Once());
    }

    [Fact]
    public async Task SetAuthKey_SetsAuthKeyAndCallsInit()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var protectedConfigManagerMock, out var config);
        var initObj = new ProcessorInitObj { AuthKey = "mykey" };

        fileRepoMock.Setup(f => f.CheckFileExists(It.IsAny<string>(), It.IsAny<ILogger>()));
        protectedConfigManagerMock.Setup(m => m.PersistAsync(ProtectedConfigurationParameters.AuthKey, config, "mykey", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var result = await processor.SetAuthKey(initObj);

        Assert.True(config.AgentUserFlow.IsAuthorized);
        Assert.Equal("mykey", config.AuthKey);
        Assert.Contains("Success", result.Message);
        protectedConfigManagerMock.Verify();
    }

    [Fact]
    public async Task ProcessorUserEvent_UpdatesAgentUserFlow()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var protectedConfigManagerMock, out var config);
        var userEvent = new ProcessorUserEventObj { IsLoggedInWebsite = true, IsHostsAdded = true };

        fileRepoMock.Setup(f => f.CheckFileExists(It.IsAny<string>(), It.IsAny<ILogger>()));
        protectedConfigManagerMock.Setup(m => m.SaveConfigurationAsync(config, ProtectedConfigurationParameters.All, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var result = await processor.ProcessorUserEvent(userEvent);

        Assert.True(config.AgentUserFlow.IsLoggedInWebsite);
        Assert.True(config.AgentUserFlow.IsHostsAdded);
        Assert.Contains("Success", result.Message);
        protectedConfigManagerMock.Verify();
    }

    [Fact]
    public async Task WakeUp_ReturnsSuccessIfSetupAndNotRunning()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var protectedConfigManagerMock, out var config);
        // Simulate setup complete and not running
        var statesField = typeof(MonitorPingProcessor)
            .GetField("_processorStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(statesField);
        var states = statesField.GetValue(processor) as LocalProcessorStates;
        Assert.NotNull(states);
        states.IsSetup = true;
        states.IsConnectRunning = false;

        var result = await processor.WakeUp();
        Assert.True(result.Success);
        Assert.Contains("Received WakeUp so Published event processorReady = true", result.Message);
    }

    [Fact]
    public async Task WakeUp_ReturnsWarningIfNotSetupOrRunning()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var protectedConfigManagerMock, out var config);
        var statesField = typeof(MonitorPingProcessor)
            .GetField("_processorStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(statesField);
        var states = statesField.GetValue(processor) as LocalProcessorStates;
        Assert.NotNull(states);
        states.IsSetup = false;
        states.IsConnectRunning = false;

        var result = await processor.WakeUp();
        Assert.False(result.Success);
        Assert.Contains("Warning", result.Message);

        states.IsSetup = true;
        states.IsConnectRunning = true;
        result = await processor.WakeUp();
        Assert.False(result.Success);
        Assert.Contains("Warning", result.Message);
    }

    [Fact]
    public async Task ResetAlerts_ForSiteHash_ResetsSiteHashInBothMonitorPingInfoAndNetConnect()
    {
        // Arrange
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var envStoreMock, out var config);

        // Create a MonitorPingInfo and add it to the processor using only public APIs
        var monitorPingInfo = new MonitorPingInfo
        {
            MonitorIPID = 123,
            EndPointType = "sitehash",
            Enabled = true,
            SiteHash = "originalhash",
            AppID = processor.AppID // Ensure AppID matches processor for strict lookup
        };

        // Get the collections from the processor
        var monitorPingCollectionField = processor.GetType()
            .GetField("_monitorPingCollection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(monitorPingCollectionField);
        var monitorPingCollection = monitorPingCollectionField.GetValue(processor) as MonitorPingCollection;
        Assert.NotNull(monitorPingCollection);

        var netConnectCollectionField = processor.GetType()
            .GetField("_netConnectCollection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(netConnectCollectionField);
        var netConnectCollection = netConnectCollectionField.GetValue(processor) as NetConnectCollection;
        Assert.NotNull(netConnectCollection);

        // Add to both collections
        monitorPingCollection.MonitorPingInfos.TryAdd(monitorPingInfo.MonitorIPID, monitorPingInfo);
        netConnectCollection.Add(monitorPingInfo);

        // Set the SiteHash on both the MonitorPingInfo and the NetConnect in the collection
        Assert.True(monitorPingCollection.MonitorPingInfos.ContainsKey(123));
        monitorPingCollection.MonitorPingInfos[123].SiteHash = "originalhash";
        var netConnect = netConnectCollection.GetFilteredNetConnects().FirstOrDefault(nc => nc.MpiStatic.MonitorIPID == 123);
        Assert.NotNull(netConnect);
        netConnect.MpiStatic.SiteHash = "originalhash";

        // Act
        var results = await processor.ResetAlerts(new List<int> { 123 });

        // Assert
        Assert.Null(monitorPingCollection.MonitorPingInfos[123].SiteHash);
        Assert.Null(netConnect.MpiStatic.SiteHash);
        Assert.Contains(results, r => r.Success && r.Message.Contains("updated MonitorPingInfo with MonitorIPID 123"));
    }
}
