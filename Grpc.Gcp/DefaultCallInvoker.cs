﻿using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using System.IO;
using System.Text;

namespace Grpc.Gcp
{
    class ChannelRef
    {
        private int affinityRef;
        private int activeStreamRef;
        private Channel channel;
        private int id;

        public ChannelRef(Channel channel, int id, int affinityRef = 0, int activeStreamRef = 0)
        {
            this.channel = channel;
            this.id = id;
            this.affinityRef = affinityRef;
            this.activeStreamRef = activeStreamRef;
        }

        public void AffinityRefIncr()
        {
            affinityRef++;
        }

        public void AffinityRefDecr()
        {
            affinityRef--;
        }

        public int AffinityRef
        {
            get
            {
                return affinityRef;
            }
        }

        public void ActiveStreamRefIncr()
        {
            activeStreamRef++;
        }

        public void ActiveStreamRefDecr()
        {
            activeStreamRef--;
        }

        public int ActiveStreamRef
        {
            get
            {
                return activeStreamRef;
            }
        }

        public Channel Channel
        {
            get { return channel; }
        }

    }

    public class DefaultCallInvoker : CallInvoker
    {
        public static readonly string GRPC_GCP_CHANNEL_ARG_API_CONFIG = "grpc_gcp.api_config";
        private static readonly string CLIENT_CHANNEL_ID = "grpc_gcp.client_channel.id";
        private readonly GrpcGcp.ApiConfig config;
        private readonly IDictionary<string, GrpcGcp.AffinityConfig> affinityByMethod;
        private Object thisLock = new Object();
        private IDictionary<string, ChannelRef> channelRefByAffinityKey = new Dictionary<string, ChannelRef>();
        private IList<ChannelRef> channelRefs = new List<ChannelRef>();
        private readonly string target;
        private readonly ChannelCredentials credentials;
        private readonly IEnumerable<ChannelOption> options;

        public DefaultCallInvoker(string target, ChannelCredentials credentials) : this(target, credentials, null) { }

        public DefaultCallInvoker(string target, ChannelCredentials credentials, IEnumerable<ChannelOption> options)
        {
            this.target = target;
            this.credentials = credentials;

            if (options != null)
            {
                ChannelOption option = options.FirstOrDefault(opt => opt.Name == GRPC_GCP_CHANNEL_ARG_API_CONFIG);
                if (option != null)
                {
                    config = GrpcGcp.ApiConfig.Parser.ParseFrom(new MemoryStream(Encoding.Default.GetBytes(option.StringValue)));
                    affinityByMethod = InitAffinityByMethodIndex(config);
                }
                this.options = options.Where(o => o.Name != GRPC_GCP_CHANNEL_ARG_API_CONFIG).AsEnumerable<ChannelOption>();
            }
        }

        public DefaultCallInvoker(string host, int port, ChannelCredentials credentials) : this(host, port, credentials, null) { }

        public DefaultCallInvoker(string host, int port, ChannelCredentials credentials, IEnumerable<ChannelOption> options) :
            this(string.Format("{0}:{1}", host, port), credentials, options)
        { }

        private IDictionary<string, GrpcGcp.AffinityConfig> InitAffinityByMethodIndex(GrpcGcp.ApiConfig config)
        {
            IDictionary<string, GrpcGcp.AffinityConfig> index = new Dictionary<string, GrpcGcp.AffinityConfig>();
            if (config != null)
            {
                foreach (GrpcGcp.MethodConfig method in config.Method)
                {
                    // TODO(fengli): supports wildcard in method selector.
                    foreach (string name in method.Name)
                    {
                        index.Add(name, method.Affinity);
                    }
                }
            }
            return index;
        }

