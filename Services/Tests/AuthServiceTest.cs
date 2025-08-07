using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Processor.Services;
using NetworkMonitor.Connection;
using Xunit;

public class AuthServiceTest
{
    private class DummyProcessorStates : LocalProcessorStates
    {
        public DummyProcessorStates() { IsSetup = true; }
    }

    private NetConnectConfig GetConfig()
    {
        var config = new NetConnectConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TestSection");
        config.ClientId = "test-client";
        config.BaseFusionAuthURL = "https://auth.example.com";
        config.LoadServer = "load.example.com";
        // Set LocalSystemUrl via reflection since it is read-only
        typeof(NetConnectConfig).GetField("_localSystemUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(config, new SystemUrl());
        return config;
    }

    private AuthService CreateAuthService(
        out Mock<ILogger> loggerMock,
        out Mock<IRabbitRepo> rabbitRepoMock,
        out NetConnectConfig config,
        LocalProcessorStates? processorStates = null,
        HttpMessageHandler? handler = null)
    {
        loggerMock = new Mock<ILogger>();
        rabbitRepoMock = new Mock<IRabbitRepo>();
        config = GetConfig();
        return new AuthService(
            loggerMock.Object,
            config,
            rabbitRepoMock.Object,
            processorStates ?? new DummyProcessorStates()
        );
    }

    [Fact]
    public async Task InitializeAsync_ReturnsError_IfNotSetup()
    {
        var loggerMock = new Mock<ILogger>();
        var rabbitRepoMock = new Mock<IRabbitRepo>();
        var config = GetConfig();
        var processorStates = new LocalProcessorStates { IsSetup = false };
        var authService = new AuthService(loggerMock.Object, config, rabbitRepoMock.Object, processorStates);

        var result = await authService.InitializeAsync();
        Assert.False(result.Success);
        Assert.Contains("Error: Please wait for setup to complete", result.Message);
    }

    [Fact]
    public async Task InitializeAsync_ReturnsError_IfNoClientId()
    {
        var authService = CreateAuthService(out var loggerMock, out var rabbitRepoMock, out var config);
        config.ClientId = "";

        var result = await authService.InitializeAsync();
        Assert.False(result.Success);
        Assert.Contains("Error: No BaseFusionAuthUrl set", result.Message);
    }

    [Fact]
    public async Task InitializeAsync_ReturnsError_IfDiscoveryFails()
    {
        var authService = CreateAuthService(out var loggerMock, out var rabbitRepoMock, out var config);
        config.BaseFusionAuthURL = "https://invalid.example.com";
        // This will fail because the URL is invalid and no server is running

        await Assert.ThrowsAsync<HttpRequestException>(async () => await authService.InitializeAsync());
    }

    [Fact]
    public async Task SendAuthRequestAsync_ReturnsError_IfNoClientId()
    {
        var authService = CreateAuthService(out var loggerMock, out var rabbitRepoMock, out var config);
        config.ClientId = "";

        var result = await authService.SendAuthRequestAsync();
        Assert.False(result.Success);
        Assert.Contains("Error: No ClientId set", result.Message);
    }

    [Fact]
    public async Task SendAuthRequestAsync_ReturnsError_IfEndpointNotSet()
    {
        var authService = CreateAuthService(out var loggerMock, out var rabbitRepoMock, out var config);
        // _deviceAuthEndpoint is empty by default, so the request will fail

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await authService.SendAuthRequestAsync());
    }

    [Fact]
    public async Task PollForTokenAsync_ReturnsError_IfTimeout()
    {
        var authService = CreateAuthService(out var loggerMock, out var rabbitRepoMock, out var config);
        // Set _deviceCode and _tokenEndpoint to dummy values via reflection
        typeof(AuthService).GetField("_deviceCode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(authService, "dummy");
        typeof(AuthService).GetField("_tokenEndpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(authService, "https://localhost/token");

        // Use a cancellation token that cancels immediately to simulate timeout
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await authService.PollForTokenAsync(cts.Token);
        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}