﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using NServiceBus.Extensibility;
using NServiceBus.Persistence.MongoDB.Database;
using NServiceBus.Routing;
using NServiceBus.Unicast.Subscriptions;
using NServiceBus.Unicast.Subscriptions.MessageDrivenSubscriptions;

namespace NServiceBus.Persistence.MongoDB.Subscriptions
{
    public class SubscriptionPersister : ISubscriptionStorage
    {
        private readonly IMongoCollection<Subscription> _subscriptions;

        private static bool _indexCreated = false;

        public SubscriptionPersister(IMongoDatabase database)
        {
            _subscriptions = database.GetCollection<Subscription>(MongoPersistenceConstants.SubscriptionCollectionName);

            Init();
        }
        
        public void Init()
        {
            if (!_indexCreated)
            {
                //no locking - if it runs more than once it's okay
                _indexCreated = true;
                _subscriptions.Indexes.CreateOne(
                    new IndexKeysDefinitionBuilder<Subscription>().Ascending(s => s.Id).Ascending(s => s.Subscribers));
            }
        }

        public async Task Subscribe(Subscriber subscriber, MessageType messageType, ContextBag context)
        {
            var key = GetMessageTypeKey(messageType);
            
            var update = new UpdateDefinitionBuilder<Subscription>().AddToSet(s => s.Subscribers, SubscriberToString(subscriber));

            await _subscriptions.UpdateOneAsync(s => s.Id == key, update, new UpdateOptions() {IsUpsert = true});
        }

        private IEnumerable<SubscriptionKey> GetMessageTypeKeys(IEnumerable<MessageType> messageTypes)
        {
            return messageTypes.Select(GetMessageTypeKey);
        }

        private static SubscriptionKey GetMessageTypeKey(MessageType messageType)
        {
            return new SubscriptionKey {TypeName = messageType.TypeName, Version = messageType.Version.Major.ToString()};
        }

        public async Task Unsubscribe(Subscriber subscriber, MessageType messageType, ContextBag context)
        {
            var key = GetMessageTypeKey(messageType);
            
            var subscriberName = SubscriberToString(subscriber);

            var update = new UpdateDefinitionBuilder<Subscription>().Pull(s => s.Subscribers, subscriberName);
                
            await _subscriptions.UpdateOneAsync(s => s.Id == key && s.Subscribers.Contains(subscriberName), update, new UpdateOptions() {IsUpsert = false});
        }

        public async Task<IEnumerable<Subscriber>> GetSubscriberAddressesForMessage(IEnumerable<MessageType> messageTypes, ContextBag context)
        {
            var keys = GetMessageTypeKeys(messageTypes);

            var subscriptions = await _subscriptions.Find(s => keys.Contains(s.Id)).ToListAsync();
            
            return subscriptions
                .SelectMany(s => s.Subscribers)
                .Distinct()
                .Select(ParseSubscriber);
        }

        private static string SubscriberToString(Subscriber subscriber)
        {
            return $"{subscriber.TransportAddress}@{subscriber.Endpoint}";
        }

        private static Subscriber ParseSubscriber(string address)
        {
            var split = address.Split('@');

            if (split.Length > 2)
            {
                var message = $"Address contains multiple @ characters. Address supplied: '{address}'";
                throw new ArgumentException(message, nameof(address));
            }

            var transportAddress = split[0];
            if (string.IsNullOrWhiteSpace(transportAddress))
            {
                throw new ArgumentException($"Empty transportAddress part of address. Address supplied: '{address}'", nameof(address));
            }

            string endpointName = null;
            if (split.Length == 2)
            {
                endpointName = split[1];
            }

            if (string.IsNullOrWhiteSpace(endpointName))
            {
                throw new ArgumentException($"Empty endpointName part of address. Address supplied: '{address}'", nameof(address));
            }

            return new Subscriber(transportAddress, new EndpointName(endpointName));
        }
    }
}
