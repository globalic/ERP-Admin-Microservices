﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ERPAdmin.Services.Common.Infrastructure
{
    public class EventBusRabbitMQ : IEventBus
    {
        private readonly string _brokerName = "erpadmin_event_bus";
        private readonly string _connectionString;
        private readonly Dictionary<string, List<IIntegrationEventHandler>>  _handlers;
        private readonly List<Type> _eventTypes;

        private Tuple<IModel, IConnection> _connection;
        private string _queueName;
        

        public EventBusRabbitMQ(string connectionString)
        {
            _connectionString = connectionString;
            _handlers = new Dictionary<string, List<IIntegrationEventHandler>>();
            _eventTypes = new List<Type>();
        }
        public void Publish(IntegrationEventBase @event)
        {
            var eventName = @event.GetType().Name;
            var factory = new ConnectionFactory() { HostName = _connectionString };
            using (var connection = factory.CreateConnection())
            using (var channel = connection.CreateModel())
            {
                channel.ExchangeDeclare(exchange: _brokerName,
                                    type: "direct");

                string message = JsonConvert.SerializeObject(@event);                                
                var body = Encoding.UTF8.GetBytes(message);

                channel.BasicPublish(exchange: _brokerName,
                                     routingKey: eventName,
                                     basicProperties: null,
                                     body: body);                
            }
            
        }

        public void Subscribe<T>(IIntegrationEventHandler<T> handler) where T : IntegrationEventBase
        {
            var eventName = typeof(T).Name;
            if (_handlers.ContainsKey(eventName))   
            {
                _handlers[eventName].Add(handler);
            }
            else
            {
                var channel = GetChannel();
                channel.QueueBind(queue: _queueName,
                                  exchange: _brokerName,
                                  routingKey: eventName);
                                               
                _handlers.Add(eventName, new List<IIntegrationEventHandler>());
                _handlers[eventName].Add(handler);
                _eventTypes.Add(typeof(T));
            }
            
        }

        public void Unsubscribe<T>(IIntegrationEventHandler<T> handler) where T : IntegrationEventBase
        {
            var eventName = typeof(T).Name;
            if (_handlers.ContainsKey(eventName) && _handlers[eventName].Contains(handler))
            {
                _handlers[eventName].Remove(handler);

                if (_handlers[eventName].Count == 0)
                {
                    _handlers.Remove(eventName);
                    var eventType = _eventTypes.Single(e => e.Name == eventName);
                    _eventTypes.Remove(eventType);
                    _connection.Item1.QueueUnbind(queue: _queueName, 
                        exchange: _brokerName, 
                        routingKey: eventName);

                    if (_handlers.Keys.Count == 0)
                    {
                        _queueName = string.Empty;
                        _connection.Item1.Close();
                        _connection.Item2.Close();
                    }
                    
                }
            }
        }

        private IModel GetChannel()
        {
            if (_connection != null)
            {
                return _connection.Item1;
            }
            else
            {
                var factory = new ConnectionFactory() { HostName = _connectionString };
                var connection = factory.CreateConnection();
                var channel = connection.CreateModel();

                channel.ExchangeDeclare(exchange: _brokerName,
                                    type: "direct");
                if (string.IsNullOrEmpty(_queueName))
                {
                    _queueName = channel.QueueDeclare().QueueName;
                }

                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += async (model, ea) =>
                {
                    var eventName = ea.RoutingKey;
                    var message = Encoding.UTF8.GetString(ea.Body);

                    await ProcessEvent(eventName, message);
                };
                channel.BasicConsume(queue: _queueName,
                                     noAck: true,
                                     consumer: consumer);
                _connection = new Tuple<IModel, IConnection>(channel, connection);

                return _connection.Item1;
            }
        }

        private async Task ProcessEvent(string eventName, string message)
        {
            if (_handlers.ContainsKey(eventName))
            {
                Type eventType = _eventTypes.Single(t => t.Name == eventName);
                var integrationEvent = JsonConvert.DeserializeObject(message, eventType);                
                var concreteType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
                var handlers = _handlers[eventName];

                foreach (var handler in handlers)
                {
                    await (Task)concreteType.GetMethod("Handle").Invoke(handler, new object[] { integrationEvent });
                }
            }
        }
    }
}
