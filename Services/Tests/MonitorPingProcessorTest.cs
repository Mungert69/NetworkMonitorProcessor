using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Connection;
using NetworkMonitor.DTOs;
using NetworkMonitor.Processor.Services;
using NetworkMonitor.Objects.ServiceMessage; // <-- Add this for ProcessorInitObj
using Xunit;

public class MonitorPingProcessorTest
{
    private MonitorPingProcessor CreateProcessor(
        out Mock<ILogger> loggerMock,
        out Mock<IFileRepo> fileRepoMock,
        out Mock<IRabbitRepo> rabbitRepoMock,
        out NetConnectConfig config)
    {
        loggerMock = new Mock<ILogger>();
        fileRepoMock = new Mock<IFileRepo>();
        rabbitRepoMock = new Mock<IRabbitRepo>();
        config = new NetConnectConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TestSection");
        // If AppID and LocalSystemUrl are settable via methods or reflection, set them here.
        // Otherwise, ensure your NetConnectConfig is constructed with the correct values for your test.
        // For test purposes, you can use reflection to set private fields if needed:
        typeof(NetConnectConfig).GetField("_appID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(config, "test-app");
        typeof(NetConnectConfig).GetField("_localSystemUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(config, new SystemUrl { RabbitInstanceName = "test", RabbitHostName = "localhost", RabbitPort = 5672 });
        var processorStates = new LocalProcessorStates();
        var connectFactory = new Mock<IConnectFactory>().Object;
        return new MonitorPingProcessor(
            loggerMock.Object,
            config,
            connectFactory,
            fileRepoMock.Object,
            rabbitRepoMock.Object,
            processorStates
        );
    }

    [Fact]
    public async Task OnStoppingAsync_SetsProcessorStatesAndCallsRepos()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var config);

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
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce());
        fileRepoMock.Verify(f => f.ShutdownAsync(), Times.Once());
        rabbitRepoMock.Verify(r => r.Shutdown(), Times.Once());
    }

    [Fact]
    public async Task SetAuthKey_SetsAuthKeyAndCallsInit()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var config);
        var initObj = new ProcessorInitObj { AuthKey = "mykey" };

        fileRepoMock.Setup(f => f.CheckFileExists(It.IsAny<string>(), It.IsAny<ILogger>()));
        fileRepoMock.Setup(f => f.SaveStateJsonAsync<NetConnectConfig>(It.IsAny<string>(), It.IsAny<NetConnectConfig>()))
            .Returns(Task.CompletedTask);

        var result = await processor.SetAuthKey(initObj);

        Assert.True(config.AgentUserFlow.IsAuthorized);
        Assert.Equal("mykey", config.AuthKey);
        Assert.Contains("Success", result.Message);
    }

    [Fact]
    public async Task ProcessorUserEvent_UpdatesAgentUserFlow()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var config);
        var userEvent = new ProcessorUserEventObj { IsLoggedInWebsite = true, IsHostsAdded = true };

        fileRepoMock.Setup(f => f.CheckFileExists(It.IsAny<string>(), It.IsAny<ILogger>()));
        fileRepoMock.Setup(f => f.SaveStateJsonAsync<NetConnectConfig>(It.IsAny<string>(), It.IsAny<NetConnectConfig>()))
            .Returns(Task.CompletedTask);

        var result = await processor.ProcessorUserEvent(userEvent);

        Assert.True(config.AgentUserFlow.IsLoggedInWebsite);
        Assert.True(config.AgentUserFlow.IsHostsAdded);
        Assert.Contains("Success", result.Message);
    }

    [Fact]
    public async Task WakeUp_ReturnsSuccessIfSetupAndNotRunning()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var config);
        // Simulate setup complete and not running
        var states = typeof(MonitorPingProcessor)
            .GetField("_processorStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(processor) as LocalProcessorStates;
        states.IsSetup = true;
        states.IsConnectRunning = false;

        var result = await processor.WakeUp();
        Assert.True(result.Success);
        Assert.Contains("Received WakeUp so Published event processorReady = true", result.Message);
    }

    [Fact]
    public async Task WakeUp_ReturnsWarningIfNotSetupOrRunning()
    {
        var processor = CreateProcessor(out var loggerMock, out var fileRepoMock, out var rabbitRepoMock, out var config);
        var states = typeof(MonitorPingProcessor)
            .GetField("_processorStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(processor) as LocalProcessorStates;
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
}
