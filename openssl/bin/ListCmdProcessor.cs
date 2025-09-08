using System; // Required base functionality
using System.Text; // For StringBuilder
using System.Collections.Generic; // For collections
using System.Diagnostics; // For Process execution
using System.Threading.Tasks; // For async/await
using Microsoft.Extensions.Logging; // For logging
using NetworkMonitor.Objects; // For application-specific objects
using NetworkMonitor.Objects.Repository; // For repository handling
using NetworkMonitor.Objects.ServiceMessage; // For service messaging
using NetworkMonitor.Connection; // For connection handling
using NetworkMonitor.Utils; // For CliArgParser (schema-based parsing)
using System.IO; // For file operations
using System.Threading; // For CancellationToken

namespace NetworkMonitor.Connection
{
    /// <summary>
    /// Lists directory contents using 'ls' with schema-based arg parsing.
    /// Args:
    ///   --path <string>            (default: '.')
    ///   --long                     (flag, adds -l)
    ///   --all                      (flag, adds -a)
    ///   --human                    (flag, adds -h)
    ///   --recursive                (flag, adds -R)
    ///   --timeout_ms <int>         (default: 10000)
    /// </summary>
    public class ListCmdProcessor : CmdProcessor
    {
        private const int DefaultTimeoutMs = 10_000;

        private static readonly List<ArgSpec> _schema = new()
        {
            new() { Key = "path",       Required = false, IsFlag = false, TypeHint = "value", DefaultValue = ".", Help = "Directory to list" },
            new() { Key = "long",       Required = false, IsFlag = true,  DefaultValue = "false", Help = "Long listing (-l)" },
            new() { Key = "all",        Required = false, IsFlag = true,  DefaultValue = "false", Help = "Include dot-files (-a)" },
            new() { Key = "human",      Required = false, IsFlag = true,  DefaultValue = "false", Help = "Human sizes (-h)" },
            new() { Key = "recursive",  Required = false, IsFlag = true,  DefaultValue = "false", Help = "Recursive (-R)" },
            new() { Key = "timeout_ms", Required = false, IsFlag = false, TypeHint = "int",   DefaultValue = DefaultTimeoutMs.ToString(), Help = "Process timeout (ms)" },
        };

        public ListCmdProcessor(ILogger logger, ILocalCmdProcessorStates cmdProcessorStates, IRabbitRepo rabbitRepo, NetConnectConfig netConfig)
            : base(logger, cmdProcessorStates, rabbitRepo, netConfig) { }

        public override async Task<ResultObj> RunCommand(string arguments, CancellationToken cancellationToken, ProcessorScanDataObj? processorScanDataObj = null)
        {
            var result = new ResultObj();
            try
            {
                // Availability check
                if (!_cmdProcessorStates.IsCmdAvailable)
                {
                    var msg = $"{_cmdProcessorStates.CmdDisplayName} is not available on this agent.";
                    LogErrorToFile(msg);
                    _logger.LogWarning(msg);
                    result.Success = false;
                    result.Message = msg;
                    return result;
                }

                // Parse args via schema
                var parse = CliArgParser.Parse(arguments, _schema, allowUnknown: false, fillDefaults: true);
                if (!parse.Success)
                {
                    var err = CliArgParser.BuildErrorMessage(_cmdProcessorStates.CmdDisplayName, parse, _schema);
                    _logger.LogWarning("Invalid args: {msg}", parse.Message);
                    result.Success = false;
                    result.Message = err;
                    return result;
                }

                var path      = parse.GetString("path", ".");
                var timeoutMs = parse.GetInt("timeout_ms", DefaultTimeoutMs);

                var opts = new StringBuilder();
                if (parse.GetBool("long", false))      opts.Append(" -l");
                if (parse.GetBool("all", false))       opts.Append(" -a");
                if (parse.GetBool("human", false))     opts.Append(" -h");
                if (parse.GetBool("recursive", false)) opts.Append(" -R");

                // Build process
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ls",
                        Arguments = $"{opts} -- {path}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = string.IsNullOrWhiteSpace(_rootFolder) ? Environment.CurrentDirectory : _rootFolder
                    },
                    EnableRaisingEvents = true
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder  = new StringBuilder();

                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) errorBuilder.AppendLine(e.Data); };

                // Start process
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Kill on cancellation
                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            _logger.LogInformation("Cancellation requested, killing 'ls' process...");
#if NET6_0_OR_GREATER
                            process.Kill(entireProcessTree: true);
#else
                            process.Kill();
#endif
                        }
                    }
                    catch { /* ignore */ }
                }))
                {
                    // Wait with timeout
                    var exited = await Task.Run(() => process.WaitForExit(timeoutMs), cancellationToken);
                    if (!exited)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        result.Success = false;
                        result.Message = $"ls timed out after {timeoutMs}ms";
                        return result;
                    }
                }

                // Compose output
                var stdOut = outputBuilder.ToString();
                var stdErr = errorBuilder.ToString();
                var combined = string.IsNullOrWhiteSpace(stdErr)
                    ? stdOut
                    : $"[stderr]\n{stdErr}\n[stdout]\n{stdOut}";

                result.Success = process.ExitCode == 0 && string.IsNullOrWhiteSpace(stdErr);
                result.Message = combined;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Message = "ls canceled or timed out.";
                return result;
            }
            catch (Exception e)
            {
                var errorMessage = $"Error in RunCommand: {e.Message}";
                LogErrorToFile(errorMessage);
                _logger.LogError(e, "ListCmdProcessor error");
                result.Success = false;
                result.Message = errorMessage;
                return result;
            }
        }

        public override string GetCommandHelp()
        {
            var header = "ListCmdProcessor — list directory contents using 'ls'.\n";
            var usage  = CliArgParser.BuildUsage(_cmdProcessorStates.CmdDisplayName, _schema);
            var examples = @"
Examples:
  --path .
  --path /var/log --long --human
  --path . --all --recursive --timeout_ms 5000
";
            return $"{header}\n{usage}\n{examples}";
        }

        private void LogErrorToFile(string errorMessage)
        {
            try
            {
                var baseDir = string.IsNullOrWhiteSpace(_rootFolder) ? Environment.CurrentDirectory : _rootFolder;
                var logDirectory = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(logDirectory);

                var logFilePath = Path.Combine(logDirectory, $"{GetType().Name}.log");
                var logMessage = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {errorMessage}{Environment.NewLine}";

                File.AppendAllText(logFilePath, logMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to write to error log file: {msg}", ex.Message);
            }
        }
    }
}

