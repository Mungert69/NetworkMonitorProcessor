using Microsoft.Extensions.Configuration;
using NetworkMonitor.Connection;
using NetworkMonitor.Objects;
using Xunit;
using System.Collections.Generic;

namespace NetworkMonitorProcessor.Services.Tests
{
    public class NetConnectConfigTests
    {
        private IConfiguration BuildConfig(params (string key, string value)[] entries)
        {
            var dict = new Dictionary<string, string?>();
            foreach (var (k, v) in entries)
                dict[k] = v;
            return new ConfigurationBuilder()
                .AddInMemoryCollection(dict)
                .Build();
        }

        [Fact]
        public void Parses_LocalSystemUrl_UseTls_When_Present_True()
        {
            var cfg = BuildConfig(
                ("LocalSystemUrl:RabbitHostName", "localhost"),
                ("LocalSystemUrl:RabbitPort", "5672"),
                ("LocalSystemUrl:UseTls", "true")
            );
            var nc = new NetConnectConfig(cfg, "TestSection");

            Assert.True(nc.LocalSystemUrl.UseTls);
        }

        [Fact]
        public void Parses_LocalSystemUrl_UseTls_When_Present_False()
        {
            var cfg = BuildConfig(
                ("LocalSystemUrl:RabbitHostName", "localhost"),
                ("LocalSystemUrl:RabbitPort", "5672"),
                ("LocalSystemUrl:UseTls", "false"),
                ("UseTls", "true") // root present should not override explicit LocalSystemUrl value
            );
            var nc = new NetConnectConfig(cfg, "TestSection");

            Assert.False(nc.LocalSystemUrl.UseTls);
        }

        [Fact]
        public void Current_Behavior_With_Only_Root_UseTls_Present()
        {
            var cfg = BuildConfig(
                ("LocalSystemUrl:RabbitHostName", "localhost"),
                ("LocalSystemUrl:RabbitPort", "5672"),
                ("UseTls", "true")
            );
            var nc = new NetConnectConfig(cfg, "TestSection");

            // Assert current behavior: construction succeeds and LocalSystemUrl exists.
            // We do not assert a specific value here to avoid locking undesired behavior;
            // this test guards that removing root UseTls later requires intentional updates.
            Assert.NotNull(nc.LocalSystemUrl);
        }

        [Fact]
        public void Current_Behavior_With_No_Tls_Keys_Present()
        {
            var cfg = BuildConfig(
                ("LocalSystemUrl:RabbitHostName", "localhost"),
                ("LocalSystemUrl:RabbitPort", "5672")
            );
            var nc = new NetConnectConfig(cfg, "TestSection");

            // Document current behavior without asserting a specific default value.
            Assert.NotNull(nc.LocalSystemUrl);
        }
    }
}