        private ChannelRef GetChannelRef(string affinityKey = null)
        {
            // TODO(fengli): Supports load reporting.
            lock (thisLock)
            {
                if (!string.IsNullOrEmpty(affinityKey))
                {
                    // Finds the gRPC channel according to the affinity key.
                    ChannelRef channelRef;
                    if (channelRefByAffinityKey.TryGetValue(affinityKey, out channelRef))
                    {
                        return channelRef;
                    }
                    // TODO(fengli): Affinity key not found, log an error.
                    return GetChannelRef();
                }

                // TODO(fengli): Creates new gRPC channels on demand, depends on the load reporting.
                IOrderedEnumerable<ChannelRef> orderedChannelRefs =
                    channelRefs.OrderBy(channelRef => channelRef.ActiveStreamRef);
                foreach (ChannelRef channelRef in orderedChannelRefs)
                {
                    if (channelRef.ActiveStreamRef <= config.ChannelPool.MaxConcurrentStreamsLowWatermark)
                    {
                        // If there's a free channel, use it.
                        return channelRef;
                    }
                    else
                    {
                        //  If all channels are busy, break.
                        break;
                    }
                }
                int count = channelRefs.Count;
                if (count < config.ChannelPool.MaxSize)
                {
                    // Creates a new gRPC channel.
                    Channel channel = new Channel(target, credentials,
                        options.Concat(new[] { new ChannelOption(CLIENT_CHANNEL_ID, count) }));
                    ChannelRef channelRef = new ChannelRef(channel, count);
                    channelRefs.Add(channelRef);
                    return channelRef;
                }
                // If all channels are overloaded and the channel pool is full already,
                // return the channel with least active streams.
                return orderedChannelRefs.First();
            }
        }

        private string GetAffinityKeyFromProto(string affinityKey, IMessage message)
        {
            if (string.IsNullOrEmpty(affinityKey))
            {
                return null;
            }
            string[] names = affinityKey.Split('.');
            if (names != null)
            {
                foreach (string name in names)
                {
                    object value = message.Descriptor.FindFieldByName(name).Accessor.GetValue(message);
                    if (value is string)
                    {
                        return (string)value;
                    }
                    else if (value is IMessage)
                    {
                        message = (IMessage)value;
                    }
                }
            }
            throw new Exception(String.Format("Cannot find the field in the proto, path: {0}", affinityKey));
        }

        private void Bind(ChannelRef channelRef, string affinityKey)
        {
            lock (thisLock)
            {
                if (!string.IsNullOrEmpty(affinityKey) && !channelRefByAffinityKey.Keys.Contains(affinityKey))
                {
                    channelRefByAffinityKey.Add(affinityKey, channelRef);
                }
                channelRefByAffinityKey[affinityKey].AffinityRefIncr();
            }
        }

        private ChannelRef Unbind(string affinityKey)
        {
            lock (thisLock)
            {
                ChannelRef channelRef = null;
                if (channelRefByAffinityKey.TryGetValue(affinityKey, out channelRef))
                {
                    channelRef.AffinityRefDecr();
                }
                return channelRef;
            }
        }

        public override AsyncClientStreamingCall<TRequest, TResponse>
            AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string host, CallOptions options)
        {
            throw new System.NotImplementedException();
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse>
            AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string host, CallOptions options)
        {
            throw new System.NotImplementedException();
        }

        public override AsyncServerStreamingCall<TResponse>
            AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string host, CallOptions options, TRequest request)
        {
            throw new System.NotImplementedException();
        }

        public override AsyncUnaryCall<TResponse>
            AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string host, CallOptions options, TRequest request)
        {
            throw new System.NotImplementedException();
        }

        public override TResponse
            BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string host, CallOptions options, TRequest request)
        {
            string affinityKey = null;
            GrpcGcp.AffinityConfig affinity;
            if (affinityByMethod.TryGetValue(method.FullName, out affinity))
            {
                if (affinity.Command == GrpcGcp.AffinityConfig.Types.Command.Bound
                    || affinity.Command == GrpcGcp.AffinityConfig.Types.Command.Unbind)
                {
                    affinityKey = GetAffinityKeyFromProto(affinity.AffinityKey, (IMessage)request);
                }
            }
            ChannelRef channelRef = GetChannelRef(affinityKey);
            channelRef.ActiveStreamRefIncr();
            CallInvocationDetails<TRequest, TResponse> call = new CallInvocationDetails<TRequest, TResponse>(channelRef.Channel,
                method, host, options);
            TResponse response = Calls.BlockingUnaryCall<TRequest, TResponse>(call, request);
            channelRef.ActiveStreamRefDecr();
            if (affinity != null)
            {
                if (affinity.Command == GrpcGcp.AffinityConfig.Types.Command.Bind)
                {
                    Bind(channelRef, GetAffinityKeyFromProto(affinity.AffinityKey, (IMessage)response));
                }
                else if (affinity.Command == GrpcGcp.AffinityConfig.Types.Command.Unbind)
                {
                    Unbind(affinityKey);
                }
            }
            return response;
        }
    }
}
