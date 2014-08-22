﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Xlent.Match.ClientUtilities.Logging;

namespace Xlent.Match.ClientUtilities.ServiceBus
{
    public class Queue : BaseClass, IQueueSender, IQueueReceiver, IQueueAdministrator
    {
        private readonly ReceiveMode _receiveMode;

        public Queue(string connectionStringName, string name, ReceiveMode receiveMode = ReceiveMode.PeekLock)
            : base(connectionStringName)
        {
            _receiveMode = receiveMode;
            Name = name;
            // Create a new Queue with custom settings
            SafeCreateQueue(name);
            Client = QueueClient.CreateFromConnectionString(ConnectionString, name, receiveMode);
        }

        private QueueClient Client { get; set; }

        public void Delete()
        {
            RetryPolicy.ExecuteAction(() => NamespaceManager.DeleteQueue(Client.Path));
        }

        public async Task DeleteAsync()
        {
            await RetryPolicy.ExecuteAsync(() => NamespaceManager.DeleteQueueAsync(Client.Path));
        }

        public BrokeredMessage NonBlockingReceive()
        {
            return RetryPolicy.ExecuteAction(() => Client.Receive(TimeSpan.FromSeconds(1)));
        }

        public BrokeredMessage BlockingReceive()
        {
            while (true)
            {
                BrokeredMessage message = null;
                RetryPolicy.ExecuteAction(() => message = Client.Receive(TimeSpan.FromMinutes(60)));
                if (message != null) return message;
            }
        }

        public void SetLockDuration(TimeSpan durationTimeSpan)
        {
            var queueDescription = GetQueueDescription();
            queueDescription.LockDuration = durationTimeSpan;
            SetQueueDescription(queueDescription);
        }

        public void OnMessage(Action<BrokeredMessage> action, OnMessageOptions onMessageOptions)
        {
            Client.OnMessage(action, onMessageOptions);
        }

        public void OnMessageAsync(Func<BrokeredMessage, Task> asyncAction, OnMessageOptions onMessageOptions)
        {
            Client.OnMessageAsync(asyncAction, onMessageOptions);
        }

        public void Activate()
        {
            var queueDescription = GetQueueDescription();
            queueDescription.Status = EntityStatus.Active;
            SetQueueDescription(queueDescription);
        }

        public void Disable()
        {
            var queueDescription = GetQueueDescription();
            queueDescription.Status = EntityStatus.ReceiveDisabled;
            SetQueueDescription(queueDescription);
        }

        public async Task SafeAbandonAsync(BrokeredMessage message)
        {
            try
            {
                if (_receiveMode == ReceiveMode.ReceiveAndDelete)
                {
                    Log.Warning(
                        "Not expected to abandon messages on the subscription \"{0}\" that has ReceiveMode={1}.",
                        this, _receiveMode);
                    return;
                }
                await RetryPolicy.ExecuteAction(() => message.AbandonAsync());
            }
                // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
                // It does not matter if we fail to abandon.
                // Most probably, that only means that it already has been abandoned.
            }
        }

        public async Task SafeCompleteAsync<T>(BrokeredMessage message, T interpretedMessage)
        {
            try
            {
                if (_receiveMode == ReceiveMode.ReceiveAndDelete)
                {
                    return;
                }
                await RetryPolicy.ExecuteAction(() => message.CompleteAsync());
            }
            catch (MessageLockLostException ex)
            {
                Log.Error(ex, "Queue {1}: {0} could not be completed (due to lock lost)", interpretedMessage, Name);
            }
            catch (Exception ex)
            {
                Log.Critical(ex, "Queue {1}: {0} could not be completed.", interpretedMessage, Name);
            }
        }

        public async Task SafeDeadLetterAsync(BrokeredMessage message)
        {
            try
            {
                await RetryPolicy.ExecuteAction(() => message.DeadLetterAsync());
            }
            catch (MessageLockLostException ex)
            {
                Log.Error(ex, "Queue {0}: Message could not be put on the dead letter queue (due to lock lost)", Name);
            }
            catch (Exception ex)
            {
                Log.Critical(ex, "Queue {0}: Message could not be put on the dead letter queue", Name);
            }
        }

