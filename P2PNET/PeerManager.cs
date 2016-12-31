﻿using Sockets.Plugin;
using System;
using System.Collections.Generic;
using P2PNET.EventArgs;
using System.Threading.Tasks;
using System.Threading;

namespace P2PNET
{
    public class PeerManager
    {
        public event EventHandler<PeerChangeEventArgs> PeerChange;
        public event EventHandler<MsgReceivedEventArgs> msgReceived;

        private Listener listener;
        private BaseStation baseStation;
        private HeartBeat heartBeat;


        public List<Peer> KnownPeers
        {
            get
            {
                return this.baseStation.KnownPeers;
            }
        }

        private string ipAddress;
        public string IpAddress
        {
            get
            {
                return ipAddress;
            }
        }
        public int PortNum { get; }

        //constructor
        public PeerManager(int mPortNum = 8080)
        {
            this.PortNum = mPortNum;
            this.listener = new Listener(this.PortNum);
            this.baseStation = new BaseStation(this.PortNum);

            this.baseStation.PeerChange += BaseStation_PeerChange;
            this.baseStation.MsgReceived += IncomingMsg;

            //baseStation looks up incoming messages to see if there is a new peer talk to us
            this.listener.IncomingMsg += baseStation.IncomingMsgAsync;
            this.listener.PeerConnectTCPRequest += baseStation.NewTCPConnection;
        }

        
        //This function will enable a heartbeat.
        //refreshRateMilliSec is the period before heartbeats
        //heartbeats helps other peers find out which other peers
        //are avaliable on the same local network (same subnet)
        public void EnableAutomaticConnect(int refreshRateMilliSec)
        {
            heartBeat = new HeartBeat(refreshRateMilliSec, baseStation);
        }

        public async Task StartAsync()
        {
            this.ipAddress = await this.GetLocalIPAddress();
            baseStation.LocalIpAddress = this.ipAddress;
            heartBeat?.StartBroadcasting();
            await listener.StartAsync();
        }

        public async Task<bool> SendMsgAsyncTCP(string ipAddress, byte[] msg)
        {
            return await baseStation.SendTCPMsgAsync(ipAddress, msg);
        }

        public async Task SendMsgAsyncUDP(string ipAddress, byte[] msg)
        {
            await baseStation.SendUDPMsgAsync(ipAddress, msg);
        }

        public async Task SendBroadcastAsyncUDP(byte[] msg)
        {
            await baseStation.SendUDPBroadcastAsync(msg);
        }

        //This is here for existing Peer to Peer systems that use asynchronous Connections.
        //This method is not needed otherwise because this class automatically keeps track
        //of peer connections
        public async Task DirrectConnectAsyncTCP(string ipAddress)
        {
            await baseStation.DirectConnectTCPAsync(ipAddress);
        }

        private void IncomingMsg(object sender, MsgReceivedEventArgs e)
        {
            //send message out
            msgReceived?.Invoke(this, e);
        }

        private void BaseStation_PeerChange(object sender, PeerChangeEventArgs e)
        {
            PeerChange?.Invoke(this, e);
        }

        private async Task<string> GetLocalIPAddress()
        {
            List<CommsInterface> interfaces = await CommsInterface.GetAllInterfacesAsync();
            foreach(CommsInterface comms in interfaces)
            {
                if(comms.ConnectionStatus == Sockets.Plugin.Abstractions.CommsInterfaceStatus.Connected)
                {
                    return comms.IpAddress;
                }
            }

            //raise exception
            throw (new NoNetworkInterface("Unable to find an active network interface connection. Is this device connected to wifi?"));
        }
    }
}
