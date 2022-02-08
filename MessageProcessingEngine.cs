﻿
namespace UA.MQTT.Publisher
{
    using Microsoft.Extensions.Logging;
    using Opc.Ua;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;
    using UA.MQTT.Publisher.Configuration;
    using UA.MQTT.Publisher.Interfaces;
    using UA.MQTT.Publisher.Models;

    public class MessageProcessingEngine : IMessageProcessingEngine
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <returns></returns>
        public MessageProcessingEngine(
            IPeriodicDiagnosticsInfo diag,
            IMessageEncoder encoder,
            ILoggerFactory loggerFactory,
            ISettingsConfiguration settingsConfiguration,
            IMessageSink sink)
        {
            _logger = loggerFactory.CreateLogger("MessageProcessingEngine");
            _diag = diag;
            _encoder = encoder;
            _settingsConfiguration = settingsConfiguration;
            _sink = sink;
        }

        /// <summary>
        /// Cleanup of unmanaged resources
        /// </summary>
        public void Dispose()
        {
            _batchBuffer.Dispose();

            if (_monitoredItemsDataQueue != null)
            {
                _monitoredItemsDataQueue.Dispose();
            }
        }

        /// <summary>
        /// Enqueue a message for sending to IoT Hub.
        /// </summary>
        public static void Enqueue(MessageDataModel json)
        {
            if (_monitoredItemsDataQueue != null)
            {
                if (_monitoredItemsDataQueue.TryAdd(json) == false)
                {
                    _diag.Info.EnqueueFailureCount++;

                    // log an error message for every 10K messages lost
                    if (_diag.Info.EnqueueFailureCount % 10000 == 0)
                    {
                        _logger.LogError($"The internal monitored item message queue is above its capacity of {_monitoredItemsDataQueue.BoundedCapacity}. We have lost {_diag.Info.EnqueueFailureCount} monitored item notifications so far.");
                    }
                }
                else
                {
                    _diag.Info.EnqueueCount++;
                    _diag.Info.MonitoredItemsQueueCount++;
                }
            }
        }

