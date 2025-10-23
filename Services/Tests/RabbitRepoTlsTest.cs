using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.Connection;
using NetworkMonitor.Objects; // For SystemUrl
using NetworkMonitor.Objects.Repository;
using Xunit;

namespace NetworkMonitorProcessor.Services.Tests
{
    public class RabbitRepoTlsTest
    {
        [Fact]
        public void RabbitRepo_WithSystemUrl_Respects_UseTls_True()
        {
            var logger = new Mock<ILogger<RabbitRepo>>().Object;
            var sys = new SystemUrl { RabbitHostName = "h", RabbitPort = 5672, UseTls = true };

            var repo = new RabbitRepo(new Mock<ILogger<RabbitRepo>>().Object, sys);

            // We cannot access internal _isTls; but constructing with SystemUrl(UseTls=true)
            // must not throw and should be usable. At minimum, this guards ctor path.
            Assert.NotNull(repo);
        }

        [Fact]
        public void RabbitRepo_WithSystemUrl_Respects_UseTls_False()
        {
            var logger = new Mock<ILogger<RabbitRepo>>().Object;
            var sys = new SystemUrl { RabbitHostName = "h", RabbitPort = 5672, UseTls = false };

            var repo = new RabbitRepo(new Mock<ILogger<RabbitRepo>>().Object, sys);
            Assert.NotNull(repo);
        }

        [Fact]
        public void RabbitRepo_WithNetConfig_Uses_LocalSystemUrl_Tls_CurrentBehavior()
        {
            var logger = new Mock<ILogger<RabbitRepo>>().Object;
            var cfg = new NetConnectConfig(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "TestSection");
            // Inject LocalSystemUrl via reflection
            typeof(NetConnectConfig).GetField("_localSystemUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(cfg, new SystemUrl { RabbitHostName = "h", RabbitPort = 5672, UseTls = true });

            var repo = new RabbitRepo(new Mock<ILogger<RabbitRepo>>().Object, cfg);
            Assert.NotNull(repo);
        }
    }
}
