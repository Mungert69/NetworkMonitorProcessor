using System;
using System.Collections.Generic;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Utils;
using NetworkMonitor.Utils.Helpers;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.DTOs;
using System.Linq;
using NetworkMonitor.Connection;
using NetworkMonitor.Objects.Factory;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using NetworkMonitor.Security;

namespace NetworkMonitor.Processor.Services
{
    public class MonitorPingProcessor : IMonitorPingProcessor, IDisposable
    {
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
        private readonly ILogger _logger;
        private readonly NetConnectConfig _netConfig;
        private LocalProcessorStates _processorStates = new LocalProcessorStates();
        private List<int> _removeMonitorPingInfoIDs = new List<int>();
        private List<SwapMonitorPingInfo> _swapMonitorPingInfos = new List<SwapMonitorPingInfo>();
        private readonly NetConnectCollection _netConnectCollection;
        private readonly MonitorPingCollection _monitorPingCollection;
        private IMonitorPingInfoView? _monitorPingInfoView;
        private ConcurrentDictionary<string, List<UpdateMonitorIP>> _monitorIPQueueDic = new ConcurrentDictionary<string, List<UpdateMonitorIP>>();
        private uint _piIDKey = 1;
        private readonly IRabbitRepo _rabbitRepo;
        private readonly IFileRepo _fileRepo;
        private readonly IProtectedConfigManager _protectedConfigManager;
        private readonly IReadOnlyList<ProtectedParameter> _protectedParameters;

        public string AppID => _netConfig.AppID;

        public MonitorPingProcessor(
            ILogger logger,
            NetConnectConfig netConfig,
            IConnectFactory connectFactory,
            IFileRepo fileRepo,
            IRabbitRepo rabbitRepo,
            LocalProcessorStates processorStates,
            IProtectedConfigManager protectedConfigManager,
            IMonitorPingInfoView? monitorPingInfoView = null,
            IReadOnlyList<ProtectedParameter>? protectedParameters = null)
        {
            _logger = logger;
            _fileRepo = fileRepo;
            _rabbitRepo = rabbitRepo;
            _protectedConfigManager = protectedConfigManager;
            _protectedParameters = protectedParameters ?? ProtectedConfigurationParameters.All;
            _netConfig = netConfig;
            _netConfig.OnAppIDChangedAsync += HandleAppIDChangedAsync;

            var systemUrl = netConfig.LocalSystemUrl;
            _logger.LogInformation(
                " Starting Processor with AppID = {AppID} instanceName={Instance} connecting to RabbitMQ at {Host}:{Port}",
                AppID, systemUrl.RabbitInstanceName, systemUrl.RabbitHostName, systemUrl.RabbitPort);

            _netConnectCollection = new NetConnectCollection(_logger, _netConfig, connectFactory);
            _monitorPingCollection = new MonitorPingCollection(_logger);
            _monitorPingInfoView = monitorPingInfoView;

            _processorStates = processorStates;
            _processorStates.IsRunning = true;
            _processorStates.RunningMessage = " Success : Agent started ";
        }

        private void SetMonitorPingInfoView()
        {
            if (_monitorPingInfoView == null) return;

            var monitorPingInfos = new List<MonitorPingInfo>();
            foreach (var kv in _monitorPingCollection.MonitorPingInfos.ToList())
            {
                var mpi = kv.Value;
                mpi.PingInfos = new List<PingInfo>();
                monitorPingInfos.Add(mpi);
            }
            _monitorPingInfoView.MonitorPingInfos = monitorPingInfos;
            _monitorPingInfoView.Update();
        }

        public async Task OnStoppingAsync()
        {
            _logger.LogWarning("PROCESSOR SHUTDOWN : starting shutdown of MonitorPingService");

            try
            {
                _processorStates.IsRunning = false;
                _processorStates.IsSetup = false;
                _processorStates.IsRabbitConnected = false;
                _processorStates.IsConnectRunning = false;
                _processorStates.SetupMessage = " Agent is shutdown ";
                _processorStates.RabbitSetupMessage = " Agent is shutdown ";
                _processorStates.ConnectRunningMessage = " Agent is shutdown ";
                _processorStates.RunningMessage = " Success : Agent shutdown ";

                _logger.LogInformation(" Saving MonitorPingInfos to state");
                await PublishRepo.MonitorPingInfos(
                    _logger,
                    _rabbitRepo,
                    _monitorPingCollection.MonitorPingInfos.Values.ToList(),
                    _removeMonitorPingInfoIDs,
                    new List<RemovePingInfo>(),
                    _swapMonitorPingInfos,
                    _monitorPingCollection.PingInfos.Values.ToList(),
                    _netConfig.AppID,
                    _piIDKey,
                    true,
                    _fileRepo,
                    _netConfig.AuthKey);
            }
            catch (Exception e)
            {
                _logger.LogError("Error during saving MonitorPingInfos: {Error}", e);
            }

            try
            {
                _logger.LogInformation(" Sending ProcessorReady = false");
                await PublishRepo.ProcessorReady(_logger, _rabbitRepo, _netConfig.AppID, false);
                _logger.LogInformation(" Success : Published event ProcessorItitObj.IsProcessorReady = false");
            }
            catch (Exception e)
            {
                _logger.LogError("Error during sending ProcessorReady: {Error}", e);
            }

            try
            {
                await _netConnectCollection.WaitAllTasks();
            }
            catch (Exception e)
            {
                _logger.LogError("Error during waiting for all tasks: {Error}", e);
            }

            try
            {
                _logger.LogInformation("Shutting down RabbitRepo.");
                _rabbitRepo?.Shutdown();
                _logger.LogInformation(" Success : Shutdown RabbitRepo.");
            }
            catch (Exception e)
            {
                _logger.LogError("Error during shutting down RabbitRepo: {Error}", e);
            }

            try
            {
                _logger.LogInformation("Shutting down FileRepo.");
                await _fileRepo.ShutdownAsync();
                _logger.LogInformation(" Success : Shutdown FileRepo.");
            }
            catch (Exception e)
            {
                _logger.LogError("Error during shutting down FileRepo: {Error}", e);
            }

            _logger.LogWarning("PROCESSOR SHUTDOWN : Complete");
        }

