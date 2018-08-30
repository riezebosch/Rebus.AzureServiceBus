﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Azure.ServiceBus.Management;
using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Extensions;
using Rebus.Internals;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Subscriptions;
using Rebus.Threading;
using Rebus.Transport;
using Message = Microsoft.Azure.ServiceBus.Message;
// ReSharper disable RedundantArgumentDefaultValue
// ReSharper disable ArgumentsStyleNamedExpression
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleLiteral
#pragma warning disable 1998

namespace Rebus.AzureServiceBus
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses Azure Service Bus queues to send/receive messages.
    /// </summary>
    public class AzureServiceBusTransport : ITransport, IInitializable, IDisposable, ISubscriptionStorage
    {
        /// <summary>
        /// Outgoing messages are stashed in a concurrent queue under this key
        /// </summary>
        const string OutgoingMessagesKey = "new-azure-service-bus-transport";

        /// <summary>
        /// Subscriber "addresses" are prefixed with this bad boy so we can recognize them and publish to a topic client instead
        /// </summary>
        const string MagicSubscriptionPrefix = "***Topic***: ";

        /// <summary>
        /// Defines the maximum number of outgoing messages to batch together when sending/publishing
        /// </summary>
        const int DefaultOutgoingBatchSize = 50;

        static readonly RetryExponential DefaultRetryStrategy = new RetryExponential(
            minimumBackoff: TimeSpan.FromMilliseconds(100),
            maximumBackoff: TimeSpan.FromSeconds(10),
            maximumRetryCount: 10
        );

        readonly ConcurrentStack<IDisposable> _disposables = new ConcurrentStack<IDisposable>();
        readonly ConcurrentDictionary<string, MessageSender> _messageSenders = new ConcurrentDictionary<string, MessageSender>();
        readonly ConcurrentDictionary<string, TopicClient> _topicClients = new ConcurrentDictionary<string, TopicClient>();
        readonly ConcurrentDictionary<string, string[]> _cachedSubscriberAddresses = new ConcurrentDictionary<string, string[]>();
        readonly IAsyncTaskFactory _asyncTaskFactory;
        readonly ManagementClient _managementClient;
        readonly string _connectionString;
        readonly TimeSpan? _receiveTimeout;
        readonly ILog _log;

        bool _prefetchingEnabled;
        int _prefetchCount;

        MessageReceiver _messageReceiver;

        /// <summary>
        /// Constructs the transport, connecting to the service bus pointed to by the connection string.
        /// </summary>
        public AzureServiceBusTransport(string connectionString, string queueName, IRebusLoggerFactory rebusLoggerFactory, IAsyncTaskFactory asyncTaskFactory)
        {
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));

            Address = queueName?.ToLowerInvariant();

            if (Address != null)
            {
                if (Address.StartsWith(MagicSubscriptionPrefix))
                {
                    throw new ArgumentException($"Sorry, but the queue name '{queueName}' cannot be used because it conflicts with Rebus' internally used 'magic subscription prefix': '{MagicSubscriptionPrefix}'. ");
                }
            }

            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _asyncTaskFactory = asyncTaskFactory ?? throw new ArgumentNullException(nameof(asyncTaskFactory));
            _log = rebusLoggerFactory.GetLogger<AzureServiceBusTransport>();
            _managementClient = new ManagementClient(connectionString);

            _receiveTimeout = _connectionString.Contains("OperationTimeout")
                ? default(TimeSpan?)
                : TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Gets "subscriber addresses" by getting one single magic "queue name", which is then
        /// interpreted as a publish operation to a topic when the time comes to send to that "queue"
        /// </summary>
        public async Task<string[]> GetSubscriberAddresses(string topic)
        {
            return _cachedSubscriberAddresses.GetOrAdd(topic, _ =>
            {
                var normalizedTopic = topic.ToValidAzureServiceBusEntityName();

                return new[] { $"{MagicSubscriptionPrefix}{normalizedTopic}" };
            });
        }

        /// <summary>
        /// Registers this endpoint as a subscriber by creating a subscription for the given topic, setting up
        /// auto-forwarding from that subscription to this endpoint's input queue
        /// </summary>
        public async Task RegisterSubscriber(string topic, string subscriberAddress)
        {
            VerifyIsOwnInputQueueAddress(subscriberAddress);

            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();
            var topicDescription = await EnsureTopicExists(normalizedTopic).ConfigureAwait(false);
            var messageSender = GetMessageSender(Address);

            var inputQueuePath = messageSender.Path;
            var topicPath = topicDescription.Path;
            var subscriptionName = GetSubscriptionName();

            var subscription = await GetOrCreateSubscription(topicPath, subscriptionName).ConfigureAwait(false);

            subscription.ForwardTo = inputQueuePath;

            await _managementClient.UpdateSubscriptionAsync(subscription).ConfigureAwait(false);
        }

        /// <summary>
        /// Unregisters this endpoint as a subscriber by deleting the subscription for the given topic
        /// </summary>
        public async Task UnregisterSubscriber(string topic, string subscriberAddress)
        {
            VerifyIsOwnInputQueueAddress(subscriberAddress);

            var normalizedTopic = topic.ToValidAzureServiceBusEntityName();
            var topicDescription = await EnsureTopicExists(normalizedTopic).ConfigureAwait(false);
            var topicPath = topicDescription.Path;
            var subscriptionName = GetSubscriptionName();

            try
            {
                await _managementClient.DeleteSubscriptionAsync(topicPath, subscriptionName).ConfigureAwait(false);
            }
            catch (MessagingEntityNotFoundException)
            {
                // it's alright man
            }
        }

        async Task<SubscriptionDescription> GetOrCreateSubscription(string topicPath, string subscriptionName)
        {
            try
            {
                return await _managementClient.CreateSubscriptionAsync(topicPath, subscriptionName).ConfigureAwait(false);
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                return await _managementClient.GetSubscriptionAsync(topicPath, subscriptionName).ConfigureAwait(false);
            }
        }

        string GetSubscriptionName()
        {
            var idx = Address.LastIndexOf("/", StringComparison.Ordinal) + 1;

            return Address.Substring(idx).ToValidAzureServiceBusEntityName();
        }

        void VerifyIsOwnInputQueueAddress(string subscriberAddress)
        {
            if (subscriberAddress == Address) return;

            var message = $"Cannot register subscriptions endpoint with input queue '{subscriberAddress}' in endpoint with input" +
                          $" queue '{Address}'! The Azure Service Bus transport functions as a centralized subscription" +
                          " storage, which means that all subscribers are capable of managing their own subscriptions";

            throw new ArgumentException(message);
        }

        async Task<TopicDescription> EnsureTopicExists(string normalizedTopic)
        {
            try
            {
                return await _managementClient.CreateTopicAsync(normalizedTopic).ConfigureAwait(false);
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                return await _managementClient.GetTopicAsync(normalizedTopic).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new ArgumentException($"Could not create topic '{normalizedTopic}'", exception);
            }
        }

        /// <summary>
        /// Creates a queue with the given address
        /// </summary>
        public void CreateQueue(string address)
        {
            QueueDescription GetInputQueueDescription()
            {
                var queueDescription = new QueueDescription(address);

                // if it's the input queue, do this:
                if (address == Address)
                {
                    // must be set when the queue is first created
                    queueDescription.EnablePartitioning = PartitioningEnabled;

                    if (MessagePeekLockDuration.HasValue)
                    {
                        queueDescription.LockDuration = MessagePeekLockDuration.Value;
                    }

                    if (MessageTimeToLive.HasValue)
                    {
                        queueDescription.DefaultMessageTimeToLive = MessageTimeToLive.Value;
                    }
                }

                return queueDescription;
            }

            if (DoNotCreateQueuesEnabled)
            {
                _log.Info("Transport configured to not create queue - skipping existence check and potential creation for {queueName}", address);
                return;
            }

            AsyncHelpers.RunSync(async () =>
            {
                if (await _managementClient.QueueExistsAsync(address).ConfigureAwait(false)) return;

                try
                {
                    _log.Info("Creating ASB queue {queueName}", address);

                    var queueDescription = GetInputQueueDescription();

                    await _managementClient.CreateQueueAsync(queueDescription).ConfigureAwait(false);
                }
                catch (MessagingEntityAlreadyExistsException)
                {
                    // it's alright man
                }
                catch (Exception exception)
                {
                    throw new ArgumentException($"Could not create Azure Service Bus queue '{address}'", exception);
                }
            });
        }

        void CheckInputQueueConfiguration(string address)
        {
            AsyncHelpers.RunSync(async () =>
            {
                var queueDescription = await GetQueueDescription(address).ConfigureAwait(false);

                if (queueDescription.EnablePartitioning != PartitioningEnabled)
                {
                    _log.Warn("The queue {queueName} has EnablePartitioning={enablePartitioning}, but the transport has PartitioningEnabled={partitioningEnabled}. As this setting cannot be changed after the queue is created, please either make sure the Rebus transport settings are consistent with the queue settings, or delete the queue and let Rebus create it again with the new settings.",
                        address, queueDescription.EnablePartitioning, PartitioningEnabled);
                }

                var updates = new List<string>();

                if (MessageTimeToLive.HasValue)
                {
                    var messageTimeToLive = MessageTimeToLive.Value;
                    if (queueDescription.DefaultMessageTimeToLive != messageTimeToLive)
                    {
                        queueDescription.DefaultMessageTimeToLive = messageTimeToLive;
                        updates.Add($"DefaultMessageTimeToLive = {messageTimeToLive}");
                    }
                }

                if (MessagePeekLockDuration.HasValue)
                {
                    var messagePeekLockDuration = MessagePeekLockDuration.Value;
                    if (queueDescription.LockDuration != messagePeekLockDuration)
                    {
                        queueDescription.LockDuration = messagePeekLockDuration;
                        updates.Add($"LockDuration = {MessagePeekLockDuration}");
                    }
                }

                if (!updates.Any()) return;

                if (DoNotCreateQueuesEnabled)
                {
                    _log.Warn("Detected changes in the settings for the queue {queueName}: {updates} - but the transport is configured to NOT create queues, so no settings will be changed", address, updates);
                    return;
                }

                _log.Info("Updating ASB queue {queueName}: {updates}", address, updates);
                await _managementClient.UpdateQueueAsync(queueDescription);
            });
        }

        async Task<QueueDescription> GetQueueDescription(string address)
        {
            try
            {
                return await _managementClient.GetQueueAsync(address).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new RebusApplicationException(exception, $"Could not get queue description for queue {address}");
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// Sends the given message to the queue with the given <paramref name="destinationAddress" />
        /// </summary>
        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            var outgoingMessages = GetOutgoingMessages(context);

            outgoingMessages.Enqueue(new OutgoingMessage(destinationAddress, message));
        }

        static Message GetMessage(OutgoingMessage outgoingMessage)
        {
            var transportMessage = outgoingMessage.TransportMessage;
            var message = new Message(transportMessage.Body);
            var headers = transportMessage.Headers.Clone();

            if (headers.TryGetValue(Headers.TimeToBeReceived, out var timeToBeReceivedStr))
            {
                timeToBeReceivedStr = headers[Headers.TimeToBeReceived];
                var timeToBeReceived = TimeSpan.Parse(timeToBeReceivedStr);
                message.TimeToLive = timeToBeReceived;
                headers.Remove(Headers.TimeToBeReceived);
            }

            if (headers.TryGetValue(Headers.DeferredUntil, out var deferUntilTime))
            {
                var deferUntilDateTimeOffset = deferUntilTime.ToDateTimeOffset();
                message.ScheduledEnqueueTimeUtc = deferUntilDateTimeOffset.UtcDateTime;
                headers.Remove(Headers.DeferredUntil);
            }

            if (headers.TryGetValue(Headers.ContentType, out var contentType))
            {
                message.ContentType = contentType;
            }

            if (headers.TryGetValue(Headers.CorrelationId, out var correlationId))
            {
                message.CorrelationId = correlationId;
            }

            if (headers.TryGetValue(Headers.MessageId, out var messageId))
            {
                message.MessageId = messageId;
            }

            message.Label = transportMessage.GetMessageLabel();

            foreach (var kvp in headers)
            {
                message.UserProperties[kvp.Key] = kvp.Value;
            }

            return message;
        }

        ConcurrentQueue<OutgoingMessage> GetOutgoingMessages(ITransactionContext context)
        {
            return context.GetOrAdd(OutgoingMessagesKey, () =>
            {
                var messagesToSend = new ConcurrentQueue<OutgoingMessage>();

                context.OnCommitted(async () =>
                {
                    var messagesByDestinationQueue = messagesToSend.GroupBy(m => m.DestinationAddress);

                    await Task.WhenAll(messagesByDestinationQueue.Select(async group =>
                    {
                        var destinationQueue = group.Key;
                        var messages = group;

                        if (destinationQueue.StartsWith(MagicSubscriptionPrefix))
                        {
                            var topicName = destinationQueue.Substring(MagicSubscriptionPrefix.Length);

                            foreach (var batch in messages.Batch(DefaultOutgoingBatchSize))
                            {
                                var list = batch.Select(GetMessage).ToList();

                                try
                                {
                                    await GetTopicClient(topicName).SendAsync(list).ConfigureAwait(false);
                                }
                                catch (Exception exception)
                                {
                                    throw new RebusApplicationException(exception, $"Could not publish to topic '{topicName}'");
                                }
                            }
                        }
                        else
                        {
                            foreach (var batch in messages.Batch(DefaultOutgoingBatchSize))
                            {
                                var list = batch.Select(GetMessage).ToList();

                                try
                                {
                                    await GetMessageSender(destinationQueue).SendAsync(list).ConfigureAwait(false);
                                }
                                catch (Exception exception)
                                {
                                    throw new RebusApplicationException(exception, $"Could not send to queue '{destinationQueue}'");
                                }
                            }
                        }

                    })).ConfigureAwait(false);
                });

                return messagesToSend;
            });
        }

        /// <summary>
        /// Receives the next message from the input queue. Returns null if no message was available
        /// </summary>
        public async Task<TransportMessage> Receive(ITransactionContext context, CancellationToken cancellationToken)
        {
            var message = await ReceiveInternal().ConfigureAwait(false);

            if (message == null) return null;

            if (!message.SystemProperties.IsLockTokenSet)
            {
                throw new RebusApplicationException($"OMG that's weird - message with ID {message.MessageId} does not have a lock token!");
            }

            var lockToken = message.SystemProperties.LockToken;
            var messageId = message.MessageId;

            if (AutomaticallyRenewPeekLock && !_prefetchingEnabled)
            {
                var now = DateTime.UtcNow;
                var leaseDuration = message.SystemProperties.LockedUntilUtc - now;
                var lockRenewalInterval = TimeSpan.FromMinutes(0.7 * leaseDuration.TotalMinutes);

                var renewalTask = _asyncTaskFactory
                    .Create($"RenewPeekLock-{messageId}",
                        async () =>
                        {
                            await RenewPeekLock(messageId, lockToken).ConfigureAwait(false);
                        },
                        intervalSeconds: (int)lockRenewalInterval.TotalSeconds,
                        prettyInsignificant: true);

                context.OnCommitted(async () => renewalTask.Dispose());

                renewalTask.Start();
            }

            context.OnCompleted(async () =>
            {
                try
                {
                    await _messageReceiver.CompleteAsync(lockToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw new RebusApplicationException(exception,
                        $"Could not complete message with ID {message.MessageId} and lock token {lockToken}");
                }
            });

            context.OnAborted(() =>
            {
                try
                {
                    AsyncHelpers.RunSync(async () => await _messageReceiver.AbandonAsync(lockToken).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    throw new RebusApplicationException(exception,
                        $"Could not abandon message with ID {message.MessageId} and lock token {lockToken}");
                }
            });

            var userProperties = message.UserProperties;
            var headers = userProperties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());
            var body = message.Body;

            return new TransportMessage(headers, body);
        }

        async Task<Message> ReceiveInternal()
        {
            try
            {
                return _receiveTimeout.HasValue
                    ? await _messageReceiver.ReceiveAsync(_receiveTimeout.Value).ConfigureAwait(false)
                    : await _messageReceiver.ReceiveAsync().ConfigureAwait(false);
            }
            catch (MessagingEntityNotFoundException exception)
            {
                throw new RebusApplicationException(exception, $"Could not receive next message from Azure Service Bus queue '{Address}'");
            }
        }

        async Task RenewPeekLock(string messageId, string lockToken)
        {
            _log.Info("Renewing peek lock for message with ID {messageId}", messageId);

            try
            {
                await _messageReceiver.RenewLockAsync(lockToken).ConfigureAwait(false);
            }
            catch (MessageLockLostException exception)
            {
                // if we get this, it is probably because the message has been handled
                _log.Error(exception, "Could not renew lock for message with ID {messageId} and lock token {lockToken}", messageId, lockToken);
            }
        }

        /// <summary>
        /// Gets the input queue name for this transport
        /// </summary>
        public string Address { get; }

        /// <summary>
        /// Initializes the transport by ensuring that the input queue has been created
        /// </summary>
        /// <inheritdoc />
        public void Initialize()
        {
            if (Address != null)
            {
                _log.Info("Initializing Azure Service Bus transport with queue {queueName}", Address);

                CreateQueue(Address);

                CheckInputQueueConfiguration(Address);

                _messageReceiver = new MessageReceiver(
                    _connectionString,
                    Address,
                    receiveMode: ReceiveMode.PeekLock,
                    retryPolicy: DefaultRetryStrategy,
                    prefetchCount: _prefetchCount
                );

                _disposables.Push(_messageReceiver.AsDisposable(m => AsyncHelpers.RunSync(async () => await m.CloseAsync().ConfigureAwait(false))));

                return;
            }

            _log.Info("Initializing one-way Azure Service Bus transport");
        }

        /// <summary>
        /// Always returns true because Azure Service Bus topics and subscriptions are global
        /// </summary>
        public bool IsCentralized => true;

        /// <summary>
        /// Enables automatic peek lock renewal - only recommended if you truly need to handle messages for a very long time
        /// </summary>
        public bool AutomaticallyRenewPeekLock { get; set; }

        /// <summary>
        /// Gets/sets whether partitioning should be enabled on new queues. Only takes effect for queues created
        /// after the property has been enabled
        /// </summary>
        public bool PartitioningEnabled { get; set; }

        /// <summary>
        /// Gets/sets whether to skip creating queues
        /// </summary>
        public bool DoNotCreateQueuesEnabled { get; set; }

        /// <summary>
        /// Gets/sets the default message TTL. Must be set before calling <see cref="Initialize"/>, because that is the time when the queue is (re)configured
        /// </summary>
        public TimeSpan? MessageTimeToLive { get; set; }

        /// <summary>
        /// Gets/sets message peek lock duration
        /// </summary>
        public TimeSpan? MessagePeekLockDuration { get; set; }

        /// <summary>
        /// Purges the input queue by receiving all messages as quickly as possible
        /// </summary>
        public void PurgeInputQueue()
        {
            var queueName = Address;

            if (string.IsNullOrWhiteSpace(queueName))
            {
                throw new InvalidOperationException("Cannot 'purge input queue' because there's no input queue name – it's most likely because this is a one-way client, and hence there is no input queue");
            }

            PurgeQueue(queueName);
        }

        /// <summary>
        /// Configures the transport to prefetch the specified number of messages into an in-mem queue for processing, disabling automatic peek lock renewal
        /// </summary>
        public void PrefetchMessages(int prefetchCount)
        {
            if (prefetchCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(prefetchCount), prefetchCount, "Must prefetch zero or more messages");
            }

            _prefetchingEnabled = prefetchCount > 0;
            _prefetchCount = prefetchCount;
        }

        /// <summary>
        /// Disposes all resources associated with this particular transport instance
        /// </summary>
        public void Dispose()
        {
            while (_disposables.TryPop(out var disposable))
            {
                disposable.Dispose();
            }
        }

        void PurgeQueue(string queueName)
        {
            try
            {
                AsyncHelpers.RunSync(async () =>
                    await ManagementExtensions.PurgeQueue(_connectionString, queueName).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                throw new ArgumentException($"Could not purge queue '{queueName}'", exception);
            }
        }

        IMessageSender GetMessageSender(string queue)
        {
            return _messageSenders.GetOrAdd(queue, _ =>
            {
                var messageSender = new MessageSender(
                    _connectionString,
                    queue,
                    retryPolicy: DefaultRetryStrategy
                );
                _disposables.Push(messageSender.AsDisposable(t => AsyncHelpers.RunSync(async () => await t.CloseAsync().ConfigureAwait(false))));
                return messageSender;
            });
        }

        ITopicClient GetTopicClient(string topic)
        {
            return _topicClients.GetOrAdd(topic, _ =>
            {
                var topicClient = new TopicClient(
                    _connectionString,
                    topic,
                    retryPolicy: DefaultRetryStrategy
                );
                _disposables.Push(topicClient.AsDisposable(t => AsyncHelpers.RunSync(async () => await t.CloseAsync().ConfigureAwait(false))));
                return topicClient;
            });
        }
    }
}
