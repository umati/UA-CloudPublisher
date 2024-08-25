
namespace Opc.Ua.Cloud.Publisher
{
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;
    using Opc.Ua;
    using Opc.Ua.Client;
    using Opc.Ua.Client.ComplexTypes;
    using Opc.Ua.Cloud.Publisher.Interfaces;
    using Opc.Ua.Cloud.Publisher.Models;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public class UAClient : IUAClient
    {
        private const uint WoTAssetConnectionManagement = 31;
        private const uint WoTAssetConnectionManagement_CreateAsset = 32;
        private const uint WoTAssetConnectionManagement_DeleteAsset = 35;
        private const uint WoTAssetFileType_CloseAndUpdate = 111;

        private readonly IUAApplication _app;
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IFileStorage _storage;

        private IMessageSource _trigger;

        private List<ISession> _sessions = new List<ISession>();
        private object _sessionLock = new object();

        private List<SessionReconnectHandler> _reconnectHandlers = new List<SessionReconnectHandler>();
        private object _reconnectHandlersLock = new object();

        private List<PeriodicPublishing> _periodicPublishingList = new List<PeriodicPublishing>();
        private object _periodicPublishingListLock = new object();

        private Dictionary<string, uint> _missedKeepAlives = new Dictionary<string, uint>();
        private object _missedKeepAlivesLock = new object();

        private Dictionary<string, EndpointDescription> _endpointDescriptionCache = new Dictionary<string, EndpointDescription>();
        private object _endpointDescriptionCacheLock = new object();

        private readonly Dictionary<ISession, ComplexTypeSystem> _complexTypeList = new Dictionary<ISession, ComplexTypeSystem>();

        public UAClient(
            IUAApplication app,
            ILoggerFactory loggerFactory,
            IMessageSource trigger,
            IFileStorage storage)
        {
            _logger = loggerFactory.CreateLogger("UAClient");
            _loggerFactory = loggerFactory;
            _app = app;
            _trigger = trigger;
            _storage = storage;
        }

        public void Dispose()
        {
            try
            {
                UnpublishAllNodes(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failure while unpublishing all nodes.");
            }
        }

        private Session FindSession(string endpointUrl)
        {
            EndpointDescription selectedEndpoint;
            try
            {
                lock (_endpointDescriptionCacheLock)
                {
                    if (_endpointDescriptionCache.ContainsKey(endpointUrl))
                    {
                        selectedEndpoint = _endpointDescriptionCache[endpointUrl];
                    }
                    else
                    {
                        // use a discovery client to connect to the server and discover all its endpoints, then pick the one with the highest security
                        selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointUrl, true);

                        // add to cache
                        _endpointDescriptionCache[endpointUrl] = selectedEndpoint;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot reach server on endpoint {endpointUrl}. Please make sure your OPC UA server is running and accessible.", endpointUrl);
                return null;
            }

            if (selectedEndpoint == null)
            {
                // could not get the requested endpoint
                return null;
            }

            // check if there is already a session for the requested endpoint
            lock (_sessionLock)
            {
                ConfiguredEndpoint configuredEndpoint = new ConfiguredEndpoint(
                    null,
                    selectedEndpoint,
                    EndpointConfiguration.Create()
                );

                foreach (Session session in _sessions)
                {
                    if ((session.ConfiguredEndpoint.EndpointUrl == configuredEndpoint.EndpointUrl) ||
                        (session.ConfiguredEndpoint.EndpointUrl.ToString() == endpointUrl))
                    {
                        // return the existing session
                        return session;
                    }
                }
            }

            return null;
        }

        private async Task<Session> ConnectSessionAsync(string endpointUrl, string username, string password)
        {
            // check if the required session is already available
            Session existingSession = FindSession(endpointUrl);
            if (existingSession != null)
            {
                return existingSession;
            }

            EndpointDescription selectedEndpoint = null;
            ITransportWaitingConnection connection = null;
            if (Settings.Instance.UseReverseConnect)
            {
                _logger.LogInformation("Waiting for reverse connection from {0}", endpointUrl);
                connection = await _app.ReverseConnectManager.WaitForConnection(new Uri(endpointUrl), null, new CancellationTokenSource(30_000).Token).ConfigureAwait(false);
                if (connection == null)
                {
                    throw new ServiceResultException(StatusCodes.BadTimeout, "Waiting for a reverse connection timed out after 30 seconds.");
                }

                selectedEndpoint = CoreClientUtils.SelectEndpoint(_app.UAApplicationInstance.ApplicationConfiguration, connection, true);
            }
            else
            {
                selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointUrl, true);
            }

            ConfiguredEndpoint configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, EndpointConfiguration.Create(_app.UAApplicationInstance.ApplicationConfiguration));
            _logger.LogInformation("Connecting session on endpoint {endpointUrl}.", configuredEndpoint.EndpointUrl);

            uint timeout = (uint)_app.UAApplicationInstance.ApplicationConfiguration.ClientConfiguration.DefaultSessionTimeout;

            _logger.LogInformation("Creating session for endpoint {endpointUrl} with timeout of {timeout} ms.",
                configuredEndpoint.EndpointUrl,
                timeout);

            UserIdentity userIdentity = null;
            if (username == null)
            {
                userIdentity = new UserIdentity(new AnonymousIdentityToken());
            }
            else
            {
                userIdentity = new UserIdentity(username, password);
            }

            Session newSession = null;
            try
            {
                newSession = await Session.Create(
                    _app.UAApplicationInstance.ApplicationConfiguration,
                    configuredEndpoint,
                    true,
                    false,
                    _app.UAApplicationInstance.ApplicationConfiguration.ApplicationName,
                    timeout,
                    userIdentity,
                    null
                ).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Session creation to endpoint {endpointUrl} failed. Please verify that the OPC UA server for the specified endpoint is accessible.",
                     configuredEndpoint.EndpointUrl);

                return null;
            }

            _logger.LogInformation("Session successfully created with Id {session}.", newSession.SessionId);
            if (!selectedEndpoint.EndpointUrl.Equals(configuredEndpoint.EndpointUrl.OriginalString, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("The Server has updated the EndpointUrl to {endpointUrl}", selectedEndpoint.EndpointUrl);
            }

            // enable diagnostics
            newSession.ReturnDiagnostics = DiagnosticsMasks.All;

            // register keep alive callback
            newSession.KeepAlive += KeepAliveHandler;

            // enable subscriptions transfer
            newSession.DeleteSubscriptionsOnClose = false;
            newSession.TransferSubscriptionsOnReconnect = true;

            // add the session to our list
            lock (_sessionLock)
            {
                _sessions.Add(newSession);
                Diagnostics.Singleton.Info.NumberOfOpcSessionsConnected++;
            }

            // load complex type system
            try
            {
                if (!_complexTypeList.ContainsKey(newSession))
                {
                    _complexTypeList.Add(newSession, new ComplexTypeSystem(newSession));
                }

                await _complexTypeList[newSession].Load().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load complex type system for session!");
            }

            return newSession;
        }

        public void UnpublishAllNodes(bool updatePersistencyFile = true)
        {
            // loop through all sessions
            lock (_sessionLock)
            {
                foreach (PeriodicPublishing heartbeat in _periodicPublishingList)
                {
                    heartbeat.Stop();
                    heartbeat.Dispose();
                }
                _periodicPublishingList.Clear();

                while (_sessions.Count > 0)
                {
                    ISession session = _sessions[0];
                    while (session.SubscriptionCount > 0)
                    {
                        Subscription subscription = session.Subscriptions.First();
                        while (subscription.MonitoredItemCount > 0)
                        {
                            subscription.RemoveItem(subscription.MonitoredItems.First());
                            subscription.ApplyChanges();
                        }
                        Diagnostics.Singleton.Info.NumberOfOpcMonitoredItemsMonitored -= (int)subscription.MonitoredItemCount;

                        session.RemoveSubscription(subscription);
                        Diagnostics.Singleton.Info.NumberOfOpcSubscriptionsConnected--;
                    }

                    string endpoint = session.ConfiguredEndpoint.EndpointUrl.AbsoluteUri;
                    session.Close();
                    _sessions.Remove(session);
                    _complexTypeList.Remove(session);
                    Diagnostics.Singleton.Info.NumberOfOpcSessionsConnected--;

                    _logger.LogInformation("Session to endpoint {endpoint} closed successfully.", endpoint);
                }
            }

            // update our persistency
            if (updatePersistencyFile)
            {
                PersistPublishedNodesAsync().GetAwaiter().GetResult();
            }
        }

        private Subscription CreateSubscription(Session session, ref int publishingInterval)
        {
            Subscription subscription = new Subscription(session.DefaultSubscription) {
                PublishingInterval = publishingInterval,
            };

            // add needs to happen before create to set the Session property
            session.AddSubscription(subscription);
            subscription.Create();

            Diagnostics.Singleton.Info.NumberOfOpcSubscriptionsConnected++;

            _logger.LogInformation("Created subscription with id {id} on endpoint {endpointUrl}.",
                subscription.Id,
                session.Endpoint.EndpointUrl);

            if (publishingInterval != subscription.PublishingInterval)
            {
                _logger.LogInformation("Publishing interval: requested: {requestedPublishingInterval}; revised: {revisedPublishingInterval}",
                    publishingInterval,
                    subscription.PublishingInterval);
            }

            return subscription;
        }

        private void KeepAliveHandler(ISession session, KeepAliveEventArgs eventArgs)
        {
            if (eventArgs != null && session != null && session.ConfiguredEndpoint != null)
            {
                try
                {
                    string endpoint = session.ConfiguredEndpoint.EndpointUrl.AbsoluteUri;

                    lock (_missedKeepAlivesLock)
                    {
                        if (!ServiceResult.IsGood(eventArgs.Status))
                        {
                            _logger.LogWarning("Session endpoint: {endpointUrl} has Status: {status}", session.ConfiguredEndpoint.EndpointUrl, eventArgs.Status);
                            _logger.LogInformation("Outstanding requests: {outstandingRequestCount}, Defunct requests: {defunctRequestCount}", session.OutstandingRequestCount, session.DefunctRequestCount);
                            _logger.LogInformation("Good publish requests: {goodPublishRequestCount}, KeepAlive interval: {keepAliveInterval}", session.GoodPublishRequestCount, session.KeepAliveInterval);
                            _logger.LogInformation("SessionId: {sessionId}", session.SessionId);
                            _logger.LogInformation("Session State: {connected}", session.Connected);

                            if (session.Connected)
                            {
                                // add a new entry, if required
                                if (!_missedKeepAlives.ContainsKey(endpoint))
                                {
                                    _missedKeepAlives.Add(endpoint, 0);
                                }

                                _missedKeepAlives[endpoint]++;
                                _logger.LogInformation("Missed Keep-Alives: {missedKeepAlives}", _missedKeepAlives[endpoint]);
                            }

                            // start reconnect if there are 3 missed keep alives
                            if (_missedKeepAlives[endpoint] >= 3)
                            {
                                // check if a reconnection is already in progress
                                bool reconnectInProgress = false;
                                lock (_reconnectHandlersLock)
                                {
                                    foreach (SessionReconnectHandler handler in _reconnectHandlers)
                                    {
                                        if (ReferenceEquals(handler.Session, session))
                                        {
                                            reconnectInProgress = true;
                                            break;
                                        }
                                    }
                                }

                                if (!reconnectInProgress)
                                {
                                    lock (_sessionLock)
                                    {
                                        _sessions.Remove(session);
                                    }

                                    Diagnostics.Singleton.Info.NumberOfOpcSessionsConnected--;
                                    _logger.LogInformation($"RECONNECTING session {session.SessionId}...");
                                    SessionReconnectHandler reconnectHandler = new SessionReconnectHandler();
                                    lock (_reconnectHandlersLock)
                                    {
                                        _reconnectHandlers.Add(reconnectHandler);
                                    }
                                    reconnectHandler.BeginReconnect(session, 10000, ReconnectCompleteHandler);
                                }
                            }
                        }
                        else
                        {
                            if (_missedKeepAlives.ContainsKey(endpoint) && (_missedKeepAlives[endpoint] != 0))
                            {
                                // Reset missed keep alive count
                                _logger.LogInformation("Session endpoint: {endpoint} got a keep alive after {missedKeepAlives} {verb} missed.",
                                    endpoint,
                                    _missedKeepAlives[endpoint],
                                    _missedKeepAlives[endpoint] == 1 ? "was" : "were");

                                _missedKeepAlives[endpoint] = 0;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Exception in keep alive handling for endpoint {endpointUrl}. {message}",
                       session.ConfiguredEndpoint.EndpointUrl,
                       e.Message);
                }
            }
            else
            {
                _logger.LogWarning("Keep alive arguments invalid.");
            }
        }

        private void ReconnectCompleteHandler(object sender, EventArgs e)
        {
            // find our reconnect handler
            SessionReconnectHandler reconnectHandler = null;
            lock (_reconnectHandlersLock)
            {
                foreach (SessionReconnectHandler handler in _reconnectHandlers)
                {
                    if (ReferenceEquals(sender, handler))
                    {
                        reconnectHandler = handler;
                        break;
                    }
                }
            }

            // ignore callbacks from discarded objects
            if (reconnectHandler == null || reconnectHandler.Session == null)
            {
                return;
            }

            // update the session
            ISession session = reconnectHandler.Session;
            lock (_sessionLock)
            {
                _sessions.Add(session);
            }

            Diagnostics.Singleton.Info.NumberOfOpcSessionsConnected++;
            lock (_reconnectHandlersLock)
            {
                _reconnectHandlers.Remove(reconnectHandler);
            }
            reconnectHandler.Dispose();

            _logger.LogInformation($"RECONNECTED session {session.SessionId}!");
        }

        public async Task<string> PublishNodeAsync(NodePublishingModel nodeToPublish, CancellationToken cancellationToken = default)
        {
            // find or create the session we need to monitor the node
            Session session = await ConnectSessionAsync(
                nodeToPublish.EndpointUrl,
                nodeToPublish.Username,
                nodeToPublish.Password
            ).ConfigureAwait(false);

            if (session == null)
            {
                // couldn't create the session
                throw new Exception($"Could not create session for endpoint {nodeToPublish.EndpointUrl}!");
            }

            Subscription opcSubscription = null;
            try
            {
                // check if there is already a subscription with the same publishing interval, which can be used to monitor the node
                int opcPublishingIntervalForNode = (nodeToPublish.OpcPublishingInterval == 0) ? (int)Settings.Instance.DefaultOpcPublishingInterval : nodeToPublish.OpcPublishingInterval;
                foreach (Subscription subscription in session.Subscriptions)
                {
                    if (subscription.PublishingInterval == opcPublishingIntervalForNode)
                    {
                        opcSubscription = subscription;
                        break;
                    }
                }

                // if there was none found, create one
                if (opcSubscription == null)
                {
                    _logger.LogInformation("PublishNode: No matching subscription with publishing interval of {publishingInterval} found, creating a new one.",
                        nodeToPublish.OpcPublishingInterval);

                    opcSubscription = CreateSubscription(session, ref opcPublishingIntervalForNode);
                }

                // resolve all node and namespace references in the select and where clauses
                EventFilter eventFilter = new EventFilter();
                if ((nodeToPublish.Filter != null) && (nodeToPublish.Filter.Count > 0))
                {
                    List<NodeId> ofTypes = new List<NodeId>();
                    foreach (FilterModel filter in nodeToPublish.Filter)
                    {
                        if (!string.IsNullOrEmpty(filter.OfType))
                        {
                            ofTypes.Add(ExpandedNodeId.ToNodeId(ExpandedNodeId.Parse(filter.OfType), session.NamespaceUris));
                        }
                    }

                    eventFilter.SelectClauses = FilterUtils.ConstructSelectClauses(session);
                    eventFilter.WhereClause = FilterUtils.ConstructWhereClause(ofTypes, EventSeverity.Min);
                }

                // if no nodeid was specified, select the server object root
                NodeId resolvedNodeId;
                if (nodeToPublish.ExpandedNodeId.Identifier == null)
                {
                    _logger.LogWarning("Selecting server root as no expanded node ID specified to publish!");
                    resolvedNodeId = ObjectIds.Server;
                }
                else
                {
                    // generate the resolved NodeId we need for publishing
                    if (nodeToPublish.ExpandedNodeId.ToString().StartsWith("nsu="))
                    {
                        resolvedNodeId = ExpandedNodeId.ToNodeId(nodeToPublish.ExpandedNodeId, session.NamespaceUris);
                    }
                    else
                    {
                        resolvedNodeId = new NodeId(nodeToPublish.ExpandedNodeId.Identifier, nodeToPublish.ExpandedNodeId.NamespaceIndex);
                    }
                }

                // if it is already published, we unpublish first, then we create a new monitored item
                foreach (MonitoredItem monitoredItem in opcSubscription.MonitoredItems)
                {
                    if (monitoredItem.ResolvedNodeId == resolvedNodeId)
                    {
                        opcSubscription.RemoveItem(monitoredItem);
                        opcSubscription.ApplyChanges();
                    }
                }

                int opcSamplingIntervalForNode = (nodeToPublish.OpcSamplingInterval == 0) ? (int)Settings.Instance.DefaultOpcSamplingInterval : nodeToPublish.OpcSamplingInterval;
                MonitoredItem newMonitoredItem = new MonitoredItem(opcSubscription.DefaultItem) {
                    StartNodeId = resolvedNodeId,
                    AttributeId = Attributes.Value,
                    SamplingInterval = opcSamplingIntervalForNode
                };

                if (eventFilter.SelectClauses.Count > 0)
                {
                    // event
                    newMonitoredItem.Notification += _trigger.EventNotificationHandler;
                    newMonitoredItem.AttributeId = Attributes.EventNotifier;
                    newMonitoredItem.Filter = eventFilter;
                }
                else
                {
                    // data change
                    newMonitoredItem.Notification += _trigger.DataChangedNotificationHandler;
                }

                // read display name
                newMonitoredItem.DisplayName = string.Empty;
                Ua.Node node = session.ReadNode(resolvedNodeId);
                if ((node != null) && (node.DisplayName != null))
                {
                    newMonitoredItem.DisplayName = node.DisplayName.Text;
                }

                opcSubscription.AddItem(newMonitoredItem);
                opcSubscription.ApplyChanges();

                // create a heartbeat timer, if required
                if (nodeToPublish.HeartbeatInterval > 0)
                {
                    PeriodicPublishing heartbeat = new PeriodicPublishing(
                        (uint)nodeToPublish.HeartbeatInterval,
                        session,
                        resolvedNodeId,
                        newMonitoredItem.DisplayName,
                        _loggerFactory);

                    lock (_periodicPublishingListLock)
                    {
                        _periodicPublishingList.Add(heartbeat);
                    }
                }

                // create a skip first entry, if required
                if (nodeToPublish.SkipFirst)
                {
                    _trigger.SkipFirst[nodeToPublish.ExpandedNodeId.ToString()] = true;
                }

                _logger.LogDebug("PublishNode: Now monitoring OPC UA node {expandedNodeId} on endpoint {endpointUrl}",
                   nodeToPublish.ExpandedNodeId,
                   session.ConfiguredEndpoint.EndpointUrl);

                Diagnostics.Singleton.Info.NumberOfOpcMonitoredItemsMonitored++;

                // update our persistency
                PersistPublishedNodesAsync().GetAwaiter().GetResult();

                return "Successfully published node " + nodeToPublish.ExpandedNodeId.ToString();
            }
            catch (ServiceResultException sre)
            {
                switch ((uint)sre.Result.StatusCode)
                {
                    case StatusCodes.BadSessionIdInvalid:
                        _logger.LogError("Session with Id {sessionId} is no longer available on endpoint {endpointUrl}. Cleaning up.",
                            session.SessionId,
                            session.ConfiguredEndpoint.EndpointUrl);
                        break;

                    case StatusCodes.BadSubscriptionIdInvalid:
                        _logger.LogError("Subscription with Id {subscription} is no longer available on endpoint {endpointUrl}. Cleaning up.",
                            opcSubscription.Id,
                            session.ConfiguredEndpoint.EndpointUrl);
                        break;

                    case StatusCodes.BadNodeIdInvalid:
                    case StatusCodes.BadNodeIdUnknown:
                        _logger.LogError("Failed to monitor node {expandedNodeId} on endpoint {endpointUrl}.",
                            nodeToPublish.ExpandedNodeId,
                            session.ConfiguredEndpoint.EndpointUrl);

                        _logger.LogError("OPC UA ServiceResultException is {result}. Please check your UA Cloud Publisher configuration for this node.", sre.Result);
                        break;

                    default:
                        _logger.LogError("Unhandled OPC UA ServiceResultException {result} when monitoring node {expandedNodeId} on endpoint {endpointUrl}. Continue.",
                            sre.Result,
                            nodeToPublish.ExpandedNodeId,
                            session.ConfiguredEndpoint.EndpointUrl);
                        break;
                }

                return sre.Message;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "PublishNode: Exception while trying to add node {expandedNodeId} for monitoring.", nodeToPublish.ExpandedNodeId);
                return e.Message;
            }
        }

        public void UnpublishNode(NodePublishingModel nodeToUnpublish)
        {
            // find the required session
            Session session = FindSession(nodeToUnpublish.EndpointUrl);
            if (session == null)
            {
                throw new ArgumentException($"Session for endpoint {nodeToUnpublish.EndpointUrl} no longer exists!");
            }

            // generate the resolved NodeId we need for unpublishing
            NodeId resolvedNodeId;
            if (nodeToUnpublish.ExpandedNodeId.ToString().StartsWith("nsu="))
            {
                resolvedNodeId = ExpandedNodeId.ToNodeId(nodeToUnpublish.ExpandedNodeId, session.NamespaceUris);
            }
            else
            {
                resolvedNodeId = new NodeId(nodeToUnpublish.ExpandedNodeId.Identifier, nodeToUnpublish.ExpandedNodeId.NamespaceIndex);
            }

            // loop through all subscriptions of the session
            foreach (Subscription subscription in session.Subscriptions)
            {
                // loop through all monitored items
                foreach (MonitoredItem monitoredItem in subscription.MonitoredItems)
                {
                    if (monitoredItem.ResolvedNodeId == resolvedNodeId)
                    {
                        subscription.RemoveItem(monitoredItem);
                        subscription.ApplyChanges();

                        Diagnostics.Singleton.Info.NumberOfOpcMonitoredItemsMonitored--;

                        // cleanup empty subscriptions and sessions
                        if (subscription.MonitoredItemCount == 0)
                        {
                            session.RemoveSubscription(subscription);
                            Diagnostics.Singleton.Info.NumberOfOpcSubscriptionsConnected--;
                        }

                        // update our persistency
                        PersistPublishedNodesAsync().GetAwaiter().GetResult();

                        return;
                    }
                }
                break;
            }
        }

        public IEnumerable<PublishNodesInterfaceModel> GetPublishedNodes()
        {
            List<PublishNodesInterfaceModel> publisherConfigurationFileEntries = new List<PublishNodesInterfaceModel>();

            try
            {
                // loop through all sessions
                lock (_sessionLock)
                {
                    foreach (ISession session in _sessions)
                    {
                        UserAuthModeEnum authenticationMode = UserAuthModeEnum.Anonymous;
                        string username = null;
                        string password = null;

                        if (session.Identity.TokenType == UserTokenType.UserName)
                        {
                            authenticationMode = UserAuthModeEnum.UsernamePassword;

                            UserNameIdentityToken token = (UserNameIdentityToken)session.Identity.GetIdentityToken();
                            username = token.UserName;
                            password = token.DecryptedPassword;
                        }

                        PublishNodesInterfaceModel publisherConfigurationFileEntry = new PublishNodesInterfaceModel
                        {
                            EndpointUrl = session.ConfiguredEndpoint.EndpointUrl.AbsoluteUri,
                            OpcAuthenticationMode = authenticationMode,
                            UserName = username,
                            Password = password,
                            OpcNodes = new List<VariableModel>(),
                            OpcEvents = new List<EventModel>()
                        };

                        foreach (Subscription subscription in session.Subscriptions)
                        {
                            foreach (MonitoredItem monitoredItem in subscription.MonitoredItems)
                            {
                                if (monitoredItem.Filter != null)
                                {
                                    // event
                                    EventModel publishedEvent = new EventModel()
                                    {
                                        ExpandedNodeId = NodeId.ToExpandedNodeId(monitoredItem.ResolvedNodeId, monitoredItem.Subscription.Session.NamespaceUris).ToString(),
                                        Filter = new List<FilterModel>()
                                    };

                                    if (monitoredItem.Filter is EventFilter)
                                    {
                                        EventFilter eventFilter = (EventFilter)monitoredItem.Filter;
                                        if (eventFilter.WhereClause != null)
                                        {
                                            foreach (ContentFilterElement whereClauseElement in eventFilter.WhereClause.Elements)
                                            {
                                                if (whereClauseElement.FilterOperator == FilterOperator.OfType)
                                                {
                                                    foreach (ExtensionObject operand in whereClauseElement.FilterOperands)
                                                    {
                                                        FilterModel filter = new FilterModel()
                                                        {
                                                            OfType = NodeId.ToExpandedNodeId(new NodeId(operand.ToString()), monitoredItem.Subscription.Session.NamespaceUris).ToString()
                                                        };

                                                        publishedEvent.Filter.Add(filter);
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    publisherConfigurationFileEntry.OpcEvents.Add(publishedEvent);
                                }
                                else
                                {
                                    // variable
                                    VariableModel publishedVariable = new VariableModel()
                                    {
                                        Id = NodeId.ToExpandedNodeId(monitoredItem.ResolvedNodeId, monitoredItem.Subscription.Session.NamespaceUris).ToString(),
                                        OpcSamplingInterval = monitoredItem.SamplingInterval,
                                        OpcPublishingInterval = subscription.PublishingInterval,
                                        HeartbeatInterval = 0,
                                        SkipFirst = false
                                    };

                                    lock (_periodicPublishingListLock)
                                    {
                                        foreach (PeriodicPublishing heartbeat in _periodicPublishingList)
                                        {
                                            if ((heartbeat.HeartBeatSession == session) && (heartbeat.HeartBeatNodeId == monitoredItem.ResolvedNodeId))
                                            {
                                                publishedVariable.HeartbeatInterval = (int)heartbeat.HeartBeatInterval;
                                                break;
                                            }
                                        }
                                    }

                                    ExpandedNodeId expandedNode = new ExpandedNodeId(monitoredItem.ResolvedNodeId);
                                    if (_trigger.SkipFirst.ContainsKey(expandedNode.ToString()))
                                    {
                                        publishedVariable.SkipFirst = true;
                                    }

                                    publisherConfigurationFileEntry.OpcNodes.Add(publishedVariable);
                                }
                            }
                        }

                        if ((publisherConfigurationFileEntry.OpcEvents.Count > 0) || (publisherConfigurationFileEntry.OpcNodes.Count > 0))
                        {
                            publisherConfigurationFileEntries.Add(publisherConfigurationFileEntry);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Reading configuration file entries failed.");
                return null;
            }

            return publisherConfigurationFileEntries;
        }

        private async Task PersistPublishedNodesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // iterate through all sessions, subscriptions and monitored items and create config file entries
                IEnumerable<PublishNodesInterfaceModel> publisherNodeConfiguration = GetPublishedNodes();

                // update the persistency file
                if (await _storage.StoreFileAsync(Path.Combine(Directory.GetCurrentDirectory(), "settings", "persistency.json"), Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(publisherNodeConfiguration, Formatting.Indented)), cancellationToken).ConfigureAwait(false) == null)
                {
                    _logger.LogError("Could not store persistency file. Published nodes won't be persisted!");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update of persistency file failed.");
            }
        }

        public static ReferenceDescriptionCollection Browse(Session session, BrowseDescription nodeToBrowse, bool throwOnError)
        {
            try
            {
                ReferenceDescriptionCollection references = new ReferenceDescriptionCollection();

                // construct browse request.
                BrowseDescriptionCollection nodesToBrowse = new BrowseDescriptionCollection
                {
                    nodeToBrowse
                };

                // start the browse operation.
                session.Browse(
                    null,
                    null,
                    0,
                    nodesToBrowse,
                    out BrowseResultCollection results,
                    out DiagnosticInfoCollection diagnosticInfos);

                ClientBase.ValidateResponse(results, nodesToBrowse);
                ClientBase.ValidateDiagnosticInfos(diagnosticInfos, nodesToBrowse);

                do
                {
                    // check for error.
                    if (StatusCode.IsBad(results[0].StatusCode))
                    {
                        break;
                    }

                    // process results.
                    for (int i = 0; i < results[0].References.Count; i++)
                    {
                        references.Add(results[0].References[i]);
                    }

                    // check if all references have been fetched.
                    if (results[0].References.Count == 0 || results[0].ContinuationPoint == null)
                    {
                        break;
                    }

                    // continue browse operation.
                    ByteStringCollection continuationPoints = new ByteStringCollection
                    {
                        results[0].ContinuationPoint
                    };

                    session.BrowseNext(
                        null,
                        false,
                        continuationPoints,
                        out results,
                        out diagnosticInfos);

                    ClientBase.ValidateResponse(results, continuationPoints);
                    ClientBase.ValidateDiagnosticInfos(diagnosticInfos, continuationPoints);
                }
                while (true);

                // return complete list.
                return references;
            }
            catch (Exception exception)
            {
                if (throwOnError)
                {
                    throw new ServiceResultException(exception, StatusCodes.BadUnexpectedError);
                }

                return null;
            }
        }

        public void WoTConUpload(string endpoint, byte[] bytes, string assetName)
        {
            Session session = null;
            NodeId fileId = null;
            object fileHandle = null;
            try
            {
                session = ConnectSessionAsync(endpoint, null, null).GetAwaiter().GetResult();
                if (session == null)
                {
                    // couldn't create the session
                    throw new Exception($"Could not create session for endpoint {endpoint}!");
                }

                NodeId createNodeId = new(WoTAssetConnectionManagement_CreateAsset, (ushort)session.NamespaceUris.GetIndex("http://opcfoundation.org/UA/WoT-Con/"));
                NodeId deleteNodeId = new(WoTAssetConnectionManagement_DeleteAsset, (ushort)session.NamespaceUris.GetIndex("http://opcfoundation.org/UA/WoT-Con/"));
                NodeId closeNodeId = new(WoTAssetFileType_CloseAndUpdate, (ushort)session.NamespaceUris.GetIndex("http://opcfoundation.org/UA/WoT-Con/"));
                NodeId parentNodeId = new(WoTAssetConnectionManagement, (ushort)session.NamespaceUris.GetIndex("http://opcfoundation.org/UA/WoT-Con/"));

                Variant assetId = new(string.Empty);

                StatusCode status = new StatusCode(0);
                assetId = ExecuteCommand(session, createNodeId, parentNodeId, assetName, null, out status);
                if (StatusCode.IsNotGood(status))
                {
                    if (status == StatusCodes.BadBrowseNameDuplicated)
                    {
                        // delete existing asset first
                        assetId = ExecuteCommand(session, deleteNodeId, parentNodeId, new NodeId(assetId.Value.ToString()), null, out status);
                        if (StatusCode.IsNotGood(status))
                        {
                            throw new Exception(status.ToString());
                        }

                        // now try again
                        assetId = ExecuteCommand(session, createNodeId, parentNodeId, assetName, null, out status);
                        if (StatusCode.IsNotGood(status))
                        {
                            throw new Exception(status.ToString());
                        }
                    }
                    else
                    {
                        throw new Exception(status.ToString());
                    }
                }

                BrowseDescription nodeToBrowse = new()
                {
                    NodeId = (NodeId)assetId.Value,
                    BrowseDirection = BrowseDirection.Forward,
                    NodeClassMask = (uint)NodeClass.Object,
                    ResultMask = (uint)BrowseResultMask.All
                };

                ReferenceDescriptionCollection references = Browse(session, nodeToBrowse, true);

                fileId = (NodeId)references[0].NodeId;
                fileHandle = ExecuteCommand(session, MethodIds.FileType_Open, fileId, (byte)6, null, out status);
                if (StatusCode.IsNotGood(status))
                {
                    throw new Exception(status.ToString());
                }

                for (int i = 0; i < bytes.Length; i += 3000)
                {
                    byte[] chunk = bytes.AsSpan(i, Math.Min(3000, bytes.Length - i)).ToArray();

                    ExecuteCommand(session, MethodIds.FileType_Write, fileId, fileHandle, chunk, out status);
                    if (StatusCode.IsNotGood(status))
                    {
                        throw new Exception(status.ToString());
                    }
                }

                Variant result = ExecuteCommand(session, closeNodeId, fileId, fileHandle, null, out status);
                if (StatusCode.IsNotGood(status))
                {
                    throw new Exception(status.ToString() + ": " + result.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);

                if ((session != null) && (fileId != null) && (fileHandle != null))
                {
                    ExecuteCommand(session, MethodIds.FileType_Close, fileId, fileHandle, null, out StatusCode status);
                }

                throw;
            }
            finally
            {
                if (session != null)
                {
                    if (session.Connected)
                    {
                        session.Close();
                    }

                    session.Dispose();
                }
            }
        }

        private Variant ExecuteCommand(Session session, NodeId nodeId, NodeId parentNodeId, object argument1, object argument2, out StatusCode status)
        {
            try
            {
                CallMethodRequestCollection requests = new CallMethodRequestCollection
                {
                    new CallMethodRequest
                    {
                        ObjectId = parentNodeId,
                        MethodId = nodeId,
                        InputArguments = new VariantCollection { new Variant(argument1) }
                    }
                };

                if (argument1 != null)
                {
                    requests[0].InputArguments = new VariantCollection { new Variant(argument1) };
                }

                if ((argument1 != null) && (argument2 != null))
                {
                    requests[0].InputArguments.Add(new Variant(argument2));
                }

                CallMethodResultCollection results;
                DiagnosticInfoCollection diagnosticInfos;

                ResponseHeader responseHeader = session.Call(
                    null,
                    requests,
                    out results,
                    out diagnosticInfos);

                ClientBase.ValidateResponse(results, requests);
                ClientBase.ValidateDiagnosticInfos(diagnosticInfos, requests);

                status = new StatusCode(0);
                if ((results != null) && (results.Count > 0))
                {
                    status = results[0].StatusCode;

                    if (StatusCode.IsBad(results[0].StatusCode) && (responseHeader.StringTable != null) && (responseHeader.StringTable.Count > 0))
                    {
                        return responseHeader.StringTable[0];
                    }

                    if ((results[0].OutputArguments != null) && (results[0].OutputArguments.Count > 0))
                    {
                        return results[0].OutputArguments[0];
                    }
                }

                return new Variant(string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Executing OPC UA command failed!");
                throw;
            }
        }
    }
}