        public void Dispose()
        {
            _netConfig.OnAppIDChangedAsync -= HandleAppIDChangedAsync;
        }

        private async Task HandleAppIDChangedAsync(string appID)
        {
            var result = new ResultObj { Message = " HandleAppIDChange : ", Success = true };
            List<MonitorIP>? oldMonitorIPs = null;

            try
            {
                oldMonitorIPs = await _fileRepo.GetStateJsonZAsync<List<MonitorIP>>("MonitorIPs");
                if (oldMonitorIPs != null)
                {
                    oldMonitorIPs.ForEach(f => f.AppID = appID);
                    await _fileRepo.SaveStateJsonZAsync("MonitorIPs", oldMonitorIPs);
                    result.Message += $" Success : Got MonitorIPS from statestore count ={oldMonitorIPs.Count}  and Save MonitorIPs back to statestore with new AppID {appID} . ";
                }
            }
            catch (Exception e)
            {
                result.Message += " Error : Could not updated MonitorIPs is state store . Error was : " + e.Message;
                result.Success = false;
            }

            try
            {
                if (oldMonitorIPs == null)
                {
                    result.Message += " MonitorIPs state is empty . Creating empty State . ";
                    oldMonitorIPs = new List<MonitorIP>();
                    await _fileRepo.SaveStateJsonZAsync("MonitorIPs", oldMonitorIPs);
                }
            }
            catch (Exception e)
            {
                result.Message += " Error : Could not reset MonitorIPs is state store . Error was : " + e.Message;
                result.Success = false;
            }

            try
            {
                _monitorIPQueueDic = new ConcurrentDictionary<string, List<UpdateMonitorIP>>();
                await _monitorPingCollection.ChangeAppID(_lock, appID);
                result.Message += $" Success : Set all MonitorPingInfo AppIDs to {_netConfig.AppID} ";
            }
            catch (Exception e)
            {
                result.Message += "  Error : Could change MonitorPingInfo AppIDs . Error was : " + e.Message;
                result.Success = false;
            }

            if (result.Success) _logger.LogInformation(result.Message);
            else _logger.LogError(result.Message);
        }

        public async Task<ResultObj> SetAuthKey(ProcessorInitObj processorInitObj)
        {
            var result = new ResultObj { Message = " SetAuthKey : " };
            if (string.IsNullOrWhiteSpace(processorInitObj.AuthKey))
            {
                result.Success = false;
                result.Message += " Error : AuthKey was null or empty.";
                _logger.LogError(result.Message);
                return result;
            }

            try
            {
                _netConfig.AuthKey = processorInitObj.AuthKey;
                _netConfig.AgentUserFlow.IsAuthorized = true;
                _netConfig.AgentUserFlow.IsLoggedInWebsite = false;
                _netConfig.AgentUserFlow.IsHostsAdded = false;
                _netConfig.AgentUserFlow.IsChatOpened = false;
                await _protectedConfigManager.PersistAsync(ProtectedConfigurationParameters.AuthKey, _netConfig, processorInitObj.AuthKey);
                await _protectedConfigManager.PersistAsync(ProtectedConfigurationParameters.RabbitPassword, _netConfig, _netConfig.RabbitPassword);


                await SaveNetConfigAsync();
                await _netConfig.AuthComplete();

                result.Success = true;
                result.Message += " Success : Stored AuthKey to environment and config.";
            }
            catch (Exception e)
            {
                result.Success = false;
                result.Message += $" Error : Could not persist AuthKey . Error was {e.Message}";
                _logger.LogError(result.Message);
                return result;
            }

            try
            {
                await Init(processorInitObj);
                result.Message += " Success : Ran Processor Init after Setting AuthKey.";
            }
            catch (Exception e)
            {
                result.Success = false;
                result.Message += $" Error : Could  run Processor Init . Error was {e.Message}";
                _logger.LogError(result.Message);
            }
            return result;
        }

