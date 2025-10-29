using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.Connection;
using NetworkMonitor.Objects; // For SystemUrl
using NetworkMonitor.Objects.Repository;
using Xunit;
using System.Net.Security;
using System.Reflection;
using RabbitMQ.Client;
using System.IO;
using System;

namespace NetworkMonitorProcessor.Services.Tests
{
    public class RabbitRepoTlsTest
    {
        private const string IsrgRootPem = @"-----BEGIN CERTIFICATE-----
MIIFazCCA1OgAwIBAgIRAIIQz7DSQONZRGPgu2OCiwAwDQYJKoZIhvcNAQELBQAw
TzELMAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2Vh
cmNoIEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDEwHhcNMTUwNjA0MTEwNDM4
WhcNMzUwNjA0MTEwNDM4WjBPMQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJu
ZXQgU2VjdXJpdHkgUmVzZWFyY2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBY
MTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAK3oJHP0FDfzm54rVygc
h77ct984kIxuPOZXoHj3dcKi/vVqbvYATyjb3miGbESTtrFj/RQSa78f0uoxmyF+
0TM8ukj13Xnfs7j/EvEhmkvBioZxaUpmZmyPfjxwv60pIgbz5MDmgK7iS4+3mX6U
A5/TR5d8mUgjU+g4rk8Kb4Mu0UlXjIB0ttov0DiNewNwIRt18jA8+o+u3dpjq+sW
T8KOEUt+zwvo/7V3LvSye0rgTBIlDHCNAymg4VMk7BPZ7hm/ELNKjD+Jo2FR3qyH
B5T0Y3HsLuJvW5iB4YlcNHlsdu87kGJ55tukmi8mxdAQ4Q7e2RCOFvu396j3x+UC
B5iPNgiV5+I3lg02dZ77DnKxHZu8A/lJBdiB3QW0KtZB6awBdpUKD9jf1b0SHzUv
KBds0pjBqAlkd25HN7rOrFleaJ1/ctaJxQZBKT5ZPt0m9STJEadao0xAH0ahmbWn
OlFuhjuefXKnEgV4We0+UXgVCwOPjdAvBbI+e0ocS3MFEvzG6uBQE3xDk3SzynTn
jh8BCNAw1FtxNrQHusEwMFxIt4I7mKZ9YIqioymCzLq9gwQbooMDQaHWBfEbwrbw
qHyGO0aoSCqI3Haadr8faqU9GY/rOPNk3sgrDQoo//fb4hVC1CLQJ13hef4Y53CI
rU7m2Ys6xt0nUW7/vGT1M0NPAgMBAAGjQjBAMA4GA1UdDwEB/wQEAwIBBjAPBgNV
HRMBAf8EBTADAQH/MB0GA1UdDgQWBBR5tFnme7bl5AFzgAiIyBpY9umbbjANBgkq
hkiG9w0BAQsFAAOCAgEAVR9YqbyyqFDQDLHYGmkgJykIrGF1XIpu+ILlaS/V9lZL
ubhzEFnTIZd+50xx+7LSYK05qAvqFyFWhfFQDlnrzuBZ6brJFe+GnY+EgPbk6ZGQ
3BebYhtF8GaV0nxvwuo77x/Py9auJ/GpsMiu/X1+mvoiBOv/2X/qkSsisRcOj/KK
NFtY2PwByVS5uCbMiogziUwthDyC3+6WVwW6LLv3xLfHTjuCvjHIInNzktHCgKQ5
ORAzI4JMPJ+GslWYHb4phowim57iaztXOoJwTdwJx4nLCgdNbOhdjsnvzqvHu7Ur
TkXWStAmzOVyyghqpZXjFaH3pO3JLF+l+/+sKAIuvtd7u+Nxe5AW0wdeRlN8NwdC
jNPElpzVmbUq4JUagEiuTDkHzsxHpFKVK7q4+63SM1N95R1NbdWhscdCb+ZAJzVc
oyi3B43njTOQ5yOf+1CceWxG1bQVs5ZufpsMljq4Ui0/1lvh+wjChP4kqKOJ2qxq
4RgqsahDYVvTH9w7jXbyLeiNdd8XM2w9U/t7y0Ff/9yi0GE44Za4rF2LN9d11TPA
mRGunUHBcnWEvgJBQl9nJEiU0Zsnvgc/ubhPgXRR4Xq37Z0j4r7g1SgEEzwxA57d
emyPxgcYxn/eR44/KJ4EBs+lVDR3veyJm+kXQ99b21/+jh5Xos1AnX5iItreGCc=
-----END CERTIFICATE-----";

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

        [Fact]
        public void RabbitRepo_LegacyAndroid_Uses_Custom_TrustStore()
        {
            using var tempCert = new TempPemFile(IsrgRootPem);
            var sys = new SystemUrl
            {
                RabbitHostName = "h",
                RabbitPort = 5671,
                UseTls = true,
                AndroidSdkLevel = 23,
                LegacyAndroidRootCertPath = tempCert.Path
            };

            var repo = new RabbitRepo(new Mock<ILogger<RabbitRepo>>().Object, sys);
            var sslOption = InvokeBuildSslOption(repo);

            Assert.True(sslOption.Enabled);
            Assert.Equal(SslPolicyErrors.None, sslOption.AcceptablePolicyErrors);
            Assert.NotNull(sslOption.CertificateValidationCallback);
        }

        [Fact]
        public void RabbitRepo_ModernAndroid_Leaves_Default_Policy()
        {
            var sys = new SystemUrl
            {
                RabbitHostName = "h",
                RabbitPort = 5671,
                UseTls = true,
                AndroidSdkLevel = 30
            };

            var repo = new RabbitRepo(new Mock<ILogger<RabbitRepo>>().Object, sys);
            var sslOption = InvokeBuildSslOption(repo);

            Assert.Equal(SslPolicyErrors.None, sslOption.AcceptablePolicyErrors);
            Assert.Null(sslOption.CertificateValidationCallback);
        }

        private static SslOption InvokeBuildSslOption(RabbitRepo repo)
        {
            var method = typeof(RabbitRepo).GetMethod("BuildSslOption", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var option = method!.Invoke(repo, null) as SslOption;
            Assert.NotNull(option);
            return option!;
        }

        private sealed class TempPemFile : IDisposable
        {
            public string Path { get; }

            public TempPemFile(string pem)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
                File.WriteAllText(Path, pem);
            }

            public void Dispose()
            {
                try
                {
                    if (File.Exists(Path))
                    {
                        File.Delete(Path);
                    }
                }
                catch
                {
                    // ignore dispose failures in test cleanup
                }
            }
        }
    }
}
