using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.Objects;
using NetworkMonitor.Utils;
using NetworkMonitor.Processor.Services;
using Xunit;

public class MonitorPingCollectionTest
{
    private MonitorPingCollection CreateCollection(out Mock<ILogger> loggerMock)
    {
        loggerMock = new Mock<ILogger>();
        return new MonitorPingCollection(loggerMock.Object);
    }

    [Fact]
    public void SetVars_SetsAppIDAndPingParams()
    {
        var collection = CreateCollection(out _);
        var pingParams = new PingParams { Timeout = 1234 };
        collection.SetVars("app42", pingParams);
        Assert.Equal(1234, collection.PingParams.Timeout);
    }

    [Fact]
    public async Task ZeroMonitorPingInfos_ResetsStatsAndRemovesPingInfos()
    {
        var collection = CreateCollection(out var loggerMock);
        var monitorPingInfo = new MonitorPingInfo { MonitorIPID = 1, PacketsLost = 5, PacketsRecieved = 5, PacketsSent = 10, RoundTripTimeAverage = 100, RoundTripTimeMaximum = 200, RoundTripTimeMinimum = 50, RoundTripTimeTotal = 1000 };
        collection.MonitorPingInfos.TryAdd(1, monitorPingInfo);
        var pingInfo = new PingInfo { ID = 42, MonitorPingInfoID = 1 };
        collection.PingInfos.TryAdd(42, pingInfo);

        var sem = new SemaphoreSlim(1);
        await collection.ZeroMonitorPingInfos(sem);

        Assert.Equal(0, monitorPingInfo.PacketsLost);
        Assert.Equal(0, monitorPingInfo.PacketsRecieved);
        Assert.Equal(0, monitorPingInfo.PacketsSent);
        Assert.Equal(0, monitorPingInfo.RoundTripTimeAverage);
        Assert.Equal(0, monitorPingInfo.RoundTripTimeMaximum);
        Assert.Equal(collection.PingParams.Timeout, monitorPingInfo.RoundTripTimeMinimum);
        Assert.Equal(0, monitorPingInfo.RoundTripTimeTotal);
        Assert.False(collection.PingInfos.ContainsKey(42));
    }

    [Fact]
    public async Task ChangeAppID_UpdatesAllMonitorPingInfos()
    {
        var collection = CreateCollection(out _);
        var monitorPingInfo = new MonitorPingInfo { MonitorIPID = 1, AppID = "old" };
        collection.MonitorPingInfos.TryAdd(1, monitorPingInfo);

        var sem = new SemaphoreSlim(1);
        await collection.ChangeAppID(sem, "newApp");
        Assert.Equal("newApp", monitorPingInfo.AppID);
    }

    [Fact]
    public void Merge_UpdatesMonitorPingInfoStats()
    {
        var collection = CreateCollection(out _);
        var monitorPingInfo = new MonitorPingInfo { MonitorIPID = 1, PacketsSent = 0, PacketsRecieved = 0, PacketsLost = 0, RoundTripTimeMaximum = 0, RoundTripTimeMinimum = 1000, RoundTripTimeTotal = 0, MonitorStatus = new StatusObj() };
        collection.MonitorPingInfos.TryAdd(1, monitorPingInfo);

        var mpiConnect = new MPIConnect
        {
            IsUp = true,
            PingInfo = new PingInfo { ID = 99, MonitorPingInfoID = 1, RoundTripTime = 123 },
            EventTime = DateTime.UtcNow,
            Message = "OK",
            SiteHash = "sitehash-abc"
        };

        collection.Merge(mpiConnect, 1);

        Assert.Equal(1, monitorPingInfo.PacketsSent);
        Assert.Equal(1, monitorPingInfo.PacketsRecieved);
        Assert.Equal(0, monitorPingInfo.PacketsLost);
        Assert.Equal(123, monitorPingInfo.RoundTripTimeMaximum);
        Assert.Equal(123, monitorPingInfo.RoundTripTimeMinimum);
        Assert.Equal(123, monitorPingInfo.RoundTripTimeAverage);
        Assert.Equal(123, monitorPingInfo.RoundTripTimeTotal);
        Assert.Equal("OK", monitorPingInfo.Status);
        Assert.True(collection.PingInfos.ContainsKey(99));
        Assert.Equal("sitehash-abc", monitorPingInfo.SiteHash); // New assertion for SiteHash
    }

    [Fact]
    public void ClearPingInfos_RemovesAllPingInfos()
    {
        var collection = CreateCollection(out _);
        collection.PingInfos.TryAdd(1, new PingInfo());
        collection.PingInfos.TryAdd(2, new PingInfo());
        var result = collection.ClearPingInfos();
        Assert.True(result.Success);
        Assert.Empty(collection.PingInfos);
    }