        public async Task<ResultObj> ProcessorUserEvent(ProcessorUserEventObj processorUserEventObj)
        {
            var result = new ResultObj();
            var isValueChanged = false;

            if (processorUserEventObj.IsLoggedInWebsite != null &&
                _netConfig.AgentUserFlow.IsLoggedInWebsite != (bool)processorUserEventObj.IsLoggedInWebsite)
            {
                _netConfig.AgentUserFlow.IsLoggedInWebsite = (bool)processorUserEventObj.IsLoggedInWebsite;
                result.Success = true;
                result.Message += $" Success : Updated AgentUserFlow.IsLoggedInWebsite to {_netConfig.AgentUserFlow.IsLoggedInWebsite}";
                isValueChanged = true;
            }

            if (processorUserEventObj.IsHostsAdded != null &&
                _netConfig.AgentUserFlow.IsHostsAdded != (bool)processorUserEventObj.IsHostsAdded)
            {
                _netConfig.AgentUserFlow.IsHostsAdded = (bool)processorUserEventObj.IsHostsAdded;
                result.Success = true;
                result.Message += $" Success : Updated AgentUserFlow.IsHostsAdded to {_netConfig.AgentUserFlow.IsHostsAdded}";
                isValueChanged = true;
            }

            if (!result.Success)
            {
                result.Success = true;
                result.Message += " No ProcessorUserEvent properties set . ";
            }

            if (isValueChanged)
            {
                await SaveNetConfigAsync();
            }

            return result;
        }

        private async Task SaveNetConfigAsync()
        {
            var originalOqsPath = _netConfig.OqsProviderPath;

            try
            {
                _netConfig.OqsProviderPath = _netConfig.OqsProviderPathReadOnly;

                await _protectedConfigManager.SaveConfigurationAsync(_netConfig, _protectedParameters);
            }
            finally
            {
                _netConfig.OqsProviderPath = originalOqsPath;
            }
        }

