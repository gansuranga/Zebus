﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Abc.Zebus.Util;
using Abc.Zebus.Util.Extensions;
using log4net;

namespace Abc.Zebus.Directory
{
    public partial class PeerDirectoryClient : IPeerDirectory,
                                               IMessageHandler<PeerStarted>,
                                               IMessageHandler<PeerStopped>,
                                               IMessageHandler<PeerDecommissioned>,
                                               IMessageHandler<PingPeerCommand>,
                                               IMessageHandler<PeerSubscriptionsUpdated>,
                                               IMessageHandler<PeerSubscriptionsForTypesUpdated>,
                                               IMessageHandler<PeerNotResponding>,
                                               IMessageHandler<PeerResponding>
    {
        private readonly ConcurrentDictionary<MessageTypeId, PeerSubscriptionTree> _globalSubscriptionsIndex = new ConcurrentDictionary<MessageTypeId, PeerSubscriptionTree>();
        private readonly ConcurrentDictionary<PeerId, PeerEntry> _peers = new ConcurrentDictionary<PeerId, PeerEntry>();
        private readonly ILog _logger = LogManager.GetLogger(typeof(PeerDirectoryClient));
        private readonly UniqueTimestampProvider _timestampProvider = new UniqueTimestampProvider(10);
        private readonly IBusConfiguration _configuration;
        private BlockingCollection<IEvent> _messagesReceivedDuringRegister;
        private IEnumerable<Peer> _directoryPeers;
        private Peer _self;

        public PeerDirectoryClient(IBusConfiguration configuration)
        {
            _configuration = configuration;
        }

        public event Action<PeerId, PeerUpdateAction> PeerUpdated = delegate { };

        public async Task RegisterAsync(IBus bus, Peer self, IEnumerable<Subscription> subscriptions)
        {
            _self = self;

            _globalSubscriptionsIndex.Clear();
            _peers.Clear();

            var selfDescriptor = CreateSelfDescriptor(subscriptions);
            AddOrUpdatePeerEntry(selfDescriptor);

            _messagesReceivedDuringRegister = new BlockingCollection<IEvent>();

            try
            {
                await TryRegisterOnDirectoryAsync(bus, selfDescriptor).ConfigureAwait(false);
            }
            finally
            {
                _messagesReceivedDuringRegister.CompleteAdding();
            }

            ProcessMessagesReceivedDuringRegister();
        }

        private void ProcessMessagesReceivedDuringRegister()
        {
            foreach (dynamic message in _messagesReceivedDuringRegister.GetConsumingEnumerable())
            {
                try
                {
                    Handle(message);
                }
                catch (Exception ex)
                {
                    _logger.WarnFormat("Unable to process message {0} {{{1}}}, Exception: {2}", message.GetType(), message.ToString(), ex);
                }
            }
        }

        private PeerDescriptor CreateSelfDescriptor(IEnumerable<Subscription> subscriptions)
        {
            return new PeerDescriptor(_self.Id, _self.EndPoint, _configuration.IsPersistent, true, true, _timestampProvider.NextUtcTimestamp(), subscriptions.ToArray())
            {
                HasDebuggerAttached = Debugger.IsAttached
            };
        }

        private async Task TryRegisterOnDirectoryAsync(IBus bus, PeerDescriptor selfDescriptor)
        {
            var directoryPeers = GetDirectoryPeers().ToList();

            foreach (var directoryPeer in directoryPeers)
            {
                try
                {
                    if (await TryRegisterOnDirectoryAsync(bus, selfDescriptor, directoryPeer).ConfigureAwait(false))
                        return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Error(ex);
                }
            }

            var directoryPeersText = string.Join(", ", directoryPeers.Select(peer => "{" + peer + "}"));
            var message = $"Unable to register peer on directory (tried: {directoryPeersText}) after {_configuration.RegistrationTimeout}";
            _logger.Error(message);
            throw new TimeoutException(message);
        }

        private async Task<bool> TryRegisterOnDirectoryAsync(IBus bus, PeerDescriptor self, Peer directoryPeer)
        {
            try
            {
                var registration = await bus.Send(new RegisterPeerCommand(self), directoryPeer).WithTimeoutAsync(_configuration.RegistrationTimeout).ConfigureAwait(false);

                var response = (RegisterPeerResponse)registration.Response;
                if (response?.PeerDescriptors == null)
                    return false;

                if (registration.ErrorCode == DirectoryErrorCodes.PeerAlreadyExists)
                {
                    _logger.InfoFormat("Register rejected for {0}, the peer already exists in the directory", new RegisterPeerCommand(self).Peer.PeerId);
                    return false;
                }

                response.PeerDescriptors?.ForEach(AddOrUpdatePeerEntry);

                return true;
            }
            catch (TimeoutException ex)
            {
                _logger.Error(ex);
                return false;
            }
        }

        public async Task UpdateSubscriptionsAsync(IBus bus, IEnumerable<SubscriptionsForType> subscriptionsForTypes)
        {
            var subscriptions = subscriptionsForTypes as SubscriptionsForType[] ?? subscriptionsForTypes.ToArray();
            if (subscriptions.Length == 0)
                return;

            var command = new UpdatePeerSubscriptionsForTypesCommand(_self.Id, _timestampProvider.NextUtcTimestamp(), subscriptions);

            foreach (var directoryPeer in GetDirectoryPeers())
            {
                try
                {
                    await bus.Send(command, directoryPeer).WithTimeoutAsync(_configuration.RegistrationTimeout).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Error(ex);
                }
            }

            throw new TimeoutException("Unable to update peer subscriptions on directory");
        }

        public async Task UnregisterAsync(IBus bus)
        {
            var command = new UnregisterPeerCommand(_self, _timestampProvider.NextUtcTimestamp());

            // Using a cache of the directory peers in case of the underlying configuration proxy values changed before stopping
            foreach (var directoryPeer in _directoryPeers)
            {
                try
                {
                    await bus.Send(command, directoryPeer).WithTimeoutAsync(_configuration.RegistrationTimeout).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException ex)
                {
                    _logger.Error(ex);
                }
            }

            throw new TimeoutException("Unable to unregister peer on directory");
        }

        public IList<Peer> GetPeersHandlingMessage(IMessage message)
            => GetPeersHandlingMessage(MessageBinding.FromMessage(message));

        public IList<Peer> GetPeersHandlingMessage(MessageBinding messageBinding)
        {
            var subscriptionList = _globalSubscriptionsIndex.GetValueOrDefault(messageBinding.MessageTypeId);
            if (subscriptionList == null)
                return Array.Empty<Peer>();

            return subscriptionList.GetPeers(messageBinding.RoutingKey);
        }

        public bool IsPersistent(PeerId peerId)
        {
            var entry = _peers.GetValueOrDefault(peerId);
            return entry != null && entry.IsPersistent;
        }

        public PeerDescriptor GetPeerDescriptor(PeerId peerId)
            => _peers.GetValueOrDefault(peerId)?.ToPeerDescriptor();

        public IEnumerable<PeerDescriptor> GetPeerDescriptors()
            => _peers.Values.Select(x => x.ToPeerDescriptor()).ToList();

        // Only internal for testing purposes
        internal IEnumerable<Peer> GetDirectoryPeers()
        {
            _directoryPeers = _configuration.DirectoryServiceEndPoints.Select(CreateDirectoryPeer);
            return _configuration.IsDirectoryPickedRandomly ? _directoryPeers.Shuffle() : _directoryPeers;
        }

        private static Peer CreateDirectoryPeer(string endPoint, int index)
        {
            var peerId = new PeerId("Abc.Zebus.DirectoryService." + index);
            return new Peer(peerId, endPoint);
        }

        private void AddOrUpdatePeerEntry(PeerDescriptor peerDescriptor)
        {
            var subscriptions = peerDescriptor.Subscriptions ?? Array.Empty<Subscription>();

            var peerEntry = _peers.AddOrUpdate(peerDescriptor.PeerId,
                                               key => new PeerEntry(peerDescriptor, _globalSubscriptionsIndex),
                                               (key, entry) =>
                                               {
                                                   entry.Peer.EndPoint = peerDescriptor.Peer.EndPoint;
                                                   entry.Peer.IsUp = peerDescriptor.Peer.IsUp;
                                                   entry.Peer.IsResponding = peerDescriptor.Peer.IsResponding;
                                                   entry.IsPersistent = peerDescriptor.IsPersistent;
                                                   entry.TimestampUtc = peerDescriptor.TimestampUtc ?? DateTime.UtcNow;
                                                   entry.HasDebuggerAttached = peerDescriptor.HasDebuggerAttached;

                                                   return entry;
                                               });

            peerEntry.SetSubscriptions(subscriptions, peerDescriptor.TimestampUtc);
        }

        public void Handle(PeerStarted message)
        {
            if (EnqueueIfRegistering(message))
                return;

            AddOrUpdatePeerEntry(message.PeerDescriptor);
            PeerUpdated(message.PeerDescriptor.Peer.Id, PeerUpdateAction.Started);
        }

        private bool EnqueueIfRegistering(IEvent message)
        {
            if (_messagesReceivedDuringRegister == null)
                return false;

            if (_messagesReceivedDuringRegister.IsAddingCompleted)
                return false;

            try
            {
                _messagesReceivedDuringRegister.Add(message);
                return true;
            }
            catch (InvalidOperationException)
            {
                // if adding is complete; should only happen in a race
                return false;
            }
        }

        public void Handle(PingPeerCommand message)
        {
        }

        public void Handle(PeerStopped message)
        {
            if (EnqueueIfRegistering(message))
                return;

            var peer = GetPeerCheckTimestamp(message.PeerId, message.TimestampUtc);
            if (peer.Value == null)
                return;

            peer.Value.Peer.IsUp = false;
            peer.Value.Peer.IsResponding = false;
            peer.Value.TimestampUtc = message.TimestampUtc ?? DateTime.UtcNow;

            PeerUpdated(message.PeerId, PeerUpdateAction.Stopped);
        }

        public void Handle(PeerDecommissioned message)
        {
            if (EnqueueIfRegistering(message))
                return;

            if (!_peers.TryRemove(message.PeerId, out var removedPeer))
                return;

            removedPeer.RemoveSubscriptions();

            PeerUpdated(message.PeerId, PeerUpdateAction.Decommissioned);
        }

        public void Handle(PeerSubscriptionsUpdated message)
        {
            if (EnqueueIfRegistering(message))
                return;

            var peer = GetPeerCheckTimestamp(message.PeerDescriptor.Peer.Id, message.PeerDescriptor.TimestampUtc);
            if (peer.Value == null)
            {
                WarnWhenPeerDoesNotExist(peer, message.PeerDescriptor.PeerId);
                return;
            }

            peer.Value.SetSubscriptions(message.PeerDescriptor.Subscriptions ?? Enumerable.Empty<Subscription>(), message.PeerDescriptor.TimestampUtc);
            peer.Value.TimestampUtc = message.PeerDescriptor.TimestampUtc ?? DateTime.UtcNow;

            PeerUpdated(message.PeerDescriptor.PeerId, PeerUpdateAction.Updated);
        }

        public void Handle(PeerSubscriptionsForTypesUpdated message)
        {
            if (EnqueueIfRegistering(message))
                return;

            var peer = GetPeerCheckTimestamp(message.PeerId, message.TimestampUtc);
            if (peer.Value == null)
            {
                WarnWhenPeerDoesNotExist(peer, message.PeerId);
                return;
            }

            peer.Value.SetSubscriptionsForType(message.SubscriptionsForType ?? Enumerable.Empty<SubscriptionsForType>(), message.TimestampUtc);

            PeerUpdated(message.PeerId, PeerUpdateAction.Updated);
        }

        private void WarnWhenPeerDoesNotExist(PeerEntryResult peer, PeerId peerId)
        {
            if (peer.FailureReason == PeerEntryResult.FailureReasonType.PeerNotPresent)
                _logger.WarnFormat("Received message but no peer existed: {0}", peerId);
        }

        public void Handle(PeerNotResponding message)
        {
            HandlePeerRespondingChange(message.PeerId, false);
        }

        public void Handle(PeerResponding message)
        {
            HandlePeerRespondingChange(message.PeerId, true);
        }

        private void HandlePeerRespondingChange(PeerId peerId, bool isResponding)
        {
            var peer = _peers.GetValueOrDefault(peerId);
            if (peer == null)
                return;

            peer.Peer.IsResponding = isResponding;

            PeerUpdated(peerId, PeerUpdateAction.Updated);
        }

        private PeerEntryResult GetPeerCheckTimestamp(PeerId peerId, DateTime? timestampUtc)
        {
            var peer = _peers.GetValueOrDefault(peerId);
            if (peer == null)
                return new PeerEntryResult(PeerEntryResult.FailureReasonType.PeerNotPresent);

            if (peer.TimestampUtc > timestampUtc)
            {
                _logger.InfoFormat("Outdated message ignored");
                return new PeerEntryResult(PeerEntryResult.FailureReasonType.OutdatedMessage);
            }

            return new PeerEntryResult(peer);
        }

        private readonly struct PeerEntryResult
        {
            internal enum FailureReasonType
            {
                PeerNotPresent,
                OutdatedMessage,
            }

            public PeerEntryResult(PeerEntry value)
                : this()
            {
                Value = value;
            }

            public PeerEntryResult(FailureReasonType failureReason)
                : this()
            {
                FailureReason = failureReason;
            }

            public PeerEntry Value { get; }
            public FailureReasonType? FailureReason { get; }
        }
    }
}
