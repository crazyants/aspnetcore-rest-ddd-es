﻿using Domain.Exceptions;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Exceptions;
using Infrastructure.Domain;
using Infrastructure.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EventStoreFacade
{
    public class EventStore : IEventStore
    {
        const int MaxPageSize = 4096; // EventStore.ClientAPI.Consts.MaxReadSize (internal)
        readonly ConnectionSettings Settings = ConnectionSettings.Create()           
            .KeepReconnecting().LimitAttemptsForOperationTo(60 * 60 / 3)
            .SetOperationTimeoutTo(TimeSpan.FromSeconds(3));
        readonly IEventStoreConnection Connection = null;        

        readonly IEventPublisher _publisher;

        public EventStore(ILogger<EventStore> logger, IEventPublisher publisher, 
            string serverUri = "tcp://localhost:1113")
        {
            _publisher = publisher;

            try
            {
                Connection = EventStoreConnection.Create(Settings, new Uri(serverUri));

                Connection.Disconnected += (s, e) =>
                    logger.LogError("EventStore Disconnected");

                Connection.Reconnecting += (s, e) =>
                    logger.LogError("EventStore Reconnecting");

                Connection.ErrorOccurred += (s, e) =>
                    logger.LogError("EventStore ErrorOccurred");

                Connection.ConnectAsync().Wait();
            }
            catch (Exception ex)
            {
                logger.LogError(0, ex, "EventStore Exception");
            }
        }

        public void SaveEvents<T>(Guid aggregateId, IEnumerable<Event> events, int expectedVersion)
            where T : AggregateRoot
        {
            try
            {
                var _ = Connection.AppendToStreamAsync(Utils.GetAggregateStreamName(typeof(T).FullName, aggregateId), expectedVersion,
                    events.Select(x => Utils.CreateEvent(x.GetType().FullName, x))).Result;
            }
            catch (AggregateException ae) when (ae.InnerException is WrongExpectedVersionException)
            {
                throw new ConcurrencyException();
            }

            foreach (var @event in events)
                // publish current event to the bus for further processing by subscribers
                _publisher.Publish(@event);
        }


        public List<Event> GetEventsForAggregate<T>(Guid aggregateId) where T : AggregateRoot
        {
            var events = ReadStream(Utils.GetAggregateStreamName(typeof(T).FullName, aggregateId))
                .Select(ev => Utils.FromJson(Utils.Encoding.GetString(ev.Event.Data), ev.Event.EventType))
                .Cast<Event>().ToList();

            if (events.Count == 0)
                throw new AggregateNotFoundException();

            return events;
        }

        public List<T> GetEventsForType<T>(int startIndex, int maxCount) 
            where T : Event =>

            ReadStream(Utils.GetEventTypeStreamName(typeof(T).FullName))
                .Select(ev => (T)Utils.FromJson(Utils.Encoding.GetString(ev.Event.Data), typeof(T)))
                .Skip(startIndex).Take(maxCount)
                .ToList();

        // ToDo: support paging
        List<ResolvedEvent> ReadStream(string streamName) 
        {
            var streamEvents = new List<ResolvedEvent>();

            StreamEventsSlice currentSlice;
            var nextSliceStart = StreamPosition.Start;
            do
            {
                currentSlice = Connection.ReadStreamEventsForwardAsync(streamName, nextSliceStart, MaxPageSize, true).Result;
                nextSliceStart = currentSlice.NextEventNumber;

                streamEvents.AddRange(currentSlice.Events);
            } while (!currentSlice.IsEndOfStream);

            return streamEvents;
        }
    }
}
