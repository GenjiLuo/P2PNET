﻿using P2PNET.FileLayer.EventArgs;
using P2PNET.ObjectLayer;
using P2PNET.ObjectLayer.EventArgs;
using P2PNET.TransportLayer;
using P2PNET.TransportLayer.EventArgs;
using PCLStorage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace P2PNET.FileLayer
{
    /// <summary>
    /// Class for sending and receiving files between peers.
    /// Built on top of <C>ObjectManager</C>
    /// </summary>
    public class FileManager
    {
        /// <summary>
        /// triggered when a file part has been sent or received sucessfully.
        /// </summary>
        public event EventHandler<FileTransferEventArgs> FileProgUpdate;

        /// <summary>
        /// Triggered when a message containing an object has been received
        /// </summary>
        public event EventHandler<ObjReceivedEventArgs> ObjReceived;

        /// <summary>
        /// Triggered when a new peer is detected or an existing peer becomes inactive
        /// </summary>
        public event EventHandler<PeerChangeEventArgs> PeerChange;

        /// <summary>
        /// Triggered when a whole file have been received from another peer
        /// </summary>
        public event EventHandler<FileReceivedEventArgs> FileReceived;

        public List<Peer> KnownPeers
        {
            get
            {
                return objManager.KnownPeers;
            }
        }

        private ObjectManager objManager;
        private IFileSystem fileSystem;
        private List<FileReceiveReq> receivedFiles;
        private List<FileSentReq> sentFiles;
        private TaskCompletionSource<bool> stillProcPrevMsg;

        /// <summary>
        /// Constructor that instantiates a file manager. To commence listening call the method <C>StartAsync</C>.
        /// </summary>
        /// <param name="mPortNum"> The port number which this peer will listen on and send messages with </param>
        /// <param name="mForwardAll"> When true, all messages received trigger a MsgReceived event. This includes UDB broadcasts that are reflected back to the local peer.</param>
        public FileManager(int portNum = 8080, bool mForwardAll = false)
        {
            this.receivedFiles = new List<FileReceiveReq>();
            this.sentFiles = new List<FileSentReq>();
            this.stillProcPrevMsg = new TaskCompletionSource<bool>();
            this.objManager = new ObjectManager(portNum, mForwardAll);
            this.fileSystem = FileSystem.Current;

            this.objManager.ObjReceived += ObjManager_objReceived;
            this.objManager.PeerChange += ObjManager_PeerChange;
        }

        private void ObjManager_PeerChange(object sender, PeerChangeEventArgs e)
        {
            this.PeerChange?.Invoke(this, e);
        }

        /// <summary>
        /// Peer will start actively listening on the specified port number.
        /// </summary>
        /// <returns></returns>
        public async Task StartAsync()
        {
            await objManager.StartAsync();
        }

        /// <summary>
        /// send file to the peer with the given IP address via a reliable TCP connection. 
        /// Works by breaking down the file into blocks each of length <C>bufferSize</C>. Each block is
        /// then compressed and sent one by one to the other peer. 
        /// </summary>
        /// <param name="ipAddress">The IP address of the peer to send the message to</param>
        /// <param name="filePath">The path to the file you want to send</param>
        /// <param name="bufferSize">
        /// Using a small buffer size will trigger <c>FileProgUpdate</c>> more but
        /// will also increase buffer overhead. Buffer size is also the max amount of memory
        /// a file will occupy in RAM.
        /// </param>
        /// <returns></returns>
        public async Task SendFileAsync(string ipAddress, string filePath, int bufferSize = 100 * 1024)
        {
            //create a file send request
            FileSentReq fileSent = await GetFileSendObj(ipAddress, filePath, bufferSize);

            //send first file part
            FilePartObj firstFilePart = await fileSent.GetNextFilePart();
            await objManager.SendAsyncTCP(ipAddress, firstFilePart);

            //update logging information
            FileProgUpdate?.Invoke(this, new FileTransferEventArgs(fileSent));

        }

        public async Task SendFileToAllAsync(string filePath, int bufferSize = 100 * 1024)
        {
            foreach( Peer peer in KnownPeers)
            {
                string targetIp = peer.IpAddress;
                await SendFileAsync(targetIp, filePath, bufferSize);
            }
        }


        private async Task<FileSentReq> GetFileSendObj(string ipAddress, string filePath, int bufferSize = 100 * 1024)
        {
            //TODO: handle folders as well as files

            //get file details
            IFile file = await fileSystem.GetFileFromPathAsync(filePath);

            //TODO: check if file is already open
            Stream fileStream;
            try
            {
                fileStream = await file.OpenAsync(FileAccess.Read);
            }
            catch
            {
                //can't find file
                throw new FileNotFound("Can't access the file: " + filePath);
            }
            //store away file details and the stream
            FilePartObj filePart = new FilePartObj(file, fileStream.Length, bufferSize);
            FileSentReq fileSend = new FileSentReq(filePart, fileStream, ipAddress);
            
            //store the fileSend object
            sentFiles.Add(fileSend);
            return fileSend;
        }

        private async void ObjManager_objReceived(object sender, ObjReceivedEventArgs e)
        {
            
            BObject bObj = e.Obj;
            Metadata metadata = bObj.GetMetadata();
            ObjReceived?.Invoke(this, e);

            string objType = bObj.GetType();
            switch (objType)
            {
                case "FilePartObj":
                    FilePartObj filePart = e.Obj.GetObject<FilePartObj>();
                    await ReceivedFilePart(filePart, metadata);
                    await SendAckBack(filePart, metadata);
                    break;
                case "AckMessage":
                    AckMessage ackMsg = e.Obj.GetObject<AckMessage>();
                    await ProcessAckMessage(ackMsg, metadata);
                    break;
                default:
                    break;
            } 
        }

        //called when a file part is received
        private async Task ReceivedFilePart(FilePartObj filePart, Metadata metadata)
        {
            //check if file part is valid
            if (filePart == null)
            {
                throw new Exception("filePart has not been set.");
            }

            //check if is for a new file
            if( filePart.FilePartNum == 1)
            {
                //new file being received
                FileReceiveReq newFileReceived = await NewFileInit(filePart, metadata);
                receivedFiles.Add(newFileReceived);
                FileReceived?.Invoke(this, new FileReceivedEventArgs());
            }

            //find correct file to write to
            FileReceiveReq fileReceived = GetFileReceivedFromFilePart(filePart, metadata);

            await fileReceived.WriteFilePartToFile(filePart);

            //log incoming file
            FileProgUpdate?.Invoke(this, new FileTransferEventArgs(fileReceived));

            //if last file part then close stream
            if (filePart.FilePartNum == filePart.TotalPartNum)
            {
                await fileReceived.CloseStream();
            }
        }

        //received Ack from another peer
        private async Task ProcessAckMessage(AckMessage ackMsg, Metadata metadata)
        {
            //get file send request info
            FileSentReq fileSent = GetSendFileFromAck(ackMsg, metadata);
            string targetIp = fileSent.TargetIpAddress;

            //send next file part
            FilePartObj nextFilePart = await fileSent.GetNextFilePart();
            if (nextFilePart == null)
            {
                return;
            }
            await objManager.SendAsyncTCP(targetIp, nextFilePart);

            //update logging information
            FileProgUpdate?.Invoke(this, new FileTransferEventArgs(fileSent));
        }

        //find a match based on remote ip, file name and file path
        private FileSentReq GetSendFileFromAck(AckMessage ackMsg, Metadata metadata)
        {
            //find corresponding sentFiles
            foreach (FileSentReq fileSent in sentFiles)
            {
                if (fileSent.TargetIpAddress == metadata.SourceIp && fileSent.FilePart.FileName == ackMsg.FileName && fileSent.FilePart.FilePath == ackMsg.FilePath)
                {
                    return fileSent;
                }   
            }
            //can't find coresponding file
            throw new FileNotFound("Recieved an Ack but can't find file in sent storage.");
        }

        private async Task SendAckBack(FilePartObj filePart, Metadata metadata)
        {
            //send message back to sender
            string targetIp = metadata.SourceIp;
            AckMessage ackMsg = new AckMessage(filePart);
            await objManager.SendAsyncTCP(targetIp, ackMsg);
        }

        
        private async Task<FileReceiveReq> NewFileInit(FilePartObj filePart, Metadata metadata)
        {
            //create a folder to store the file
            IFolder root = await fileSystem.GetFolderFromPathAsync("./");
            if (await root.CheckExistsAsync("./temp/") == ExistenceCheckResult.NotFound)
            {
                //create folder
                await root.CreateFolderAsync("temp", CreationCollisionOption.FailIfExists);
            }
            IFolder tempFolder = await fileSystem.GetFolderFromPathAsync("./temp");

            //create the file
            IFile newFile = await tempFolder.CreateFileAsync(filePart.FileName, CreationCollisionOption.ReplaceExisting);
            Stream fileStream = await newFile.OpenAsync(FileAccess.ReadAndWrite);

            //store as a received file
            FileReceiveReq fileReceived = new FileReceiveReq(filePart, fileStream, metadata.SourceIp);
            return fileReceived;
        }

        private FileReceiveReq GetFileReceivedFromFilePart(FilePartObj filePart, Metadata metadata)
        {
            foreach (FileReceiveReq receivedFile in this.receivedFiles)
            {
                if (receivedFile.TargetIpAddress == metadata.SourceIp && receivedFile.FilePart.FileName == filePart.FileName && receivedFile.FilePart.FilePath == filePart.FilePath)
                {
                    return receivedFile;
                }
            }

            //can't find coresponding file
            throw new FileNotFound("Recieved an file part but can't find file in received storage.");
        }
    }
}
