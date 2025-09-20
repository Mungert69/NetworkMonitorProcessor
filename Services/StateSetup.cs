using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects.Repository;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor.Processor.Services
{
    public class StateSetup
    {
        private readonly ILogger _logger;
        private readonly MonitorPingCollection _monitorPingCollection;
        private readonly IFileRepo _fileRepo;

        private List<MonitorPingInfo> _currentMonitorPingInfos = new List<MonitorPingInfo>();
        private List<PingInfo> _currentPingInfos = new List<PingInfo>();
        private List<MonitorIP>? _stateMonitorIPs;
        private PingParams? _statePingParams;

        // expose current working sets
        public List<MonitorPingInfo> CurrentMonitorPingInfos { get => _currentMonitorPingInfos; set => _currentMonitorPingInfos = value; }
        public List<PingInfo> CurrentPingInfos { get => _currentPingInfos; set => _currentPingInfos = value; }

        // outputs from LoadFromState without changing its signature
        public uint LoadedPiIdKey { get; private set; } = 1;
        public List<int> LoadedRemoveMonitorPingInfoIDs { get; private set; } = new();
        public List<SwapMonitorPingInfo> LoadedSwapMonitorPingInfos { get; private set; } = new();

        public StateSetup(ILogger logger, MonitorPingCollection monitorPingCollection, SemaphoreSlim lockObj, IFileRepo fileRepo)
        {
            _logger = logger;
            _fileRepo = fileRepo;
            _monitorPingCollection = monitorPingCollection;

            _fileRepo.CheckFileExistsWithCreateStringJsonZObject("ProcessorDataObj", new ProcessorDataObj(), logger);
            _fileRepo.CheckFileExistsWithCreateJsonZObject("MonitorIPs", new List<MonitorIP>(), logger);
            _fileRepo.CheckFileExistsWithCreateJsonZObject("PingParams", new PingParams { Timeout = 59000, AlertThreshold = 4, HostLimit = 10 }, logger);
        }

        public async Task<bool> TotalReset()
        {
            var initNetConnects = false;
            CurrentMonitorPingInfos = new List<MonitorPingInfo>();
            CurrentPingInfos = new List<PingInfo>();

            var processorDataObj = new ProcessorDataObj
            {
                MonitorPingInfos = new List<MonitorPingInfo>(),
                RemoveMonitorPingInfoIDs = new List<int>(),
                SwapMonitorPingInfos = new List<SwapMonitorPingInfo>(),
                RemovePingInfos = new List<RemovePingInfo>(),
                PingInfos = new List<PingInfo>(),
                PiIDKey = 1
            };

            try
            {
                await _fileRepo.SaveStateStringJsonZAsync("ProcessorDataObj", processorDataObj);
                _logger.LogInformation(" State Setup : Success : Resetting Processor ProcessorDataObj in statestore");

                await _fileRepo.SaveStateJsonZAsync("MonitorIPs", new List<MonitorIP>());
                _logger.LogInformation(" State Setup : Success : Reset Processor MonitorIPs in statestore ");

                await _fileRepo.SaveStateJsonZAsync("PingParams", new PingParams());
                _logger.LogInformation(" State Setup : Success : Reset Processor PingPamrms in statestore ");

                initNetConnects = true;
            }
            catch (Exception e)
            {
                _logger.LogError(" State Setup : Error : Could not reset Processor Objects to statestore. Error was : {Error}", e.Message);
            }

            return initNetConnects;
        }

        // Signature unchanged (no API change)
        public async Task LoadFromState(bool initNetConnects, uint piIDKey, List<int> _removeMonitorPingInfoIDs, List<SwapMonitorPingInfo> _swapMonitorPingInfos, MonitorPingCollection monitorPingCollection)
        {
            initNetConnects = true; // original behavior preserved
            string infoLog = " Starting Load From State ";

            try
            {
                var processorDataObj = await _fileRepo.GetStateStringJsonZAsync<ProcessorDataObj>("ProcessorDataObj");

                if (processorDataObj != null)
                {
                    // capture outputs to properties (caller reads these)
                    LoadedPiIdKey = processorDataObj.PiIDKey;
                    var removeIds = processorDataObj.RemoveMonitorPingInfoIDs ?? new List<int>();
                    var swapInfos = processorDataObj.SwapMonitorPingInfos ?? new List<SwapMonitorPingInfo>();

                    // also mutate the passed-in lists so callers that rely on references see updates
                    if (_removeMonitorPingInfoIDs != null)
                    {
                        _removeMonitorPingInfoIDs.Clear();
                        _removeMonitorPingInfoIDs.AddRange(removeIds);
                    }
                    if (_swapMonitorPingInfos != null)
                    {
                        _swapMonitorPingInfos.Clear();
                        _swapMonitorPingInfos.AddRange(swapInfos);
                    }

                    // track working sets
                    CurrentPingInfos = processorDataObj.PingInfos ?? new List<PingInfo>();
                    CurrentMonitorPingInfos = processorDataObj.MonitorPingInfos ?? new List<MonitorPingInfo>();

                    foreach (var f in processorDataObj.RemovePingInfos ?? Enumerable.Empty<RemovePingInfo>())
                    {
                        _monitorPingCollection.RemovePingInfos.TryAdd(f.ID, f);
                    }

                    infoLog += $" State Setup : Got PiIDKey={LoadedPiIdKey} and loaded ProcessorDataObj from state . ";
                }
                else
                {
                    infoLog += " Error : ProcessorDataObj null from state .";
                    LoadedPiIdKey = 1;
                    LoadedRemoveMonitorPingInfoIDs = new();
                    LoadedSwapMonitorPingInfos = new();
                }

                if (_removeMonitorPingInfoIDs == null) _removeMonitorPingInfoIDs = new List<int>();
                if (_swapMonitorPingInfos == null) _swapMonitorPingInfos = new List<SwapMonitorPingInfo>();

                var firstEnabledPingInfo = CurrentMonitorPingInfos.FirstOrDefault(w => w.Enabled);
                if (firstEnabledPingInfo != null)
                {
                    var cnt = CurrentPingInfos.Count(w => w.MonitorPingInfoID == firstEnabledPingInfo.MonitorIPID);
                    infoLog += $" Success : Building MonitorPingInfos from ProcessorDataObj in statestore. First Enabled PingInfo Count = {cnt} ";
                }
                else
                {
                    _logger.LogWarning(" State Setup : Warning : MonitorPingInfos from ProcessorDataObj in statestore contains no Data .");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(" Logged so far : {Info} : State Setup :Error : Building MonitorPingInfos from ProcessorDataObj in statestore . Error was : {Error}", infoLog, e);
                _currentMonitorPingInfos = new List<MonitorPingInfo>();
                _currentPingInfos = new List<PingInfo>();
                LoadedPiIdKey = 1;
                LoadedRemoveMonitorPingInfoIDs = new();
                LoadedSwapMonitorPingInfos = new();
            }

            try
            {
                _stateMonitorIPs = await _fileRepo.GetStateJsonZAsync<List<MonitorIP>>("MonitorIPs");
                if (_stateMonitorIPs != null) infoLog += $" Got MonitorIPS from statestore count ={_stateMonitorIPs.Count} . ";
            }
            catch (Exception e)
            {
                _logger.LogWarning(" State Setup :Warning : Could not get MonitorIPs from statestore. Error was : {Error}", e.Message);
            }

            try
            {
                _statePingParams = await _fileRepo.GetStateJsonZAsync<PingParams>("PingParams");
                infoLog += " State Setup :Got PingParams from statestore . ";
            }
            catch (Exception e)
            {
                _logger.LogWarning(" State Setup :Warning : Could not get PingParms from statestore. Error was : {Error}", e.Message);
            }

            _logger.LogInformation(infoLog);
        }

        public async Task MergeState(ProcessorInitObj initObj)
        {
            if (initObj.MonitorIPs == null || initObj.MonitorIPs.Count == 0)
            {
                _logger.LogWarning(" State Setup : Warning : There are No MonitorIPs using statestore");
                if (_stateMonitorIPs != null) initObj.MonitorIPs = _stateMonitorIPs;
                if (_stateMonitorIPs == null || _stateMonitorIPs.Count == 0)
                {
                    initObj.MonitorIPs = new List<MonitorIP>();
                    _logger.LogError(" State Setup :Error : There are No MonitorIPs in statestore");
                }
            }
            else
            {
                try
                {
                    await _fileRepo.SaveStateJsonZAsync("MonitorIPs", initObj.MonitorIPs);
                }
                catch (Exception e)
                {
                    _logger.LogError(" State Setup : Error : Unable to Save MonitorIPs to statestore. Error was : {Error}", e.Message);
                }
            }

            if (initObj.PingParams == null)
            {
                if (_statePingParams == null)
                {
                    _logger.LogError(" State Setup : Error : There are No PingParams in statestore");
                }
                else
                {
                    initObj.PingParams = _statePingParams;
                    _logger.LogWarning(" State Setup : Warning : There are No PingParams using statestore");
                }
            }
            else
            {
                try
                {
                    await _fileRepo.SaveStateJsonZAsync("PingParams", initObj.PingParams);
                }
                catch (Exception e)
                {
                    _logger.LogError(" State Setup : Error : Unable to Save PingParams to statestore. Error was : {Error}", e.Message);
                }
            }
        }
    }
}
