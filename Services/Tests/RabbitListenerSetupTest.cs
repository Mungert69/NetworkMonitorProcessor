using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.Connection;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Processor.Services;
using Xunit;
using System.Threading.Tasks;
using NetworkMonitor.Objects;

namespace NetworkMonitorProcessor.Services.Tests
{
    public class RabbitListenerSetupTest
    {
        [Fact]
        public async Task RabbitListener_Constructs_With_Current_Ctor_Path()
        {
            var logger = new Mock<ILogger>().Object;
            var monitor = new Mock<IMonitorPingProcessor>().Object;
            var cmdProvider = new Mock<ICmdProcessorProvider>().Object;
            var states = new LocalProcessorStates();

            // Build minimal NetConnectConfig
            var cfg = new NetConnectConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TestSection");
            typeof(NetConnectConfig).GetField("_localSystemUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(cfg, new SystemUrl { RabbitHostName = "localhost", RabbitPort = 5672, UseTls = false, RabbitInstanceName = "test" });

            var listener = new RabbitListener(monitor, logger, cfg, states, cmdProvider);

            // We don't call Setup() to avoid needing a live RabbitMQ.
            // Construction should succeed under current wiring.
            Assert.NotNull(listener);
        }
    }
}
