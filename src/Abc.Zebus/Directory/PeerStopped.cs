﻿using System;
using Abc.Zebus.Util;
using ProtoBuf;

namespace Abc.Zebus.Directory
{
    [ProtoContract, Transient]
    public class PeerStopped : IEvent
    {
        [ProtoMember(1, IsRequired = true)]
        public readonly PeerId PeerId;

        [ProtoMember(2, IsRequired = false)]
        public readonly string PeerEndPoint;

        [ProtoMember(3, IsRequired = false)]
        public readonly DateTime? TimestampUtc;

        public PeerStopped(Peer peer, DateTime? timestampUtc = null)
            : this(peer.Id, peer.EndPoint, timestampUtc)
        {
        }

        public PeerStopped(PeerId peerId, string peerEndPoint, DateTime? timestampUtc = null)
        {
            PeerId = peerId;
            PeerEndPoint = peerEndPoint;
            TimestampUtc = timestampUtc ?? SystemDateTime.UtcNow;
        }

        public override string ToString()
        {
            return PeerId.ToString();
        }
    }
}