        /// <summary>
        /// Initializes agent state using state store and input. 
        /// </summary>
        public async Task<ResultObj> Init(ProcessorInitObj initObj)
        {
            _processorStates.IsSetup = false;
            var result = new ResultObj { Message = " Init : ", Success = true };

            var stateSetup = new StateSetup(_logger, _monitorPingCollection, _lock, _fileRepo);
            _removeMonitorPingInfoIDs = new List<int>();
            var initNetConnects = false;
            var disableNetConnects = false;

            try
            {
                if (initObj.TotalReset)
                {
                    initNetConnects = await stateSetup.TotalReset();
                    if (!initNetConnects)
                    {
                        result.Message += " Error : Unable to perform TotalReset exiting Init() .";
                        _logger.LogCritical(result.Message);
                        result.Success = false;
                        _processorStates.IsSetup = result.Success;
                        _processorStates.SetupMessage = result.Message;
                        return result;
                    }
                    disableNetConnects = true;
                }
                else
                {
                    if (initObj.Reset)
                    {
                        _logger.LogInformation("Zeroing MonitorPingInfos for new DataSet");
                        await _monitorPingCollection.ZeroMonitorPingInfos(_lock);
                        stateSetup.CurrentMonitorPingInfos = _monitorPingCollection.MonitorPingInfos.Values.ToList();
                        stateSetup.CurrentPingInfos = _monitorPingCollection.PingInfos.Values.ToList();
                        _piIDKey = 1;
                        initNetConnects = false;
                        disableNetConnects = true;
                    }
                    else
                    {
                        await stateSetup.LoadFromState(initNetConnects, _piIDKey, _removeMonitorPingInfoIDs, _swapMonitorPingInfos, _monitorPingCollection);
                        // pick up outputs without changing the LoadFromState API
                        _piIDKey = stateSetup.LoadedPiIdKey;
                        _removeMonitorPingInfoIDs = stateSetup.LoadedRemoveMonitorPingInfoIDs;
                        _swapMonitorPingInfos = stateSetup.LoadedSwapMonitorPingInfos;
                        initNetConnects = false;
                        disableNetConnects = false;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError("Failed : Loading statestore : Error was : {Error}", e);
                stateSetup.CurrentMonitorPingInfos = new List<MonitorPingInfo>();
                stateSetup.CurrentPingInfos = new List<PingInfo>();
            }

            try
            {
                if (initNetConnects) _monitorIPQueueDic = new ConcurrentDictionary<string, List<UpdateMonitorIP>>();
                await stateSetup.MergeState(initObj);
                _logger.LogDebug(" Merge State Complete ");

                if (initObj.PingParams == null)
                {
                    result.Message += " Critical Error : Can not continue Init. PingParms is null .";
                    _logger.LogCritical(result.Message);
                    result.Success = false;
                    _processorStates.IsSetup = result.Success;
                    _processorStates.SetupMessage = result.Message;
                    return result;
                }

                if (_netConfig.AppID == null)
                {
                    result.Message += " Critical Error : Can not continue Init. AppID is null .";
                    _logger.LogCritical(result.Message);
                    result.Success = false;
                    _processorStates.IsSetup = result.Success;
                    _processorStates.SetupMessage = result.Message;
                    return result;
                }

                _monitorPingCollection.SetVars(AppID, initObj.PingParams);
                _logger.LogDebug("  MonitorPingCollection Set Vars Complete ");

                await _monitorPingCollection.MonitorPingInfoFactory(
                    initObj.MonitorIPs, stateSetup.CurrentMonitorPingInfos, stateSetup.CurrentPingInfos, _lock);
                _logger.LogDebug(" MonitorPingCollection MonitorPingInfoFactory Complete");

                await _netConnectCollection.NetConnectFactory(
                    _monitorPingCollection.MonitorPingInfos.Values.ToList(),
                    initObj.PingParams,
                    initNetConnects,
                    disableNetConnects,
                    _lock);
                _logger.LogDebug(" NetConnectCollection NetConnectFactory Complete");

                var monitorPingInfos = _monitorPingCollection.MonitorPingInfos.Values.ToList();
                _logger.LogDebug("MonitorPingInfos : {Json}", JsonUtils.WriteJsonObjectToString(monitorPingInfos));
                _logger.LogDebug("MonitorIPs : {Json}", JsonUtils.WriteJsonObjectToString(initObj.MonitorIPs));
                _logger.LogDebug("PingParams : {Json}", JsonUtils.WriteJsonObjectToString(initObj.PingParams));

                await PublishRepo.MonitorPingInfosLowPriorityThread(
                    _logger,
                    _rabbitRepo,
                    monitorPingInfos,
                    _removeMonitorPingInfoIDs,
                    new List<RemovePingInfo>(),
                    _swapMonitorPingInfos,
                    stateSetup.CurrentPingInfos,
                    _netConfig.AppID,
                    _piIDKey,
                    false,
                    _fileRepo,
                    _netConfig.AuthKey);
            }
            catch (Exception e)
            {
                result.Message += $" Error : Unable to init Processor : Error was : {e}";
                _logger.LogCritical(result.Message);
                result.Success = false;
            }
            finally
            {
                // publish readiness based on success
                await PublishRepo.ProcessorReady(_logger, _rabbitRepo, _netConfig.AppID, result.Success);
            }

            try
            {
                if (_monitorPingInfoView != null) SetMonitorPingInfoView();
            }
            catch (Exception e)
            {
                result.Success = false;
                _logger.LogError(" Error : Could not set MonitorPingInfoView . Error was : {Error}", e);
            }

            try
            {
                await _protectedConfigManager.PersistAsync(ProtectedConfigurationParameters.AuthKey, _netConfig, _netConfig.AuthKey);
                await _protectedConfigManager.PersistAsync(ProtectedConfigurationParameters.RabbitPassword, _netConfig, _netConfig.RabbitPassword);

                await SaveNetConfigAsync();
                result.Message += " Success : Saved netconfig with protected parameters . ";
            }
            catch (Exception e)
            {
                result.Success = false;
                _logger.LogError(" Error : Could not save protected configuration parameters . Error was : {Error}", e);
            }


            _processorStates.IsSetup = result.Success;
            if (result.Success) result.Message += " Success : Setup completed ";
            _processorStates.SetupMessage = result.Message;
            return result;
        }

        public async Task<ResultObj> Connect(ProcessorConnectObj connectObj)
        {
            var result = new ResultObj();
            if (!_processorStates.IsSetup)
            {
                result.Message += " Warning : Agent not setup. Please wait... ";
                result.Success = false;
                _processorStates.IsConnectRunning = false;
                _processorStates.IsConnectState = ConnectState.Error;
                _processorStates.ConnectRunningMessage = result.Message;
                return result;
            }

            _processorStates.IsConnectRunning = true;
            _processorStates.IsConnectState = ConnectState.Running;
            _processorStates.ConnectRunningMessage = " Success : Monitor running ";

            var timerInner = new Stopwatch();
            timerInner.Start();

            _logger.LogDebug(" ProcessorConnectObj : {Json}", JsonUtils.WriteJsonObjectToString(connectObj));
            await PublishRepo.ProcessorReady(_logger, _rabbitRepo, _netConfig.AppID, false);

            result.Success = false;
            result.Message = " SERVICE : MonitorPingProcessor.Connect() ";
            result.Message += await UpdateMonitorPingInfosFromMonitorIPQueue();

            if (_monitorPingCollection.MonitorPingInfos == null ||
                _monitorPingCollection.MonitorPingInfos.Values.Count(x => x.Enabled) == 0)
            {
                result.Message += " Warning : There is no MonitorPingInfo data. ";
                result.Success = false;
                _processorStates.IsConnectRunning = false;
                _processorStates.IsConnectState = ConnectState.Error;
                _processorStates.ConnectRunningMessage = result.Message;
                await PublishRepo.ProcessorReady(_logger, _rabbitRepo, _netConfig.AppID, true);
                return result;
            }

            try
            {
                var countPingInfos = _monitorPingCollection.PingInfos.Count;
                var maxPingInfos = _netConfig.LocalSystemUrl.MaxLoad * _netConfig.LocalSystemUrl.MaxRuntime;
                if (countPingInfos > maxPingInfos)
                {
                    result.Success = false;
                    result.Message += $" Error : The number of stored monitor events ({countPingInfos}) is greater than the maximum threshold ({maxPingInfos}) . Unable to continue monitoring. When the agent connects to the data service it will offload this data and continue. If this message persists then contact support. ";
                    _processorStates.IsConnectRunning = false;
                    _processorStates.IsConnectState = ConnectState.Error;
                    _processorStates.ConnectRunningMessage = result.Message;
                    try
                    {
                        await PublishRepo.MonitorPingInfosLowPriorityThread(
                            _logger,
                            _rabbitRepo,
                            _monitorPingCollection.MonitorPingInfos.Values.ToList(),
                            _removeMonitorPingInfoIDs,
                            _monitorPingCollection.RemovePingInfos.Values.ToList(),
                            _swapMonitorPingInfos,
                            _monitorPingCollection.PingInfos.Values.ToList(),
                            _netConfig.AppID,
                            _piIDKey,
                            true,
                            _fileRepo,
                            _netConfig.AuthKey);

                        await PublishRepo.ProcessorReady(_logger, _rabbitRepo, _netConfig.AppID, true);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(" Error : unable to publish rabbit messages after threshold reached. Error was : {Error}", e.Message);
                    }

                    return result;
                }

                var filteredNetConnects = _netConnectCollection.GetFilteredNetConnects().ToList();
                var count = filteredNetConnects.Count;
                if (count == 0)
                {
                    result.Message += " Warning : There are no NetConnects to process. ";
                    _logger.LogWarning(" Warning : There are no NetConnects to process. ");
                    count = 1;
                }

                var executionTime = connectObj.NextRunInterval - connectObj.MaxBuffer;
                var timeToWait = executionTime / count;
                if (timeToWait < 25)
                {
                    result.Message += " Warning : Time to wait between monitor events is less than 25ms.  This may cause problems with the agent.  Reduce the number of hosts monitored. ";
                }

                result.Message += " Info : Time to wait between monitor events : " + timeToWait + "ms. ";
                var countDown = filteredNetConnects.Count;

                foreach (var netConnect in filteredNetConnects)
                {
                    netConnect.Cts = new CancellationTokenSource();
                    netConnect.PiID = _piIDKey;
                    _piIDKey++;

                    if (netConnect.IsLongRunning)
                    {
                        _ = _netConnectCollection.HandleLongRunningTask(netConnect, _monitorPingCollection.Merge);
                    }
                    else
                    {
                        _ = _netConnectCollection.HandleShortRunningTask(netConnect, _monitorPingCollection.Merge);
                    }

                    await Task.Delay(timeToWait);

                    if (countDown < 1) countDown = 1;
                    timeToWait = (executionTime - (int)timerInner.ElapsedMilliseconds) / countDown;
                    if (timeToWait < 0)
                    {
                        timeToWait = 0;
                        result.Message += " Warning : Time to wait is less than 0ms.  This may cause problems with the service.  Please check the schedule settings. ";
                    }
                    countDown--;
                }

                result.Message += " Success : Completed all connections in " + timerInner.Elapsed.TotalMilliseconds + " ms ";
                result.Success = true;
                result.Message += _netConnectCollection.LogInfo(filteredNetConnects);
            }
            catch (Exception e)
            {
                result.Message += " Error : MonitorPingProcessor.Connect Failed : Error Was : " + e + " . ";
                result.Success = false;
                _logger.LogCritical(" Error : MonitorPingProcessor.Connect Failed : Error Was : {Error} . ", e);
            }
            finally
            {
                if (_monitorPingCollection.MonitorPingInfos.Count > 0)
                {
                    var removeResult = await _monitorPingCollection.RemovePublishedPingInfos(_lock);
                    result.Message += removeResult.Message;

                    await PublishRepo.MonitorPingInfosLowPriorityThread(
                        _logger,
                        _rabbitRepo,
                        _monitorPingCollection.MonitorPingInfos.Values.ToList(),
                        _removeMonitorPingInfoIDs,
                        _monitorPingCollection.RemovePingInfos.Values.ToList(),
                        _swapMonitorPingInfos,
                        _monitorPingCollection.PingInfos.Values.ToList(),
                        _netConfig.AppID,
                        _piIDKey,
                        true,
                        _fileRepo,
                        _netConfig.AuthKey);
                }

                await PublishRepo.ProcessorReady(_logger, _rabbitRepo, _netConfig.AppID, true);
            }

            var timeTakenInnerInt = (int)timerInner.Elapsed.TotalMilliseconds;
            if (timeTakenInnerInt > connectObj.NextRunInterval)
            {
                result.Message += " Warning : Time to execute the monitor tasks was greater than next schedule time. One schedule will be missed.";
            }

            try
            {
                if (_monitorPingInfoView != null) SetMonitorPingInfoView();
            }
            catch (Exception e)
            {
                result.Message += $" Error : Could not set UI data view . Error was : {e.Message}";
                result.Success = false;
                _logger.LogError(" Error : Could not set MonitorPingInfoView . Error was : {Error}", e);
            }

            if (result.Success)
            {
                result.Message += " Success : All monitor tasks executed in " + timerInner.Elapsed.TotalMilliseconds + " ms ";
                _processorStates.IsConnectState = ConnectState.Waiting;
            }
            else
            {
                result.Message += " All monitor tasks executed in " + timerInner.Elapsed.TotalMilliseconds + " ms with Errors .";
                _processorStates.IsConnectState = ConnectState.Error;
            }

            _processorStates.IsConnectRunning = false;
            _processorStates.ConnectRunningMessage = result.Message;

            return result;
        }

        // ---------- MISSING INTERFACE MEMBERS (restored) ----------

        public List<ResultObj> UpdateAlertSent(List<int> monitorIPIDs, bool alertSent)
        {
            var results = new List<ResultObj>();
            foreach (int id in monitorIPIDs)
            {
                var updateMonitorPingInfo = _monitorPingCollection.MonitorPingInfos.Values.FirstOrDefault(w => w.MonitorIPID == id);
                var result = new ResultObj();
                if (updateMonitorPingInfo != null)
                {
                    updateMonitorPingInfo.MonitorStatus.AlertSent = alertSent;
                    result.Success = true;
                    result.Message += "Success : updated AlertSent to " + alertSent + " for MonitorPingInfo with MonitorIPID = " + id;
                }
                else
                {
                    result.Success = false;
                    result.Message += "Failed : updating AlertSent for MonitorPingInfo with MonitorIPID = " + id;
                }
                results.Add(result);
            }
            return results;
        }

        public List<ResultObj> UpdateAlertFlag(List<int> monitorIPIDs, bool alertFlag)
        {
            var results = new List<ResultObj>();
            foreach (int id in monitorIPIDs)
            {
                var updateMonitorPingInfo = _monitorPingCollection.MonitorPingInfos.Values.FirstOrDefault(w => w.MonitorIPID == id);
                var result = new ResultObj();
                if (updateMonitorPingInfo != null)
                {
                    updateMonitorPingInfo.MonitorStatus.AlertFlag = alertFlag;
                    result.Success = true;
                    result.Message += "Success : updated AlertFlag to " + alertFlag + " for MonitorPingInfo with MonitorIPID = " + id;
                }
                else
                {
                    result.Success = false;
                    result.Message += "Failed : updating AlertFlag for MonitorPingInfo with MonitorIPID = " + id;
                }
                results.Add(result);
            }
            return results;
        }

        public async Task<List<ResultObj>> ResetAlerts(List<int> monitorIPIDs)
        {
            var results = new List<ResultObj>();
            ResultObj result;
            var alertFlagObjs = new List<AlertFlagObj>();
            monitorIPIDs.ForEach(m =>
            {
                result = new ResultObj();
                var alertFlagObj = new AlertFlagObj();
                alertFlagObj.ID = m;
                alertFlagObj.AppID = AppID;
                alertFlagObjs.Add(alertFlagObj);
                var updateMonitorPingInfo = _monitorPingCollection.MonitorPingInfos.Values.FirstOrDefault(w => w.MonitorIPID == alertFlagObj.ID && w.AppID == alertFlagObj.AppID);
                if (updateMonitorPingInfo == null)
                {
                    result.Success = false;
                    result.Message += " Warning : Unable to find MonitorPingInfo with MonitorIPID " + alertFlagObj.ID + " with AppID " + alertFlagObj.AppID + " . ";
                }
                else
                {
                    if (updateMonitorPingInfo.EndPointType == "sitehash")
                    {
                        _netConnectCollection.ResetSiteHash(updateMonitorPingInfo.MonitorIPID);
                        updateMonitorPingInfo.SiteHash = null;
                    }
                    updateMonitorPingInfo.MonitorStatus.AlertFlag = false;
                    updateMonitorPingInfo.MonitorStatus.AlertSent = false;
                    updateMonitorPingInfo.IsDirtyDownCount = true;
                    updateMonitorPingInfo.MonitorStatus.ResetDownCount();
                    result.Success = true;
                    result.Message += " Success : updated MonitorPingInfo with MonitorIPID " + alertFlagObj.ID + " with AppID " + alertFlagObj.AppID + " . ";
                }
                results.Add(result);
            });
            results.Add(await PublishRepo.AlertMessgeResetAlerts(_rabbitRepo, alertFlagObjs, _netConfig.AppID, _netConfig.AuthKey));
            return results;
        }

        public async Task<ResultObj> WakeUp()
        {
            ResultObj result = new ResultObj();
            result.Message = "SERVICE : MonitorPingProcessor.WakeUp() ";
            try
            {
                if (!_processorStates.IsSetup)
                {
                    result.Message += " Warning : Received WakeUp but setup is running. ";
                    result.Success = false;
                    return result;
                }
                if (_processorStates.IsConnectRunning)
                {
                    result.Message += " Warning : Received WakeUp but processor is currently running";
                    result.Success = false;
                }
                else
                {
                    await PublishRepo.ProcessorReady(_logger, _rabbitRepo, _netConfig.AppID, true);
                    result.Message += "Received WakeUp so Published event processorReady = true";
                    result.Success = true;
                }
            }
            catch (Exception e)
            {
                result.Message += "Error : failed to Published event processorReady = true. Error was : " + e.ToString();
                result.Success = false;
            }
            return result;
        }

        // ---------- internal helpers ----------

        private async Task<string> UpdateMonitorPingInfosFromMonitorIPQueue()
        {
            await _lock.WaitAsync();
            string message = "";
            try
            {
                var monitorIPQueue = new List<UpdateMonitorIP>();
                if (_monitorIPQueueDic.Count == 0) return " No host config changes to Process . ";

                foreach (var kvp in _monitorIPQueueDic)
                {
                    if (!kvp.Value[0].DeleteAll)
                    {
                        kvp.Value.ForEach(f =>
                        {
                            if (!f.Delete) monitorIPQueue.Add(f);
                        });
                    }
                }

                // Add & update
                foreach (var monIP in monitorIPQueue)
                {
                    var monitorPingInfo = _monitorPingCollection.MonitorPingInfos.Values.FirstOrDefault(m => m.MonitorIPID == monIP.ID);
                    if (monitorPingInfo != null)
                    {
                        try
                        {
                            var endpointChanged = monitorPingInfo.EndPointType != monIP.EndPointType;
                            _monitorPingCollection.FillPingInfo(monitorPingInfo, monIP);
                            if (endpointChanged)
                                message += _netConnectCollection.RemoveAndAdd(monitorPingInfo);
                            else
                                _netConnectCollection.UpdateOrAdd(monitorPingInfo);
                        }
                        catch
                        {
                            message += "Error : Failed to update Host list check Values .";
                        }
                        _logger.LogInformation(" Updating MonitorPingInfo with ID {Id}", monitorPingInfo.ID);
                    }
                    else
                    {
                        if (!monIP.IsSwapping || monIP.MonitorPingInfo == null)
                        {
                            monitorPingInfo = new MonitorPingInfo();
                            _monitorPingCollection.FillPingInfo(monitorPingInfo, monIP);
                            _logger.LogInformation(" Just adding a new MonitorPingInfo with ID {Id}", monitorPingInfo.ID);
                        }
                        else
                        {
                            monitorPingInfo = monIP.MonitorPingInfo;
                            monitorPingInfo.AppID = _netConfig.AppID;
                            _swapMonitorPingInfos.Add(new SwapMonitorPingInfo
                            {
                                ID = monitorPingInfo.MonitorIPID,
                                AppID = _netConfig.AppID
                            });
                            _logger.LogInformation(" Adding SwapMonitorPingInfo with ID {Id} AppID {AppID}", monitorPingInfo.ID, _netConfig.AppID);
                        }

                        if (!_monitorPingCollection.MonitorPingInfos.TryAdd(monitorPingInfo.MonitorIPID, monitorPingInfo))
                        {
                            _logger.LogError(" Error : Failed to add MonitorPingInfo with ID {Id} to MonitorPingCollection. ", monitorPingInfo.ID);
                            message += $" Error : Failed to add MonitorPingInfo with ID {monitorPingInfo.ID} to MonitorPingCollection. ";
                        }
                        _netConnectCollection.Add(monitorPingInfo);
                    }
                }

                // Delete
                var delList = new List<MonitorPingInfo>();
                foreach (var kvp in _monitorIPQueueDic)
                {
                    kvp.Value.ForEach(f =>
                    {
                        if (f.Delete)
                        {
                            var del = _monitorPingCollection.MonitorPingInfos.Where(w => w.Key == f.ID).FirstOrDefault();
                            if (del.Value != null)
                            {
                                delList.Add(del.Value);
                                _logger.LogInformation(" Deleting MonitorPingInfo with MonitorIPID {Id}", f.ID);

                                if (!f.IsSwapping)
                                {
                                    _removeMonitorPingInfoIDs.Add(del.Value.MonitorIPID);
                                    _logger.LogInformation(" Not a swap; adding to removeMonitorPingInfosIDS for MonitorIPID {Id}", f.ID);
                                }
                                else
                                {
                                    _logger.LogInformation(" This is a swap; not adding to removeMonitorPingInfosIDS for MonitorIPID {Id}", f.ID);
                                }
                            }
                        }
                    });
                }

                var failRemove = new List<int>();
                foreach (var del in delList)
                {
                    if (!_monitorPingCollection.MonitorPingInfos.TryRemove(del.MonitorIPID, out _))
                    {
                        // FIX: match keying by MonitorIPID
                        failRemove.Add(del.MonitorIPID);
                        message += $" Error : Failed to remove MonitorPingInfo with MonitorIPID {del.MonitorIPID} . ";
                        _logger.LogError(" Error : Failed to remove MonitorPingInfo with MonitorIPID {Id} . ", del.MonitorIPID);
                    }
                    _netConnectCollection.DisableAll(del.MonitorIPID);
                }

                message += " Success : Updated MonitorPingInfos. ";

                // Update statestore
                var resultStateUpdated = await UpdateMonitorIPsInStatestore(monitorIPQueue);
                message += resultStateUpdated.Message;

                if (resultStateUpdated.Success)
                {
                    // prune processed queue entries except those that failed removal
                    foreach (var kvp in _monitorIPQueueDic)
                    {
                        kvp.Value.RemoveAll(r => !failRemove.Contains(r.ID));
                    }

                    // clean up empty keys
                    foreach (var key in _monitorIPQueueDic.Keys.ToList())
                    {
                        if (_monitorIPQueueDic.TryGetValue(key, out var value) && value.Count == 0)
                        {
                            _monitorIPQueueDic.TryRemove(key, out _);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                message += " Error : Failed to Process Monitor IP Queue. Error was : " + e.Message + " . ";
                _logger.LogError(" Error : Failed to Process Monitor IP Queue. Error was : {Error} . ", e);
            }
            finally
            {
                _lock.Release();
            }

            return message;
        }

        public void ProcessesMonitorReturnData(ProcessorDataObj processorDataObj)
        {
            if (_removeMonitorPingInfoIDs == null) _removeMonitorPingInfoIDs = new List<int>();
            if (_swapMonitorPingInfos == null) _swapMonitorPingInfos = new List<SwapMonitorPingInfo>();

            try
            {
                processorDataObj.RemovePingInfos.ForEach(f =>
                {
                    _monitorPingCollection.RemovePingInfos.TryAdd(f.ID, f);
                });
            }
            catch { }

            try
            {
                _removeMonitorPingInfoIDs = _removeMonitorPingInfoIDs.Except(processorDataObj.RemoveMonitorPingInfoIDs).ToList();
            }
            catch { }

            try
            {
                _swapMonitorPingInfos = _swapMonitorPingInfos.Except(processorDataObj.SwapMonitorPingInfos, new SwapMonitorPingInfoComparer()).ToList();
            }
            catch { }
        }

        private async Task<ResultObj> UpdateMonitorIPsInStatestore(List<UpdateMonitorIP> updateMonitorIPs)
        {
            var result = new ResultObj { Message = "" };
            try
            {
                var stateMonitorIPs = await _fileRepo.GetStateJsonZAsync<List<MonitorIP>>("MonitorIPs") ?? new List<MonitorIP>();

                foreach (var updateMonitorIP in updateMonitorIPs)
                {
                    var found = stateMonitorIPs.FirstOrDefault(w => w.ID == updateMonitorIP.ID);
                    if (found == null)
                    {
                        stateMonitorIPs.Add((MonitorIP)updateMonitorIP);
                    }
                    else
                    {
                        stateMonitorIPs.Remove(found);
                        stateMonitorIPs.Add((MonitorIP)updateMonitorIP);
                    }
                }

                foreach (var kvp in _monitorIPQueueDic)
                {
                    kvp.Value.ForEach(f =>
                    {
                        if (f.Delete) stateMonitorIPs.RemoveAll(r => r.ID == f.ID);
                    });
                }

                await _fileRepo.SaveStateJsonZAsync("MonitorIPs", stateMonitorIPs);
                result.Message += " Success : saved MonitorIP queue into statestore. ";
                result.Success = true;
            }
            catch (Exception e)
            {
                result.Message = "Error : Failed to update MonitorIP queue to statestore. Error was : " + e.Message;
                _logger.LogError("Error : Failed to update MonitorIP queue to statestore. Error was : {Error}", e.Message);
                result.Success = false;
            }
            return result;
        }

        public ResultObj AddMonitorIPsToQueueDic(ProcessorQueueDicObj queueDicObj)
        {
            var result = new ResultObj { Message = " AddMonitorIPsToQueueDic : " };

            if (queueDicObj.MonitorIPs == null || queueDicObj.MonitorIPs.Count == 0)
            {
                result.Success = true;
                result.Message += " Nothing to do : No Data .";
                return result;
            }

            // protect the shared list values while reader holds the same semaphore in UpdateMonitorPingInfosFromMonitorIPQueue
            _lock.Wait();
            try
            {
                var list = _monitorIPQueueDic.GetOrAdd(queueDicObj.UserId, _ => new List<UpdateMonitorIP>());
                foreach (var newMonitorIP in queueDicObj.MonitorIPs)
                {
                    var existing = list.FirstOrDefault(w => w.ID == newMonitorIP.ID);
                    if (existing != null)
                    {
                        if (!existing.Delete)
                        {
                            list.Remove(existing);
                            list.Add(newMonitorIP);
                        }
                    }
                    else
                    {
                        list.Add(newMonitorIP);
                    }
                }

                result.Success = true;
                result.Message += $" Success : Added {queueDicObj.MonitorIPs.Count} MonitorIPs Queue . ";
                return result;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