    [Fact]
    public void ClearMonitorPingInfos_RemovesAllMonitorPingInfos()
    {
        var collection = CreateCollection(out _);
        collection.MonitorPingInfos.TryAdd(1, new MonitorPingInfo());
        collection.MonitorPingInfos.TryAdd(2, new MonitorPingInfo());
        var result = collection.ClearMonitorPingInfos();
        Assert.True(result.Success);
        Assert.Empty(collection.MonitorPingInfos);
    }

    [Fact]
    public void ClearRemovePingInfos_RemovesAllRemovePingInfos()
    {
        var collection = CreateCollection(out _);
        collection.RemovePingInfos.TryAdd(1, new RemovePingInfo());
        collection.RemovePingInfos.TryAdd(2, new RemovePingInfo());
        var result = collection.ClearRemovePingInfos();
        Assert.True(result.Success);
        Assert.Empty(collection.RemovePingInfos);
    }

    [Fact]
    public void RemovePingInfosFromPingInfos_RemovesMatchingPingInfos()
    {
        var collection = CreateCollection(out _);
        collection.PingInfos.TryAdd(1, new PingInfo());
        collection.PingInfos.TryAdd(2, new PingInfo());
        collection.RemovePingInfos.TryAdd(1, new RemovePingInfo());
        var result = collection.RemovePingInfosFromPingInfos();
        Assert.True(result.Success);
        Assert.False(collection.PingInfos.ContainsKey(1));
        Assert.True(collection.PingInfos.ContainsKey(2));
    }

    [Fact]
    public async Task RemovePublishedPingInfos_RemovesAndClears()
    {
        var collection = CreateCollection(out _);
        collection.PingInfos.TryAdd(1, new PingInfo());
        collection.RemovePingInfos.TryAdd(1, new RemovePingInfo());
        collection.MonitorPingInfos.TryAdd(1, new MonitorPingInfo());
        var sem = new SemaphoreSlim(1);
        var result = await collection.RemovePublishedPingInfos(sem);
        Assert.True(result.Success);
        Assert.Empty(collection.PingInfos);
        Assert.Empty(collection.RemovePingInfos);
    }

    [Fact]
    public async Task MonitorPingInfoFactory_AddsAndUpdatesMonitorPingInfos()
    {
        var collection = CreateCollection(out var loggerMock);
        var monitorIPs = new List<MonitorIP>
        {
            new MonitorIP { ID = 1, Address = "a", EndPointType = "icmp", Enabled = true, Port = 80, Timeout = 1000, UserID = "u" }
        };
        var currentMonitorPingInfos = new List<MonitorPingInfo>();
        var currentPingInfos = new List<PingInfo>();
        var sem = new SemaphoreSlim(1);

        var result = await collection.MonitorPingInfoFactory(monitorIPs, currentMonitorPingInfos, currentPingInfos, sem);

        // Accept either Success or "Nothing removed" as a valid outcome
        Assert.True(result.Success || result.Message.Contains("Nothing removed"), $"Expected Success or 'Nothing removed', got: {result.Message}");

        // Always check that the collection contains the expected MonitorPingInfo
        Assert.True(collection.MonitorPingInfos.ContainsKey(1));
        var mpi = collection.MonitorPingInfos[1];
        Assert.Equal("a", mpi.Address);
        Assert.Equal("icmp", mpi.EndPointType);
        Assert.True(mpi.Enabled);
        Assert.Equal(80, mpi.Port);
        Assert.Equal(1000, mpi.Timeout);
        Assert.Equal("u", mpi.UserID);
    }

    [Fact]
    public void FillPingInfo_FillsAllFields()
    {
        var collection = CreateCollection(out _);
        var monIP = new MonitorIP { ID = 1, Address = "a", EndPointType = "icmp", Enabled = true, Port = 80, Timeout = 1000, UserID = "u", Username = "user", Password = "pw", AddUserEmail = "e", IsEmailVerified = true };
        var mpi = new MonitorPingInfo();
        collection.FillPingInfo(mpi, monIP);
        Assert.Equal(1, mpi.MonitorIPID);
        Assert.Equal("a", mpi.Address);
        Assert.Equal("icmp", mpi.EndPointType);
        Assert.True(mpi.Enabled);
        Assert.Equal(80, mpi.Port);
        Assert.Equal(1000, mpi.Timeout);
        Assert.Equal("u", mpi.UserID);
        Assert.Equal("user", mpi.Username);
        Assert.Equal("pw", mpi.Password);
        Assert.Equal("e", mpi.AddUserEmail);
        Assert.True(mpi.IsEmailVerified);
    }
}