        public string Name { get; private set; }

        public void Send<T>(T message, IDictionary<string, object> properties = null)
        {
            var m = new BrokeredMessage(message, new DataContractSerializer(typeof (T)));
            if (properties != null)
            {
                foreach (var property in properties)
                {
                    m.Properties.Add(property);
                }
            }
            Send(m);
        }

        public void Send(BrokeredMessage message)
        {
            RetryPolicy.ExecuteAction(() => Client.Send(message));
        }

        public async Task ResendAndCompleteAsync(BrokeredMessage message)
        {
            var newMessage = message.Clone();
            await SendAsync(newMessage);
            try
            {
                await RetryPolicy.ExecuteAsync(message.CompleteAsync);
            }
                // ReSharper disable once EmptyGeneralCatchClause
            catch
            {
            }
        }

        public long GetLength()
        {
            var queueDescription = GetQueueDescription();
            return queueDescription.MessageCountDetails.ActiveMessageCount;
        }

        public async Task FlushAsync()
        {
            await ForEachMessageAsync(async message => await Task.Run(() => { }));
            var length = GetLength();
            //Debug.Assert(length == 0, "Expected queue to be empty after flush", "Queue \"{0}\" = {1}", Name, length);

            do
            {
                var deadLetterPath = QueueClient.FormatDeadLetterPath(Name);
                var deadLetterClient = QueueClient.CreateFromConnectionString(ConnectionString, deadLetterPath,
                    ReceiveMode.ReceiveAndDelete);

                var messages = await deadLetterClient.ReceiveBatchAsync(100, TimeSpan.FromMilliseconds(1000));
                var brokeredMessages = messages as BrokeredMessage[] ?? messages.ToArray();
                if (!brokeredMessages.Any()) break;
            } while (true);
        }

        public async Task ForEachMessageAsync(Func<BrokeredMessage, Task> actionAsync)
        {
            do
            {
                var flushClient = QueueClient.CreateFromConnectionString(ConnectionString, Name,
                    ReceiveMode.ReceiveAndDelete);
                var messages = await flushClient.ReceiveBatchAsync(100, TimeSpan.FromMilliseconds(1000));
                var brokeredMessages = messages as BrokeredMessage[] ?? messages.ToArray();
                if (!brokeredMessages.Any()) break;

                Parallel.ForEach(brokeredMessages, async brokeredMessage => { await actionAsync(brokeredMessage); });
            } while (true);
        }

        private void SafeCreateQueue(string name)
        {
            if (NamespaceManager.QueueExists(name)) return;

            try
            {
                // Configure Queue Settings
                var qd = new QueueDescription(name);
                RetryPolicy.ExecuteAction(() => NamespaceManager.CreateQueue(qd));
            }
            catch (Exception)
            {
                if (NamespaceManager.QueueExists(name)) return;
                throw;
            }
        }

        public T GetFromQueue<T>(out BrokeredMessage message) where T : class
        {
            message = BlockingReceive();

            return message.GetBody<T>(new DataContractSerializer(typeof (T)));
        }

        public async Task SendAsync(BrokeredMessage message)
        {
            await RetryPolicy.ExecuteAsync(() => Client.SendAsync(message));
        }

        public async Task<BrokeredMessage> NonBlockingReceiveAsync()
        {
            return await RetryPolicy.ExecuteAction(() => Client.ReceiveAsync(TimeSpan.FromSeconds(1)));
        }

        private Task Callback(BrokeredMessage brokeredMessage)
        {
            throw new NotImplementedException();
        }

        private QueueDescription GetQueueDescription()
        {
            return RetryPolicy.ExecuteAction(() => NamespaceManager.GetQueue(Client.Path));
        }

        private void SetQueueDescription(QueueDescription queueDescription)
        {
            RetryPolicy.ExecuteAction(() => NamespaceManager.UpdateQueue(queueDescription));
        }
    }
}