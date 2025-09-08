using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP; // Modern FTP/FTPS client
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Utils;

namespace NetworkMonitor.Connection
{
    /// <summary>
    /// Tests FTP/FTPS connectivity using FluentFTP with schema-based argument parsing.
    /// Args:
    ///   --host <string>            (required)
    ///   --username <string>        (required)
    ///   --password <string>        (required)
    ///   --port <int>               (default: 21)
    ///   --ssl                      (flag: explicit TLS)
    ///   --passive                  (flag: passive data mode; default true)
    ///   --timeout_ms <int>         (default: 30000)
    ///   --path <string>            (optional remote path to list, default empty)
    /// </summary>
    public class FTPConnectionTesterCmdProcessor : CmdProcessor
    {
        private static readonly List<ArgSpec> _schema = new()
        {
            new() { Key = "host",       Required = true,  TypeHint = "value", Help = "FTP host (e.g., ftp.example.com)" },
            new() { Key = "username",   Required = true,  TypeHint = "value", Help = "FTP username" },
            new() { Key = "password",   Required = true,  TypeHint = "value", Help = "FTP password" },
            new() { Key = "port",       Required = false, TypeHint = "int",   DefaultValue = "21",     Help = "FTP port" },
            new() { Key = "ssl",        Required = false, IsFlag   = true,    DefaultValue = "false",  Help = "Use explicit FTPS (TLS)" },
            new() { Key = "passive",    Required = false, IsFlag   = true,    DefaultValue = "true",   Help = "Passive mode (PASV)" },
            new() { Key = "timeout_ms", Required = false, TypeHint = "int",   DefaultValue = "30000",  Help = "Timeout in milliseconds" },
            new() { Key = "path",       Required = false, TypeHint = "value", DefaultValue = "",       Help = "Remote path to list" },
        };

        public FTPConnectionTesterCmdProcessor(
            ILogger logger,
            ILocalCmdProcessorStates cmdProcessorStates,
            IRabbitRepo rabbitRepo,
            NetConnectConfig netConfig)
            : base(logger, cmdProcessorStates, rabbitRepo, netConfig) { }

        public override async Task<ResultObj> RunCommand(
            string arguments,
            CancellationToken cancellationToken,
            ProcessorScanDataObj? processorScanDataObj = null)
        {
            var result = new ResultObj();

            try
            {
                if (!_cmdProcessorStates.IsCmdAvailable)
                {
                    var m = $"{_cmdProcessorStates.CmdDisplayName} is not available on this agent.";
                    _logger.LogWarning(m);
                    result.Success = false;
                    result.Message = m;
                    return result;
                }

                // Parse & validate args
                var parse = CliArgParser.Parse(arguments, _schema, allowUnknown: false, fillDefaults: true);
                if (!parse.Success)
                {
                    result.Success = false;
                    result.Message = CliArgParser.BuildErrorMessage(_cmdProcessorStates.CmdDisplayName, parse, _schema);
                    return result;
                }

                var host      = parse.GetString("host");
                var username  = parse.GetString("username");
                var password  = parse.GetString("password");
                var port      = parse.GetInt("port", 21);
                var useSsl    = parse.GetBool("ssl", false);
                var passive   = parse.GetBool("passive", true);
                var timeoutMs = parse.GetInt("timeout_ms", 30000);
                var path      = parse.GetString("path", "");

                _logger.LogInformation("Testing FTP connection to {host}:{port} (ssl={ssl}, passive={passive}) with username {user}",
                    host, port, useSsl, passive, username);

                // Build FluentFTP configuration
                var config = new FtpConfig
                {
                    EncryptionMode = useSsl ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
                    DataConnectionType = passive ? FtpDataConnectionType.PASV : FtpDataConnectionType.PORT,
                    ConnectTimeout = timeoutMs,
                    ReadTimeout = timeoutMs,
                    DataConnectionConnectTimeout = timeoutMs,
                    DataConnectionReadTimeout = timeoutMs,
                    // Accept any certificate (useful for self-signed; tighten for production)
                    ValidateAnyCertificate = true
                };

                using var client = new AsyncFtpClient(host, new System.Net.NetworkCredential(username, password), port, config);

                // Cancel connect/list if token is signaled
                await client.Connect(cancellationToken);

                if (!client.IsConnected)
                {
                    result.Success = false;
                    result.Message = $"FTP connection failed to {host}:{port}.";
                    return result;
                }

                // Optionally list a directory to verify data channel works too
                int count = 0;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var items = await client.GetListing(path, cancellationToken);
                    count = items?.Length ?? 0;
                }

                result.Success = true;
                result.Message = string.IsNullOrWhiteSpace(path)
                    ? $"FTP connection successful to {host}:{port} (ssl={useSsl}, passive={passive})."
                    : $"FTP connection successful. Listed {count} item(s) at '{path}' on {host}:{port} (ssl={useSsl}, passive={passive}).";
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("FTP connection operation was canceled by the user.");
                return new ResultObj { Success = false, Message = "Operation canceled by user." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing FTP connection");
                return new ResultObj { Success = false, Message = $"Error testing FTP connection: {ex.Message}" };
            }
        }

        public override string GetCommandHelp()
        {
            // Compact help using the schema-generated usage
            var header = "Tests FTP/FTPS connectivity (FluentFTP) and optionally lists a remote directory.\n";
            var usage  = CliArgParser.BuildUsage(_cmdProcessorStates.CmdDisplayName, _schema);
            var examples = @"
Examples:
  --host ftp.example.com --username admin --password secret
  --host ftp.example.com --username admin --password secret --ssl --path /incoming
  --host 10.0.0.5 --username u --password p --port 21 --passive --timeout_ms 15000
";
            return $"{header}\n{usage}\n{examples}";
        }
    }
}