        /// <summary>
        /// Dequeue monitored item notification messages, batch them for send (if needed) and send them
        /// </summary>
        public void Run(CancellationToken cancellationToken = default)
        {
            if (_isRunning)
            {
                _logger.LogError("Message Processor Engine is already running.");
                return;
            }

            Init();
            _isRunning = true;

            while (true)
            {
                try
                {
                    // read the next message from our queue
                    MessageDataModel messageData = new MessageDataModel();
                    int timeout = CalculateBatchTimeout(cancellationToken);
                    bool gotItem = _monitoredItemsDataQueue.TryTake(out messageData, timeout, cancellationToken);
                    if (!gotItem)
                    {
                        // timeout or shutdown case (cancellation)
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogInformation($"Cancellation requested.");
                            _monitoredItemsDataQueue.CompleteAdding();
                            break;
                        }
                        else
                        {
                            // timeout (i.e. send interval reached). Check if there is something in the buffer and send it now
                            _logger.LogTrace($"Send interval reached at {_nextSendTime}");
                            if (!_batchEmpty)
                            {
                                // send what we have so far
                                SendBatch(FinishBatch());
                                continue;
                            }
                            else
                            {
                                // nothing to send, reset the clock and keep waiting
                                _logger.LogTrace("Adding {seconds} seconds to current nextSendTime {nextSendTime}...", _settingsConfiguration.DefaultSendIntervalSeconds, _nextSendTime);
                                _nextSendTime += TimeSpan.FromSeconds(_settingsConfiguration.DefaultSendIntervalSeconds);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        _diag.Info.MonitoredItemsQueueCount--;
                    }

                    // check if we should send the new item straight away
                    if (_singleMessageSend)
                    {
                        BatchMessage(JsonEncodeMessage(messageData));
                        SendBatch(FinishBatch());
                    }
                    else
                    {
                        // batch message instead
                        string jsonMessage = JsonEncodeMessage(messageData);
                        int jsonMessageSize = Encoding.UTF8.GetByteCount(jsonMessage);
                        uint hubMessageBufferSize = _settingsConfiguration.HubMessageSize > 0 ? _settingsConfiguration.HubMessageSize : SettingsConfiguration.HubMessageSizeMax;

                        // TODO: Try to get the size occupied by the encoded message properties reliably from the IoT Hub client
                        int encodedMessagePropertiesLengthMax = 512;

                        // reduce the IoT Hub message payload by the space occupied by the message properties
                        hubMessageBufferSize -= (uint)encodedMessagePropertiesLengthMax;

                        // check if the message will fit into our batch in principle
                        if (jsonMessageSize > hubMessageBufferSize)
                        {
                            _logger.LogError($"Configured hub message size {hubMessageBufferSize} too small to even fit the generated telemetry message of {jsonMessageSize}. Please adjust. The telemetry message will be discarded!");
                            _diag.Info.TooLargeCount++;
                            continue;
                        }

                        // check if the message still fits into out batch, otherwise send what we have so far and start a new batch with the message
                        if ((_batchBuffer.Position + jsonMessageSize + _messageClosingParenthesisSize) < hubMessageBufferSize)
                        {
                            BatchMessage(jsonMessage);
                        }
                        else
                        {
                            SendBatch(FinishBatch());
                            BatchMessage(jsonMessage);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                    {
                        throw;
                    }
                    else
                    {
                        _logger.LogError(ex, "Error while processing messages, discarding!");
                    }
                }
            }
        }

        private void Init()
        {
            _logger.LogInformation($"Message processing configured with a send interval of {_settingsConfiguration.DefaultSendIntervalSeconds} sec and a message buffer size of {_settingsConfiguration.HubMessageSize} bytes.");

            // create the queue for monitored items
            _monitoredItemsDataQueue = new BlockingCollection<MessageDataModel>(_settingsConfiguration.MonitoredItemsQueueCapacity);

            _singleMessageSend = _settingsConfiguration.DefaultSendIntervalSeconds == 0 && _settingsConfiguration.HubMessageSize == 0;

            _messageClosingParenthesisSize = 2;
            
            InitBatch();

            // init our send time
            _nextSendTime = DateTime.UtcNow + TimeSpan.FromSeconds(_settingsConfiguration.DefaultSendIntervalSeconds);
        }

        private void BatchMessage(string jsonMessage)
        {
            _batchBuffer.Write(Encoding.UTF8.GetBytes(jsonMessage));
            _batchBuffer.Write(Encoding.UTF8.GetBytes(","));
            _logger.LogDebug($"Batching message with size {Encoding.UTF8.GetByteCount(jsonMessage)}, size is now {_batchBuffer.Position - 1}.");
            _batchEmpty = false;
            _notificationsInBatch++;
        }

        private byte[] FinishBatch()
        {
            // remove the trailing comma and finish the JSON message
            _batchBuffer.Position -= 1;

            _batchBuffer.Write(Encoding.UTF8.GetBytes("]}"));
            
            _lastNotificationInBatch.Enqueue(_notificationsInBatch);

            // calc the average for the last 100 batches
            if (_lastNotificationInBatch.Count > 100)
            {
                _lastNotificationInBatch.Dequeue();
            }

            long sum = 0;
            foreach (long notificationInBatch in _lastNotificationInBatch)
            {
                sum += notificationInBatch;
            }

            _diag.Info.AverageNotificationsInHubMessage = sum / _lastNotificationInBatch.Count;

            return _batchBuffer.ToArray();
        }

        private void SendBatch(byte[] bytesToSend)
        {
            _sink.SendMessage(bytesToSend);

            _diag.Info.SentBytes += bytesToSend.Length;
            _diag.Info.SentMessages++;
            _diag.Info.SentLastTime = DateTime.UtcNow;
            _logger.LogDebug($"Sent {bytesToSend.Length} bytes to hub!");

            // reset our batch
            InitBatch();

            // reset our send time
            _nextSendTime = DateTime.UtcNow + TimeSpan.FromSeconds(_settingsConfiguration.DefaultSendIntervalSeconds);
        }

        private string JsonEncodeMessage(MessageDataModel messageData)
        {
            string jsonMessage = string.Empty;

            EventMessageDataModel eventMessageData = null;
            MessageDataModel dataChangeMessageData = null;
            if (messageData is EventMessageDataModel model)
            {
                eventMessageData = model;
            }
            else
            {
                dataChangeMessageData = messageData;
            }

            // encode message
            if (dataChangeMessageData != null)
            {
                jsonMessage = _encoder.EncodeDataChange(dataChangeMessageData);
            }
            if (eventMessageData != null)
            {
                jsonMessage = _encoder.EncodeEvent(eventMessageData);
            }

            _diag.Info.NumberOfEvents++;

            return jsonMessage;
        }

        private int CalculateBatchTimeout(CancellationToken cancellationToken = default)
        {
            int timeout;

            // sanity check the send interval
            if (_settingsConfiguration.DefaultSendIntervalSeconds > 0)
            {
                TimeSpan timeTillNextSend = _nextSendTime.Subtract(DateTime.UtcNow);
                if (timeTillNextSend < TimeSpan.Zero)
                {
                    _diag.Info.MissedSendIntervalCount++;

                    // no wait if the send interval was missed
                    timeTillNextSend = TimeSpan.Zero;
                }

                long millisLong = (long)timeTillNextSend.TotalMilliseconds;
                if (millisLong < 0 || millisLong > int.MaxValue)
                {
                    timeout = 0;
                }
                else
                {
                    timeout = (int)millisLong;
                }
            }
            else
            {
                // no wait if shutdown is requested, else infinite wait if send interval is not set
                timeout = cancellationToken.IsCancellationRequested ? 0 : Timeout.Infinite;
            }

            return timeout;
        }

        private void InitBatch()
        {
            _batchEmpty = true;
            _batchBuffer.Position = 0;
            _batchBuffer.SetLength(0);
            _notificationsInBatch = 0;

            // add PubSub JSON network message header (the mandatory fields of the OPC UA PubSub JSON NetworkMessage definition)
            // see https://reference.opcfoundation.org/v104/Core/docs/Part14/7.2.3/#7.2.3.2
            JsonEncoder encoder = new JsonEncoder(new ServiceMessageContext(), _settingsConfiguration.ReversiblePubSubEncoding);

            encoder.WriteString("MessageId", _messageID++.ToString());
            encoder.WriteString("MessageType", "ua-data");
            encoder.WriteString("PublisherId", _settingsConfiguration.PublisherName);
            encoder.PushArray("Messages");

            // remove the closing bracket as we will add this later
            string pubSubJSONNetworkMessageHeader = encoder.CloseAndReturnText().TrimEnd('}');

            _batchBuffer.Write(Encoding.UTF8.GetBytes(pubSubJSONNetworkMessageHeader));
        }

        private static ulong _messageID = 0;
        private bool _batchEmpty = true;
        private bool _singleMessageSend = false;
        private int _messageClosingParenthesisSize = 1;
        DateTime _nextSendTime = DateTime.UtcNow;

        private Queue<long> _lastNotificationInBatch = new Queue<long>();
        private int _notificationsInBatch = 0;

        MemoryStream _batchBuffer = new MemoryStream(); // turn this into a FileStream to persist the batch cache
        private static BlockingCollection<MessageDataModel> _monitoredItemsDataQueue;
        private bool _isRunning = false;

        private static ILogger _logger;
        private static IPeriodicDiagnosticsInfo _diag;
        private readonly IMessageEncoder _encoder;
        private readonly ISettingsConfiguration _settingsConfiguration;
        private readonly IMessageSink _sink;
    }
}